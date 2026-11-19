using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using Ember.Services.Linux;

namespace Ember.Services;

/// <summary>What Ember noticed is missing on this system, shown as tips on the Settings page.</summary>
public sealed class SensorHints
{
    public bool NvidiaWithoutNvml { get; set; }     // NVIDIA card but no libnvidia-ml (nouveau or driver utils missing)
    public bool SataWithoutDrivetemp { get; set; }  // SATA drives present, drivetemp module not loaded
    public bool CpuPowerDenied { get; set; }        // RAPL energy counter is root-only
    public bool NoCpuTemperature { get; set; }      // no k10temp / coretemp / zenpower
    public bool NoBoardSensors { get; set; }        // no Super I/O driver (e.g. nct6687 on MSI boards)
    public bool NoDimmSensors { get; set; }         // no spd5118 / jc42
    public bool Any => NvidiaWithoutNvml || SataWithoutDrivetemp || CpuPowerDenied || NoCpuTemperature || NoBoardSensors || NoDimmSensors;
}

/// <summary>
/// Linux sensor polling. Same public surface as the Windows SensorService so the shared view-model
/// code works unchanged. Sources, none of which need root:
///   • /sys/class/hwmon  – CPU (k10temp, coretemp, zenpower), AMD/Intel/nouveau GPUs, NVMe and SATA
///                          (drivetemp) drives, DDR5/DDR4 DIMMs (spd5118, jc42), Super I/O board chips
///   • NVML              – NVIDIA cards on the proprietary driver
///   • /proc/stat, /proc/meminfo, cpufreq, RAPL (powercap) – CPU usage, RAM, CPU clock and power
/// Off Linux (or with EMBER_DEMO=1) it produces realistic demo readings, for UI work and screenshots.
/// </summary>
public sealed class SensorService : IDisposable
{
    public const int HistoryLength = 300;

    private Thread? _thread;
    private volatile bool _running;
    private volatile int _intervalMs = 1000;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _histLock = new();
    private readonly Dictionary<string, History> _history = new();
    private readonly Dictionary<string, (double min, double max)> _minMax = new();

    public event Action<SensorSnapshot>? Updated;
    public event Action<string>? Status;

    public SensorSnapshot? Latest { get; private set; }
    /// <summary>Linux needs no extra driver (kept for the shared dashboard code).</summary>
    public bool DriverInstalled => true;
    public string? FailureMessage { get; private set; }
    public SensorHints Hints { get; private set; } = new();
    public bool IsDemo { get; } = !OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("EMBER_DEMO") == "1";
    public volatile bool LowPower;

    public readonly ConcurrentDictionary<string, double> UpdateCostMs = new();

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

    public double[] GetHistory(string id)
    {
        lock (_histLock) return _history.TryGetValue(id, out var h) ? h.ToArray() : Array.Empty<double>();
    }

    public (double? min, double? max) GetMinMax(string id)
    {
        lock (_histLock) return _minMax.TryGetValue(id, out var mm) ? (mm.min, mm.max) : (null, null);
    }

    public void ResetMinMax() { lock (_histLock) _minMax.Clear(); }

    private void Post(Action a) => Dispatcher.UIThread.Post(a, DispatcherPriority.Background);

    private void Loop()
    {
        try
        {
            if (!IsDemo) Init();
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
                var devices = IsDemo ? Demo.Devices(_tick++) : Poll();
                var snap = Build(devices);
                Latest = snap;
                Post(() => Updated?.Invoke(snap));
            }
            catch (Exception ex) { Debug.WriteLine("Poll failed: " + ex); }
            var wait = _intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0) _wake.WaitOne(wait);
        }
    }

    // ------------------------------------------------------------------ snapshot assembly

    private string? _primaryGpuId;

    private SensorSnapshot Build(List<DeviceSnapshot> devices)
    {
        var cpu = devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        var gpus = devices.Where(d => d.Kind == DeviceKind.Gpu).ToList();
        var drives = devices.Where(d => d.Kind == DeviceKind.Storage).ToList();
        var mem = devices.Where(d => d.Kind == DeviceKind.Memory).ToList();
        var ramTemps = mem.SelectMany(d => d.Sensors).Where(s => s.Type == "Temperature" && s.Value.HasValue).ToList();
        var memory = mem.FirstOrDefault(d => d.Id == "/ram");
        var board = devices.FirstOrDefault(d => d.Kind == DeviceKind.Motherboard);

        var pref = App.Settings.PrimaryGpuId;
        var gpu = (pref != null ? gpus.FirstOrDefault(g => g.Id == pref) : null)
                  ?? gpus.Where(g => !g.IsIntegratedGpu && g.PrimaryTemp != null)
                         .OrderBy(g => g.HardwareType == "GpuNvidia" ? 0 : g.HardwareType == "GpuAmd" ? 1 : 2).FirstOrDefault()
                  ?? gpus.FirstOrDefault(g => g.PrimaryTemp != null)
                  ?? gpus.FirstOrDefault();
        _primaryGpuId = gpu?.Id;

        lock (_histLock)
        {
            foreach (var d in devices)
                foreach (var s in d.Sensors)
                {
                    if (s.Value is not double v || double.IsNaN(v)) continue;
                    _minMax[s.Id] = _minMax.TryGetValue(s.Id, out var mm) ? (Math.Min(mm.min, v), Math.Max(mm.max, v)) : (v, v);
                    if (s.Type is not ("Temperature" or "Load" or "Power" or "Fan")) continue;
                    if (!_history.TryGetValue(s.Id, out var h)) _history[s.Id] = h = new History(HistoryLength);
                    h.Add(v);
                }
        }

        return new SensorSnapshot
        {
            Devices = devices, Cpu = cpu, Gpu = gpu, Gpus = gpus, Drives = drives,
            RamTemps = ramTemps, Memory = memory, Motherboard = board,
        };
    }

    // ------------------------------------------------------------------ Linux sources

    private long _tick;
    private List<HwmonChip> _chips = new();
    private DateTime _lastScan;
    private Nvml? _nvml;
    private readonly List<GpuInfo> _gpus = new();
    private readonly List<DriveInfoX> _drives = new();
    private string[] _freqFiles = Array.Empty<string>();
    private string? _raplFile; private double _raplRange;
    private (double uj, long ts)? _raplPrev;
    private Dictionary<string, ulong[]> _prevStat = new();
    private Dictionary<string, (double total, double free)> _usage = new();
    private DateTime _lastUsage;
    private string _cpuName = "Processor";
    private readonly Dictionary<string, List<DeviceSnapshot>> _cache = new();
    private readonly Dictionary<string, long> _cacheTick = new();

    private sealed record GpuInfo(string Slot, string Vendor, string Driver, string DevDir, HwmonChip? Hwmon, Nvml.Gpu? NvGpu, string Name, bool Integrated);
    private sealed record DriveInfoX(HwmonChip Chip, string Id, string Name, string[] Blocks, bool Rotational, bool Nvme);

    private void Init()
    {
        _cpuName = ReadCpuName();
        _nvml = Nvml.TryLoad();
        Rescan();
        // RAPL package energy counter (Intel, and AMD Zen via the same powercap driver).
        foreach (var zone in SafeDirs("/sys/class/powercap"))
        {
            if (SysFs.ReadText(Path.Combine(zone, "name"))?.Trim() != "package-0") continue;
            _raplFile = Path.Combine(zone, "energy_uj");
            _raplRange = SysFs.ReadDouble(Path.Combine(zone, "max_energy_range_uj")) ?? 0;
            break;
        }
    }

    private void Rescan()
    {
        _chips = HwmonChip.Scan();
        _lastScan = DateTime.UtcNow;
        _freqFiles = SafeDirs("/sys/devices/system/cpu")
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^cpu\d+$"))
            .Select(d => Path.Combine(d, "cpufreq", "scaling_cur_freq"))
            .Where(File.Exists).ToArray();

        // ---- graphics cards ----
        _gpus.Clear();
        var seen = new HashSet<string>();
        foreach (var card in SafeDirs("/sys/class/drm").Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^card\d+$")))
        {
            var dev = SysFs.RealPath(Path.Combine(card, "device"));
            if (dev == null || !seen.Add(dev)) continue;
            var cls = SysFs.ReadText(Path.Combine(dev, "class"))?.Trim() ?? "";
            if (!cls.StartsWith("0x03")) continue;
            var vendor = SysFs.ReadText(Path.Combine(dev, "vendor"))?.Trim() ?? "";
            var device = SysFs.ReadText(Path.Combine(dev, "device"))?.Trim() ?? "";
            var driver = SysFs.LinkName(Path.Combine(dev, "driver")) ?? "";
            var slot = Path.GetFileName(dev);
            var hw = _chips.FirstOrDefault(c => c.DevicePath == dev);
            var nv = vendor == "0x10de" ? _nvml?.Gpus.FirstOrDefault(g => string.Equals(g.PciSlot, slot, StringComparison.OrdinalIgnoreCase)) : null;
            string name;
            if (nv != null) name = nv.Name;
            else
            {
                var product = SysFs.ReadText(Path.Combine(dev, "product_name"))?.Trim();
                name = !string.IsNullOrEmpty(product) ? product : PciIds.GpuName(vendor, device);
            }
            bool integrated = vendor switch
            {
                "0x1002" => (SysFs.ReadDouble(Path.Combine(dev, "mem_info_vram_total")) ?? 0) < 2.0 * 1024 * 1024 * 1024,
                "0x8086" => !name.Contains("Arc", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            _gpus.Add(new GpuInfo(slot, vendor, driver, dev, hw, nv, name, integrated));
        }
        // NVML cards without a DRM node (e.g. nvidia-drm not loaded)
        if (_nvml != null)
            foreach (var g in _nvml.Gpus.Where(g => _gpus.All(x => x.NvGpu != g)))
                _gpus.Add(new GpuInfo(g.PciSlot, "0x10de", "nvidia", "", null, g, g.Name, false));

        // ---- drives ----
        _drives.Clear();
        foreach (var c in _chips.Where(c => c.Name is "nvme" or "drivetemp" && c.DevicePath != null))
        {
            string dp = c.DevicePath!;
            if (c.Name == "nvme")
            {
                // Usually the hwmon parent is the controller (…/nvme/nvme0); on some kernels it's the
                // PCI device, with the controller one level down.
                if (!File.Exists(Path.Combine(dp, "model")) && SafeDirs(Path.Combine(dp, "nvme")).FirstOrDefault() is string sub) dp = sub;
                var ctrl = Path.GetFileName(dp);
                var model = SysFs.ReadText(Path.Combine(dp, "model"))?.Trim() ?? "NVMe SSD";
                var serial = SysFs.ReadText(Path.Combine(dp, "serial"))?.Trim();
                var blocks = SafeDirs("/sys/block").Select(Path.GetFileName).Where(b => b != null && b.StartsWith(ctrl + "n")).Cast<string>().ToArray();
                _drives.Add(new DriveInfoX(c, "/nvme/" + (string.IsNullOrEmpty(serial) ? ctrl : serial), model, blocks, false, true));
            }
            else
            {
                var vendorName = SysFs.ReadText(Path.Combine(dp, "vendor"))?.Trim() ?? "";
                var model = SysFs.ReadText(Path.Combine(dp, "model"))?.Trim() ?? "Drive";
                if (vendorName.Length > 0 && vendorName != "ATA" && !model.StartsWith(vendorName)) model = vendorName + " " + model;
                var blocks = SafeDirs(Path.Combine(dp, "block")).Select(Path.GetFileName).Cast<string>().ToArray();
                bool rot = blocks.Any(b => SysFs.ReadText($"/sys/block/{b}/queue/rotational")?.Trim() == "1");
                _drives.Add(new DriveInfoX(c, "/ata/" + model.Replace(' ', '_') + "/" + string.Join("+", blocks), model, blocks, rot, false));
            }
        }

        // ---- hints ----
        var h = new SensorHints
        {
            NvidiaWithoutNvml = _gpus.Any(g => g.Vendor == "0x10de" && g.NvGpu == null),
            NoCpuTemperature = !_chips.Any(c => IsCpuChip(c.Name)),
            NoBoardSensors = !_chips.Any(c => BoardChip(c.Name)),
            NoDimmSensors = !_chips.Any(c => c.Name is "spd5118" or "jc42"),
            CpuPowerDenied = Hints.CpuPowerDenied,
        };
        // SATA disks exist (rotational or not) but drivetemp isn't providing temperatures.
        bool sata = SafeDirs("/sys/block").Any(b => Path.GetFileName(b).StartsWith("sd") &&
                        (SysFs.RealPath(b)?.Contains("/ata") ?? false));
        h.SataWithoutDrivetemp = sata && !_chips.Any(c => c.Name == "drivetemp");
        Hints = h;
    }

    private List<DeviceSnapshot> Poll()
    {
        _tick++;
        if ((DateTime.UtcNow - _lastScan).TotalSeconds > 60) Rescan();
        var s = App.Settings;
        long Every(int ms) => Math.Max(1, ms / Math.Max(250, _intervalMs));
        bool Due(string key, int ms)
        {
            if (_tick <= 2 || !_cache.ContainsKey(key)) return true;
            return _tick - _cacheTick[key] >= Every(ms);
        }
        List<DeviceSnapshot> Group(string key, int everyMs, Func<List<DeviceSnapshot>> read)
        {
            if (Due(key, everyMs))
            {
                var t0 = Stopwatch.GetTimestamp();
                try { _cache[key] = read(); } catch (Exception ex) { Debug.WriteLine(key + ": " + ex); _cache.TryAdd(key, new()); }
                _cacheTick[key] = _tick;
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                UpdateCostMs[key] = UpdateCostMs.TryGetValue(key, out var prev) ? prev * 0.8 + ms * 0.2 : ms;
            }
            return _cache[key];
        }

        var all = new List<DeviceSnapshot>();
        all.AddRange(Group("board", LowPower ? 5000 : 1000, ReadBoardAndOther));
        all.AddRange(Group("cpu", 0, () => new() { ReadCpu() }));
        all.AddRange(Group("memory", LowPower && !s.OverlayShowRam ? 5000 : 0, ReadMemory));
        foreach (var g in _gpus)
        {
            bool primary = ("/gpu/" + g.Slot) == _primaryGpuId;
            int every = primary ? (LowPower && !s.OverlayShowGpu ? 3000 : 0) : (LowPower ? 10000 : 2000);
            all.AddRange(Group("gpu:" + g.Slot, every, () => new() { ReadGpu(g) }));
        }
        all.AddRange(Group("drives", LowPower && !s.OverlayShowDrives ? 10000 : 3000, ReadDrives));
        return all;
    }

    // ---- CPU ----

    private static bool IsCpuChip(string n) => n is "k10temp" or "coretemp" or "zenpower" or "k8temp" or "cpu_thermal" or "via_cputemp";

    private DeviceSnapshot ReadCpu()
    {
        var sensors = new List<SensorReading>();
        int idx = 0;
        var ccd = new List<double>(); var cores = new List<double>();
        foreach (var c in _chips.Where(c => IsCpuChip(c.Name)))
        {
            bool hasTdie = c.Inputs.Any(i => i.Label == "Tdie");
            foreach (var i in c.Inputs.Where(i => i.Type == "Temperature"))
            {
                // Match the Windows names so both apps label things the same way.
                string name = i.Label switch
                {
                    "Tctl" => hasTdie ? "Core (Tctl)" : "Core (Tctl/Tdie)",
                    "Tdie" => "Core (Tdie)",
                    "Package id 0" => "CPU Package",
                    var l when l.StartsWith("Tccd") => $"CCD{l[4..]} (Tdie)",
                    var l => l,
                };
                var v = i.Read();
                if (i.Label.StartsWith("Tccd") && v is double cv) ccd.Add(cv);
                if (i.Label.StartsWith("Core ") && v is double kv) cores.Add(kv);
                sensors.Add(new SensorReading($"/cpu/{c.Name}{idx}/{i.Key}", name, "Temperature", v, null, null));
            }
            idx++;
        }
        if (ccd.Count > 1) sensors.Add(new SensorReading("/cpu/ccdmax", "CCDs Max (Tdie)", "Temperature", ccd.Max(), null, null));
        if (cores.Count > 0) sensors.Add(new SensorReading("/cpu/coremax", "Core Max", "Temperature", cores.Max(), null, null));

        // Usage from /proc/stat (busy = everything except idle and iowait), like top / GNOME System Monitor.
        var stat = ReadProcStat();
        double? total = null;
        foreach (var (name, vals) in stat)
        {
            if (!_prevStat.TryGetValue(name, out var prev)) continue;
            double dTotal = 0, dIdle = 0;
            for (int k = 0; k < Math.Min(vals.Length, 8); k++) dTotal += vals[k] - (double)prev[k];
            dIdle = (vals[3] - (double)prev[3]) + (vals.Length > 4 ? vals[4] - (double)prev[4] : 0);
            double? load = dTotal > 0 ? Math.Clamp(100 * (dTotal - dIdle) / dTotal, 0, 100) : null;
            if (name == "cpu") total = load;
            else sensors.Add(new SensorReading($"/cpu/load/{name}", $"CPU Core #{int.Parse(name[3..]) + 1}", "Load", load, null, null));
        }
        _prevStat = stat;
        var usage = new SensorReading("/cpu/usage", "CPU Usage", "Load", total, null, null);
        sensors.Add(usage);

        // Clock: average of the current core frequencies.
        var freqs = _freqFiles.Select(SysFs.ReadDouble).Where(v => v.HasValue).Select(v => v!.Value / 1000.0).ToList();
        SensorReading? speed = null;
        if (freqs.Count > 0)
        {
            speed = new SensorReading("/cpu/speed", "CPU Speed", "Clock", freqs.Average(), null, null);
            sensors.Add(speed);
            sensors.Add(new SensorReading("/cpu/speedmax", "Fastest core", "Clock", freqs.Max(), null, null));
        }

        // Package power from the RAPL energy counter.
        SensorReading? power = null;
        if (_raplFile != null && !Hints.CpuPowerDenied)
        {
            try
            {
                var uj = double.Parse(File.ReadAllText(_raplFile).Trim(), CultureInfo.InvariantCulture);
                var now = Stopwatch.GetTimestamp();
                if (_raplPrev is { } p)
                {
                    double d = uj - p.uj;
                    if (d < 0 && _raplRange > 0) d += _raplRange;
                    double secs = (now - p.ts) / (double)Stopwatch.Frequency;
                    if (secs > 0.05 && d >= 0) power = new SensorReading("/cpu/power", "Package", "Power", d / 1e6 / secs, null, null);
                }
                _raplPrev = (uj, now);
            }
            catch (UnauthorizedAccessException) { Hints.CpuPowerDenied = true; }
            catch { }
            if (power != null) sensors.Add(power);
        }

        var snap = new DeviceSnapshot("/cpu/0", _cpuName, DeviceKind.Cpu, "Cpu", sensors);
        return snap with
        {
            PrimaryTemp = PickExact(sensors, "Temperature", "Core (Tdie)", "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Tdie", "Tctl", "Core Max")
                          ?? sensors.FirstOrDefault(x => x.Type == "Temperature" && x.Value.HasValue),
            SecondaryTemp = PickExact(sensors, "Temperature", "CCDs Max (Tdie)", "CCD1 (Tdie)", "Core Max"),
            Load = usage, Power = power, Clock = speed,
        };
    }

    private static Dictionary<string, ulong[]> ReadProcStat()
    {
        var d = new Dictionary<string, ulong[]>();
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu")) break;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vals = parts.Skip(1).Take(8).Select(x => ulong.TryParse(x, out var u) ? u : 0).ToArray();
                if (vals.Length >= 4) d[parts[0]] = vals;
            }
        }
        catch { }
        return d;
    }

    private static string ReadCpuName()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name"))
                    return System.Text.RegularExpressions.Regex.Replace(line[(line.IndexOf(':') + 1)..].Trim(), @"\s+\d+-Core Processor$|\s+Processor$", "");
        }
        catch { }
        return "Processor";
    }

    // ---- memory ----

    private List<DeviceSnapshot> ReadMemory()
    {
        var list = new List<DeviceSnapshot>();
        double? totalKb = null, availKb = null;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) totalKb = Kb(line);
                else if (line.StartsWith("MemAvailable:")) { availKb = Kb(line); break; }
            }
        }
        catch { }
        if (totalKb is double t && availKb is double a && t > 0)
        {
            // Used = total − available: the same figure GNOME System Monitor and `free` show.
            var load = new SensorReading("/ram/load", "Memory", "Load", 100 * (t - a) / t, null, null);
            var sensors = new List<SensorReading>
            {
                load,
                new("/ram/used", "Memory Used", "Data", (t - a) / 1048576.0, null, null),
                new("/ram/avail", "Memory Available", "Data", a / 1048576.0, null, null),
            };
            list.Add(new DeviceSnapshot("/ram", "Total Memory", DeviceKind.Memory, "Memory", sensors) { Load = load });
        }

        int n = 0;
        foreach (var c in _chips.Where(c => c.Name is "spd5118" or "jc42").OrderBy(c => c.DevicePath, StringComparer.Ordinal))
        {
            n++;
            var temps = c.Inputs.Where(i => i.Type == "Temperature").Take(1)
                         .Select(i => new SensorReading($"/memory/dimm/{n}/{i.Key}", $"DIMM #{n}", "Temperature", i.Read(), null, null)).ToList();
            var name = DimmName(c) is string part ? $"{part} (#{n})" : $"Memory module (#{n})";
            list.Add(new DeviceSnapshot($"/memory/dimm/{n}", name, DeviceKind.Memory, "Memory", temps)
            { PrimaryTemp = temps.FirstOrDefault() });
        }
        return list;

        static double Kb(string line) =>
            double.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private readonly Dictionary<string, string?> _dimmNames = new();

    /// <summary>DDR5 module part number from the SPD EEPROM (spd5118 exposes it when readable).</summary>
    private string? DimmName(HwmonChip c)
    {
        if (c.DevicePath == null) return null;
        if (_dimmNames.TryGetValue(c.DevicePath, out var cached)) return cached;
        string? name = null;
        try
        {
            var eeprom = Path.Combine(c.DevicePath, "eeprom");
            if (c.Name == "spd5118" && File.Exists(eeprom))
            {
                using var fs = File.OpenRead(eeprom);
                var buf = new byte[1024];
                int read = fs.Read(buf, 0, buf.Length);
                if (read >= 551)
                {
                    var part = System.Text.Encoding.ASCII.GetString(buf, 521, 30).Trim('\0', ' ');
                    if (part.Length > 3 && part.All(ch => ch >= 0x20 && ch < 0x7F)) name = part;
                }
            }
        }
        catch { }
        _dimmNames[c.DevicePath] = name;
        return name;
    }

    // ---- GPUs ----

    private DeviceSnapshot ReadGpu(GpuInfo g)
    {
        var id = "/gpu/" + g.Slot;
        var sensors = new List<SensorReading>();
        string hwType = g.Vendor switch { "0x10de" => "GpuNvidia", "0x1002" => "GpuAmd", "0x8086" => "GpuIntel", _ => "Gpu" };

        if (g.NvGpu != null && _nvml != null)
        {
            var h = g.NvGpu.Handle;
            Add("Temperature", "GPU Core", _nvml.Temperature(h));
            Add("Load", "GPU Core", _nvml.Load(h));
            Add("Load", "GPU Memory Controller", _nvml.MemoryControllerLoad(h));
            Add("Power", "GPU Package", _nvml.PowerWatts(h));
            Add("Clock", "GPU Core", _nvml.CoreClockMHz(h));
            Add("Clock", "GPU Memory", _nvml.MemoryClockMHz(h));
            Add("Control", "GPU Fan", _nvml.FanPercent(h));
            if (_nvml.Memory(h) is var (used, total) && total > 0)
            {
                Add("SmallData", "GPU Memory Used", used);
                Add("SmallData", "GPU Memory Total", total);
                Add("Load", "GPU Memory", 100 * used / total);
            }
        }
        else
        {
            if (g.Hwmon != null)
                foreach (var i in g.Hwmon.Inputs)
                {
                    string label = i.Label.ToLowerInvariant();
                    string name = (i.Type, label) switch
                    {
                        ("Temperature", "edge") => "GPU Core",
                        ("Temperature", "junction") => "GPU Hot Spot",
                        ("Temperature", "mem") => "GPU Memory",
                        ("Temperature", _) when g.Hwmon.Inputs.Count(x => x.Type == "Temperature") == 1 => "GPU Core",
                        ("Power", "ppt") or ("Power", "power #1") => "GPU Package",
                        ("Clock", "sclk") => "GPU Core",
                        ("Clock", "mclk") => "GPU Memory",
                        ("Fan", _) => "GPU Fan",
                        ("Voltage", "vddgfx") => "GPU Core",
                        ("Voltage", "vddnb") => "GPU SoC",
                        _ => i.Label,
                    };
                    sensors.Add(new SensorReading($"{id}/{i.Key}", name, i.Type, i.Read(), null, null));
                }
            if (g.DevDir.Length > 0)
            {
                Add("Load", "GPU Core", SysFs.ReadDouble(Path.Combine(g.DevDir, "gpu_busy_percent")));
                var used = SysFs.ReadDouble(Path.Combine(g.DevDir, "mem_info_vram_used"));
                var total = SysFs.ReadDouble(Path.Combine(g.DevDir, "mem_info_vram_total"));
                if (used is double u && total is double t && t > 0)
                {
                    Add("SmallData", "GPU Memory Used", u / 1048576.0);
                    Add("SmallData", "GPU Memory Total", t / 1048576.0);
                    Add("Load", "GPU Memory", 100 * u / t);
                }
            }
        }

        var snap = new DeviceSnapshot(id, g.Name, DeviceKind.Gpu, hwType, sensors);
        return snap with
        {
            PrimaryTemp = PickExact(sensors, "Temperature", "GPU Core") ?? sensors.FirstOrDefault(x => x.Type == "Temperature" && x.Value.HasValue),
            SecondaryTemp = PickExact(sensors, "Temperature", "GPU Hot Spot", "GPU Memory"),
            Load = PickExact(sensors, "Load", "GPU Core"),
            Power = PickExact(sensors, "Power", "GPU Package"),
            Clock = PickExact(sensors, "Clock", "GPU Core"),
            Fan = PickExact(sensors, "Fan", "GPU Fan") ?? PickExact(sensors, "Control", "GPU Fan"),
            IsIntegratedGpu = g.Integrated,
        };

        void Add(string type, string name, double? v)
        {
            if (v == null) return;
            sensors.Add(new SensorReading($"{id}/{type}/{name}", name, type, v, null, null));
        }
    }

    // ---- drives ----

    private List<DeviceSnapshot> ReadDrives()
    {
        if ((DateTime.UtcNow - _lastUsage).TotalSeconds > 30) { _usage = ReadDiskUsage(); _lastUsage = DateTime.UtcNow; }
        var list = new List<DeviceSnapshot>();
        foreach (var d in _drives)
        {
            var sensors = new List<SensorReading>();
            foreach (var i in d.Chip.Inputs.Where(i => i.Type == "Temperature"))
            {
                string name = i.Label switch
                {
                    "Composite" => "Composite Temperature",
                    var l when l.StartsWith("Temperature ") && !d.Nvme => "Temperature",
                    var l => l,
                };
                sensors.Add(new SensorReading($"{d.Id}/{i.Key}", name, "Temperature", i.Read(), null, null));
            }
            SensorReading? used = null;
            double total = 0, free = 0;
            foreach (var b in d.Blocks)
                if (_usage.TryGetValue(b, out var u)) { total += u.total; free += u.free; }
            if (total > 0)
            {
                used = new SensorReading($"{d.Id}/used", "Used Space", "Load", 100 * (total - free) / total, null, null);
                sensors.Add(used);
                sensors.Add(new SensorReading($"{d.Id}/free", "Free Space", "Data", free / 1e9, null, null));
                sensors.Add(new SensorReading($"{d.Id}/total", "Total Space", "Data", total / 1e9, null, null));
            }
            var snap = new DeviceSnapshot(d.Id, d.Name, DeviceKind.Storage, "Storage", sensors);
            list.Add(snap with
            {
                PrimaryTemp = PickExact(sensors, "Temperature", "Composite Temperature", "Temperature") ?? sensors.FirstOrDefault(x => x.Type == "Temperature" && x.Value.HasValue),
                Load = used,
                Media = d.Rotational ? DriveMedia.Hdd : DriveMedia.Ssd,
            });
        }
        return list;
    }

    /// <summary>Space used per physical disk (sum over its mounted partitions, through LVM / LUKS).</summary>
    private static Dictionary<string, (double total, double free)> ReadDiskUsage()
    {
        var result = new Dictionary<string, (double, double)>();
        var seen = new HashSet<string>();
        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var p = line.Split(' ');
                if (p.Length < 3 || !p[0].StartsWith("/dev/")) continue;
                var dev = SysFs.RealPath(p[0]) ?? p[0];
                if (!seen.Add(dev)) continue; // bind mounts, btrfs subvolumes
                var mount = p[1].Replace("\\040", " ").Replace("\\011", "\t");
                double total, free;
                try { var di = new System.IO.DriveInfo(mount); total = di.TotalSize; free = di.TotalFreeSpace; }
                catch { continue; }
                if (total <= 0) continue;
                foreach (var disk in Disks(Path.GetFileName(dev), 0))
                {
                    result.TryGetValue(disk, out var acc);
                    result[disk] = (acc.Item1 + total, acc.Item2 + free);
                }
            }
        }
        catch { }
        return result;

        static IEnumerable<string> Disks(string name, int depth)
        {
            if (depth > 4) yield break;
            var sys = "/sys/class/block/" + name;
            var slaves = Path.Combine(sys, "slaves");
            var s = Directory.Exists(slaves) ? Directory.GetDirectories(slaves).Concat(Directory.GetFiles(slaves)).Select(Path.GetFileName).Cast<string>().ToArray() : Array.Empty<string>();
            if (s.Length > 0)
            {
                foreach (var x in s) foreach (var d in Disks(x, depth + 1)) yield return d;
            }
            else if (File.Exists(Path.Combine(sys, "partition")))
            {
                var real = SysFs.RealPath(sys);
                var parent = real != null ? Path.GetFileName(Path.GetDirectoryName(real)) : null;
                if (parent != null) yield return parent;
            }
            else yield return name;
        }
    }

    // ---- motherboard and everything else ----

    private static bool BoardChip(string n) =>
        n.StartsWith("nct") || n.StartsWith("it8") || n.StartsWith("w83") || n.StartsWith("f71") || n.StartsWith("asus") ||
        n is "gigabyte_wmi" or "dell_smm" or "thinkpad" or "applesmc" or "acpitz" or "hp_wmi_sensors" || n.StartsWith("pch_");

    private List<DeviceSnapshot> ReadBoardAndOther()
    {
        var list = new List<DeviceSnapshot>();
        var counts = new Dictionary<string, int>();
        foreach (var c in _chips)
        {
            if (IsCpuChip(c.Name) || c.Name is "nvme" or "drivetemp" or "spd5118" or "jc42") continue;
            if (_gpus.Any(g => g.Hwmon == c)) continue;
            if (c.Inputs.Count == 0) continue;
            int n = counts.TryGetValue(c.Name, out var k) ? k + 1 : 1;
            counts[c.Name] = n;
            var id = $"/hwmon/{c.Name}/{n}";
            var kind = BoardChip(c.Name) ? DeviceKind.Motherboard : DeviceKind.Other;
            var sensors = c.Inputs.Select(i => new SensorReading($"{id}/{i.Key}", i.Label, i.Type, i.Read(), null, null)).ToList();
            var snap = new DeviceSnapshot(id, PrettyChip(c.Name, n), kind, kind == DeviceKind.Motherboard ? "SuperIO" : "Other", sensors);
            list.Add(snap with { PrimaryTemp = sensors.FirstOrDefault(x => x.Type == "Temperature" && x.Value.HasValue) });
        }
        return list;
    }

    private static string PrettyChip(string n, int index)
    {
        string name = n switch
        {
            "acpitz" => "ACPI thermal zone",
            "gigabyte_wmi" => "Gigabyte board sensors",
            "asus_wmi_sensors" or "asus_ec_sensors" or "asusec" => "ASUS board sensors",
            "dell_smm" => "Dell system sensors",
            "thinkpad" => "ThinkPad sensors",
            "iwlwifi_1" => "Intel Wi-Fi",
            var x when x.StartsWith("nct") => "Nuvoton " + x.ToUpperInvariant(),
            var x when x.StartsWith("it8") => "ITE " + x.ToUpperInvariant(),
            var x when x.StartsWith("pch_") => "Chipset (" + x[4..] + ")",
            var x when x.StartsWith("BAT") => "Battery " + x[3..],
            var x => x,
        };
        return index > 1 ? $"{name} #{index}" : name;
    }

    // ---- helpers ----

    private static SensorReading? PickExact(List<SensorReading> sensors, string type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = sensors.FirstOrDefault(x => x.Type == type && x.Value.HasValue && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s;
        }
        return null;
    }

    private static string[] SafeDirs(string dir)
    {
        // sysfs entries are mostly symlinks to directories: list everything, keep what resolves to a directory.
        try { return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Where(Directory.Exists).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        try { _thread?.Join(2000); } catch { }
        _nvml?.Dispose();
    }
}
