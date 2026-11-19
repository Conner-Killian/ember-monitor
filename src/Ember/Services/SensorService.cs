using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace Ember.Services;

/// <summary>
/// Owns the LibreHardwareMonitor Computer instance and polls it on a background thread.
/// Snapshots are marshalled to the UI dispatcher via <see cref="Updated"/>.
/// </summary>
public sealed class SensorService : IDisposable
{
    public const int HistoryLength = 300;

    private readonly Dispatcher _ui;
    private Thread? _thread;
    private volatile bool _running;
    private volatile int _intervalMs = 1000;
    private readonly AutoResetEvent _wake = new(false);
    private Computer? _computer;
    private readonly Dictionary<string, DriveMedia> _mediaByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _histLock = new();
    private readonly Dictionary<string, History> _history = new();
    private readonly Dictionary<string, (double min, double max)> _minMax = new();
    private readonly WindowsCounters _counters = new();
    private readonly HashSet<string> _derivedSeen = new();

    public event Action<SensorSnapshot>? Updated;
    public event Action<string>? Status;

    public SensorSnapshot? Latest { get; private set; }
    public bool IsReady { get; private set; }
    public bool DriverInstalled { get; private set; } = true;
    /// <summary>Set if the sensor library failed to start (shown instead of "Starting…").</summary>
    public string? FailureMessage { get; private set; }

    public SensorService(Dispatcher ui) => _ui = ui;

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = Math.Clamp(value, 250, 10000); _wake.Set(); }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Ember.Sensors", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public double[] GetHistory(string sensorId)
    {
        lock (_histLock)
            return _history.TryGetValue(sensorId, out var h) ? h.ToArray() : Array.Empty<double>();
    }

    public (double? min, double? max) GetMinMax(string sensorId)
    {
        lock (_histLock)
            return _minMax.TryGetValue(sensorId, out var mm) ? (mm.min, mm.max) : (null, null);
    }

    public void ResetMinMax()
    {
        lock (_histLock) _minMax.Clear();
    }

    // ------------------------------------------------------------------

    private void Loop()
    {
        try
        {
            try { DriverInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { DriverInstalled = false; }
            LoadDriveMediaTypes();

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
            };
            _computer.Open();
            IsReady = true;
        }
        catch (Exception ex)
        {
            FailureMessage = "Couldn't read sensors: " + ex.Message;
            Post(() => Status?.Invoke(FailureMessage));
            return;
        }

        var sw = new Stopwatch();
        while (_running)
        {
            sw.Restart();
            try
            {
                var snap = Poll();
                Latest = snap;
                Post(() => Updated?.Invoke(snap));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Poll failed: " + ex);
            }
            PollThreadCpuMs = ThreadCpuMs();
            var wait = _intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0) _wake.WaitOne(wait);
        }
    }

    private void Post(Action a)
    {
        if (!_ui.HasShutdownStarted) _ui.BeginInvoke(a, DispatcherPriority.Background);
    }

    private SensorSnapshot Poll()
    {
        var devices = new List<DeviceSnapshot>();
        _tick++;
        _counters.Collect();
        foreach (var hw in _computer!.Hardware)
        {
            if (ShouldUpdate(hw))
            {
                var t0 = Stopwatch.GetTimestamp();
                try { hw.Update(); } catch { }
                foreach (var sub in hw.SubHardware) { try { sub.Update(); } catch { } }
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var key = hw.HardwareType + " " + hw.Name;
                UpdateCostMs[key] = UpdateCostMs.TryGetValue(key, out var prev) ? prev * 0.8 + ms * 0.2 : ms;
            }
            Collect(hw, devices, hw.HardwareType);
        }

        var cpu = devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        var gpus = devices.Where(d => d.Kind == DeviceKind.Gpu).ToList();
        var drives = devices.Where(d => d.Kind == DeviceKind.Storage).ToList();
        var memDevices = devices.Where(d => d.Kind == DeviceKind.Memory).ToList();
        var ramTemps = memDevices.SelectMany(d => d.Sensors)
                                 .Where(s => s.Type == "Temperature" && s.Value.HasValue)
                                 .ToList();
        // Physical RAM ("/ram", "Total Memory") is what Task Manager reports. "/vram" ("Virtual Memory")
        // is the commit charge (RAM + page file) and runs much higher, so never pick it for usage.
        var memory = memDevices.FirstOrDefault(d => d.Id == "/ram")
                     ?? memDevices.FirstOrDefault(d => d.Id != "/vram" && d.Sensors.Any(s => s.Type == "Load"))
                     ?? memDevices.FirstOrDefault(d => d.Id != "/vram")
                     ?? memDevices.FirstOrDefault();
        var mobo = devices.FirstOrDefault(d => d.Kind == DeviceKind.Motherboard && d.Sensors.Count > 0)
                   ?? devices.FirstOrDefault(d => d.Kind == DeviceKind.Motherboard);

        // Primary GPU (user choice, else discrete preferred, else whatever has a temp)
        var pref = App.Settings.PrimaryGpuId;
        var gpu = (pref != null ? gpus.FirstOrDefault(g => g.Id == pref) : null)
                  ?? gpus.Where(g => !g.IsIntegratedGpu && g.PrimaryTemp != null)
                         .OrderBy(g => g.HardwareType == "GpuNvidia" ? 0 : g.HardwareType == "GpuAmd" ? 1 : 2)
                         .FirstOrDefault()
                  ?? gpus.FirstOrDefault(g => g.PrimaryTemp != null)
                  ?? gpus.FirstOrDefault();
        _primaryGpuId = gpu?.Id;

        // Record history for every temperature / load / power sensor
        lock (_histLock)
        {
            foreach (var d in devices)
                foreach (var s in d.Sensors)
                {
                    if (s.Value is not double v || double.IsNaN(v)) continue;
                    // Session min/max for every sensor (dashboard and All sensors page both use it,
                    // so "Reset min/max" clears them everywhere).
                    _minMax[s.Id] = _minMax.TryGetValue(s.Id, out var mm)
                        ? (Math.Min(mm.min, v), Math.Max(mm.max, v))
                        : (v, v);
                    if (s.Type is not ("Temperature" or "Load" or "Power" or "Fan")) continue;
                    if (!_history.TryGetValue(s.Id, out var h)) _history[s.Id] = h = new History(HistoryLength);
                    h.Add(v);
                }
        }

        return new SensorSnapshot
        {
            Devices = devices,
            Cpu = cpu,
            Gpu = gpu,
            Gpus = gpus,
            Drives = drives,
            RamTemps = ramTemps,
            Memory = memory,
            Motherboard = mobo,
        };
    }

    private void Collect(IHardware hw, List<DeviceSnapshot> into, HardwareType rootType)
    {
        var sensors = hw.Sensors
            .Where(s => !s.IsDefaultHidden && !IsLimitSensor(s))
            .Select(s => new SensorReading(
                s.Identifier.ToString(), s.Name, s.SensorType.ToString(),
                Clean(s.Value), Clean(s.Min), Clean(s.Max)))
            .ToList();

        var kind = hw.HardwareType switch
        {
            HardwareType.Cpu => DeviceKind.Cpu,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => DeviceKind.Gpu,
            HardwareType.Memory => DeviceKind.Memory,
            HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => DeviceKind.Motherboard,
            HardwareType.Storage => DeviceKind.Storage,
            _ => DeviceKind.Other,
        };

        // Headline numbers must mean what Task Manager means, so a few are derived here rather than
        // taken from whichever LHM sensor happens to exist. Derived readings are added to the device's
        // sensor list so they get history, min/max and show on the All sensors page.
        // Rules: Load/Power never fall back to an arbitrary sensor of the same type (that's how a
        // memory-controller load could end up labelled "GPU usage").
        SensorReading? cpuUsage = null, cpuSpeed = null, gpuUsage = null;
        if (kind == DeviceKind.Cpu && hw.Identifier.ToString().EndsWith("/0"))
        {
            // Task Manager: "% Processor Utility" (accounts for boost) and base clock × "% Processor Performance".
            cpuUsage = Derived(sensors, hw, "usage", "CPU Usage", "Load", _counters.CpuUsage);
            cpuSpeed = Derived(sensors, hw, "speed", "CPU Speed", "Clock", _counters.CpuSpeedMhz);
        }
        else if (kind == DeviceKind.Gpu && sensors.Any(s => s.Type == "Load" && s.Name.StartsWith("D3D ")))
        {
            // Task Manager: the busiest engine (3D, Copy, Video Decode/Encode, Compute…).
            // The vendor "GPU Core" load (NVML/ADL) is a different metric and reads much lower.
            var engines = sensors.Where(s => s.Type == "Load" && s.Name.StartsWith("D3D ") && s.Value.HasValue)
                                 .Select(s => s.Value!.Value).ToList();
            gpuUsage = Derived(sensors, hw, "usage", "GPU Usage", "Load", engines.Count > 0 ? Math.Min(100, engines.Max()) : null);
        }

        var snap = new DeviceSnapshot(hw.Identifier.ToString(), StableName(hw), kind, hw.HardwareType.ToString(), sensors);
        snap = kind switch
        {
            DeviceKind.Cpu => snap with
            {
                // Tdie before Tctl: older Ryzen (1000/2000 X, Threadripper) report Tctl with a +10–27 °C offset.
                PrimaryTemp = Pick(sensors, "Temperature", "Core (Tctl/Tdie)", "Core (Tdie)", "CPU Package", "Core (Tctl)", "Core Max", "Core Average", "CPU Cores"),
                SecondaryTemp = PickExact(sensors, "Temperature", "CCDs Max (Tdie)", "CCD1 (Tdie)", "Core Max"),
                Load = cpuUsage?.Value != null ? cpuUsage : PickExact(sensors, "Load", "CPU Total") ?? cpuUsage,
                Power = PickExact(sensors, "Power", "Package", "CPU Package"),
                Clock = cpuSpeed?.Value != null ? cpuSpeed
                      : PickExact(sensors, "Clock", "Cores (Average)")
                        ?? MaxOf(sensors, "Clock", n => n.StartsWith("Core #") && !n.Contains("Effective")),
            },
            DeviceKind.Gpu => snap with
            {
                PrimaryTemp = Pick(sensors, "Temperature", "GPU Core", "GPU Temperature", "GPU VR SoC"),
                SecondaryTemp = PickExact(sensors, "Temperature", "GPU Hot Spot", "GPU Memory Junction"),
                Load = gpuUsage ?? PickExact(sensors, "Load", "GPU Core"),
                Power = PickExact(sensors, "Power", "GPU Package", "GPU Power", "GPU Board Power", "GPU Core", "GPU SoC"),
                Clock = PickExact(sensors, "Clock", "GPU Core"),
                Fan = Pick(sensors, "Fan", "GPU Fan", "GPU Fan 1"),
                IsIntegratedGpu = IsIntegrated(hw),
            },
            DeviceKind.Storage => snap with
            {
                PrimaryTemp = Pick(sensors, "Temperature", "Temperature", "Composite Temperature", "Temperature 1"),
                Load = PickExact(sensors, "Load", "Used Space"),
                Media = GuessMedia(hw.Name),
            },
            DeviceKind.Memory => snap with
            {
                PrimaryTemp = sensors.FirstOrDefault(s => s.Type == "Temperature" && s.Value.HasValue),
                Load = PickExact(sensors, "Load", "Memory"),
            },
            _ => snap,
        };

        into.Add(snap);
        foreach (var sub in hw.SubHardware)
        {
            // Sub-hardware (SuperIO chips, DIMMs) are listed as their own devices so nothing is hidden.
            Collect(sub, into, rootType);
        }
    }

    /// <summary>
    /// Static spec values exposed as "temperature" sensors (DIMM SPD limits, NVMe warning/critical
    /// thresholds, sensor resolution). They aren't live readings, so hide them everywhere.
    /// </summary>
    private static bool IsLimitSensor(ISensor s)
    {
        if (s.SensorType != SensorType.Temperature) return false;
        var n = s.Name;
        return n.Contains("Limit", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Resolution", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase);
    }

    private long _tick;
    private readonly Dictionary<string, string> _names = new();

    /// <summary>
    /// DIMM names come from SPD over SMBus and can occasionally read back corrupted.
    /// Remember the first clean name per device and reuse it.
    /// </summary>
    private string StableName(IHardware hw)
    {
        var id = hw.Identifier.ToString();
        var name = hw.Name.Trim();
        bool clean = name.Length > 0 && name.All(c => c >= 0x20 && c < 0x7F) && !name.Contains("?>") && !name.Contains('}');
        if (clean)
        {
            if (!_names.ContainsKey(id)) _names[id] = name;
            return _names[id];
        }
        if (_names.TryGetValue(id, out var known)) return known;
        if (hw.HardwareType != HardwareType.Memory) return "Device";

        // Corrupted DIMM name: borrow the model from a sibling stick (kits are usually identical)
        // and add this stick's slot number from its identifier (/memory/dimm/<n>).
        var slot = id.Split('/').LastOrDefault() ?? "?";
        foreach (var other in _names.Values)
        {
            var m = System.Text.RegularExpressions.Regex.Match(other, @"^(.*) \(#\d+\)$");
            if (m.Success) return $"{m.Groups[1].Value} (#{slot})";
        }
        return $"Memory module (#{slot})";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    /// <summary>CPU time consumed by the calling thread, in ms (diagnostics).</summary>
    public static double ThreadCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var k, out var u);
        return (k + u) / 10000.0;
    }

    /// <summary>Total CPU time used by the polling thread so far (diagnostics).</summary>
    public double PollThreadCpuMs { get; private set; }

    /// <summary>Rolling average update cost per device (diagnostics).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> UpdateCostMs = new();

    /// <summary>
    /// Drive (SMART) temperatures change slowly and are comparatively costly to read,
    /// so storage is polled at most every ~3 seconds.
    /// </summary>
    private bool ShouldUpdate(IHardware hw)
    {
        if (_tick <= 2) return true;
        long Every(int ms) => Math.Max(1, ms / Math.Max(250, _intervalMs));
        bool Due(int ms) => _tick % Every(ms) == 0;
        var s = App.Settings;
        string id = hw.Identifier.ToString();

        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:
                return true;
            case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                bool primary = id == _primaryGpuId;
                if (primary) return !LowPower || s.OverlayShowGpu || Due(3000);
                return Due(LowPower ? 10000 : 2000);
            case HardwareType.Memory:
                return !LowPower || s.OverlayShowRam || Due(5000);
            case HardwareType.Storage:
                // SMART reads are slow (esp. spinning disks) and drive temps change slowly.
                return Due(LowPower && !s.OverlayShowDrives ? 10000 : 3000);
            default:
                return Due(LowPower ? 5000 : 1000);
        }
    }

    private volatile string? _primaryGpuId;

    /// <summary>
    /// When true (main window hidden), only sensors needed by the overlay / tray are polled every tick;
    /// everything else is refreshed every few seconds so alerts keep working.
    /// </summary>
    public volatile bool LowPower;

    private static bool IsIntegrated(IHardware hw)
    {
        if (hw.HardwareType == HardwareType.GpuIntel)
            return !hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                   hw.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);
        if (hw.HardwareType == HardwareType.GpuAmd)
        {
            var n = hw.Name;
            // Ryzen iGPUs report as "AMD Radeon(TM) Graphics" / "Radeon 780M" etc.
            return n.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase) ||
                   System.Text.RegularExpressions.Regex.IsMatch(n, @"\b\d{3}M\b");
        }
        return false;
    }

    private static double? Clean(float? v) =>
        v is float f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;

    private static SensorReading? Pick(List<SensorReading> sensors, string type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = sensors.FirstOrDefault(x => x.Type == type && x.Value.HasValue &&
                                                 string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s;
        }
        return sensors.FirstOrDefault(x => x.Type == type && x.Value.HasValue);
    }

    /// <summary>Like <see cref="Pick"/> but never falls back to an unrelated sensor of the same type.</summary>
    private static SensorReading? PickExact(List<SensorReading> sensors, string type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = sensors.FirstOrDefault(x => x.Type == type && x.Value.HasValue &&
                                                 string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>Adds a reading Ember computes itself to the device's sensor list.</summary>
    private SensorReading Derived(List<SensorReading> sensors, IHardware hw, string key, string name, string type, double? value)
    {
        var id = hw.Identifier + "/ember/" + key;
        var r = new SensorReading(id, name, type, value, null, null);
        // Don't list a reading that has never had a value (e.g. performance counters disabled on this PC).
        if (value != null) _derivedSeen.Add(id);
        if (_derivedSeen.Contains(id)) sensors.Insert(0, r);
        return r;
    }

    private static SensorReading? MaxOf(List<SensorReading> sensors, string type, Func<string, bool> filter) =>
        sensors.Where(x => x.Type == type && x.Value.HasValue && filter(x.Name))
               .OrderByDescending(x => x.Value)
               .FirstOrDefault()
        ?? sensors.FirstOrDefault(x => x.Type == type && x.Value.HasValue);

    // ---- SSD vs HDD detection via Windows Storage WMI ----
    private void LoadDriveMediaTypes()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = (mo["FriendlyName"] as string)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var media = Convert.ToInt32(mo["MediaType"] ?? 0);
                var bus = Convert.ToInt32(mo["BusType"] ?? 0);
                // MediaType: 3 = HDD, 4 = SSD. BusType 17 = NVMe
                _mediaByName[name] = media == 3 && bus != 17 ? DriveMedia.Hdd : DriveMedia.Ssd;
            }
        }
        catch { }
    }

    private DriveMedia GuessMedia(string name)
    {
        name = name.Trim();
        if (_mediaByName.TryGetValue(name, out var m)) return m;
        foreach (var kv in _mediaByName)
            if (kv.Key.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        // Heuristics: Seagate ST.., WD ..EZ.., Toshiba HD.. are typically spinning disks
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(ST\d|WDC WD\d|TOSHIBA (HD|DT|MG))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return DriveMedia.Hdd;
        return DriveMedia.Ssd;
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        try { _thread?.Join(2000); } catch { }
        try { _computer?.Close(); } catch { }
        _counters.Dispose();
    }
}
