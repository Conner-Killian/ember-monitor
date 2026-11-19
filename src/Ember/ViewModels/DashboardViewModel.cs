using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using Ember.Models;
using Ember.Services;

namespace Ember.ViewModels;

public sealed class StatVM : Observable
{
    public string Key { get; init; } = "";
    private string _label = "", _value = "—";
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>A temperature reading with severity colouring, gauge and history.</summary>
public class TempVM : Observable
{
    public string Key { get; init; } = "";
    private string _name = "", _sub = "", _temp = "—", _unit = "°C", _min = "—", _max = "—";
    private Brush _brush = ThemeService.NeutralBrush;
    private Color _color = Colors.OrangeRed;
    private double _fraction;
    private double[] _history = Array.Empty<double>();
    private bool _available;
    private string _severityText = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    public string Temp { get => _temp; set => Set(ref _temp, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public string Min { get => _min; set => Set(ref _min, value); }
    public string Max { get => _max; set => Set(ref _max, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
    public Color Color { get => _color; set => Set(ref _color, value); }
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public double[] History { get => _history; set => Set(ref _history, value); }
    public bool Available { get => _available; set => Set(ref _available, value); }
    public string SeverityText { get => _severityText; set => Set(ref _severityText, value); }

    public void Apply(SensorReading? r, Thresholds t, bool withHistory = true)
    {
        var v = r?.Value;
        var sev = Temps.Rate(v, t);
        Available = v.HasValue;
        Temp = Temps.Number(v);
        Unit = Temps.UnitSymbol;
        Brush = ThemeService.BrushFor(sev);
        Color = ThemeService.GaugeColorFor(sev);
        Fraction = Temps.Fraction(v, t);
        SeverityText = !v.HasValue ? "Unavailable" : sev switch { Severity.Hot => "Hot", Severity.Warm => "Warm", _ => "Normal" };
        if (r != null)
        {
            var (mn, mx) = App.Sensors.GetMinMax(r.Id);
            Min = Temps.Format(mn);
            Max = Temps.Format(mx);
            if (withHistory) History = App.Sensors.GetHistory(r.Id);
        }
        else { Min = Max = "—"; History = Array.Empty<double>(); }
    }
}

public sealed class DeviceCardVM : TempVM
{
    private string _secondary = "";
    public string Secondary { get => _secondary; set { if (Set(ref _secondary, value)) Raise(nameof(HasSecondary)); } }
    public bool HasSecondary => !string.IsNullOrEmpty(_secondary);
    public ObservableCollection<StatVM> Stats { get; } = new();
    private double[] _loadHistory = Array.Empty<double>();
    public double[] LoadHistory { get => _loadHistory; set => Set(ref _loadHistory, value); }

    public void ApplyDevice(DeviceSnapshot? d, Thresholds t)
    {
        Apply(d?.PrimaryTemp, t);
        if (d == null) { Name = "Not detected"; Stats.Clear(); Secondary = ""; return; }
        Name = d.Name;
        Secondary = d.SecondaryTemp != null && d.SecondaryTemp.Id != d.PrimaryTemp?.Id && d.SecondaryTemp.Value.HasValue
            ? $"{d.SecondaryTemp.Name}  {Temps.Format(d.SecondaryTemp.Value)}" : "";
        LoadHistory = d.Load != null ? App.Sensors.GetHistory(d.Load.Id) : Array.Empty<double>();

        var stats = new List<(string k, string l, string v)>();
        if (d.Load != null) stats.Add(("load", "Usage", Temps.FormatValue(d.Load)));
        if (d.Power != null) stats.Add(("power", "Power", Temps.FormatValue(d.Power)));
        if (d.Clock != null) stats.Add(("clock", d.Kind == DeviceKind.Cpu ? "Speed" : "Clock", Temps.FormatValue(d.Clock)));
        if (d.Fan != null) stats.Add(("fan", "Fan", Temps.FormatValue(d.Fan)));
        CollectionSync.Sync(Stats, stats, x => x.k, x => x.Key, x => new StatVM { Key = x.k },
            (vm, x) => { vm.Label = x.l; vm.Value = x.v; });
    }
}

public sealed class DriveVM : TempVM
{
    private string _media = "", _used = "";
    private double _usedFraction;
    public string Media { get => _media; set => Set(ref _media, value); }
    public string Used { get => _used; set => Set(ref _used, value); }
    public double UsedFraction { get => _usedFraction; set => Set(ref _usedFraction, value); }
}

public sealed class DashboardViewModel : Observable
{
    public DeviceCardVM Cpu { get; } = new() { Key = "cpu" };
    public DeviceCardVM Gpu { get; } = new() { Key = "gpu" };
    public ObservableCollection<TempVM> RamSticks { get; } = new();
    public ObservableCollection<DriveVM> Drives { get; } = new();
    public ObservableCollection<StatVM> Fans { get; } = new();
    public ObservableCollection<StatVM> BoardTemps { get; } = new();
    public ObservableCollection<StatVM> MemoryStats { get; } = new();

    private string _headline = "Reading sensors…", _headlineSub = "";
    private Brush _headlineBrush = ThemeService.CoolBrush;
    private bool _hasRamTemps, _hasFans, _hasBoardTemps, _driverMissing;
    private string _ramSummary = "—";
    private Brush _ramBrush = ThemeService.NeutralBrush;

    public string Headline { get => _headline; set => Set(ref _headline, value); }
    public string HeadlineSub { get => _headlineSub; set => Set(ref _headlineSub, value); }
    public Brush HeadlineBrush { get => _headlineBrush; set => Set(ref _headlineBrush, value); }
    public bool HasRamTemps { get => _hasRamTemps; set => Set(ref _hasRamTemps, value); }
    public bool HasFans { get => _hasFans; set => Set(ref _hasFans, value); }
    public bool HasBoardTemps { get => _hasBoardTemps; set => Set(ref _hasBoardTemps, value); }
    public bool DriverMissing { get => _driverMissing; set => Set(ref _driverMissing, value); }
    public string RamSummary { get => _ramSummary; set => Set(ref _ramSummary, value); }
    public Brush RamBrush { get => _ramBrush; set => Set(ref _ramBrush, value); }

    public void Update(SensorSnapshot snap)
    {
        var s = App.Settings;
        DriverMissing = !App.Sensors.DriverInstalled && snap.CpuTemp == null;

        Cpu.ApplyDevice(snap.Cpu, s.CpuThresholds);
        Gpu.ApplyDevice(snap.Gpu, s.GpuThresholds);

        // ---- Memory ----
        HasRamTemps = snap.RamTemps.Count > 0;
        RamSummary = HasRamTemps ? Temps.Format(snap.RamTemp) : "—";
        RamBrush = ThemeService.BrushFor(Temps.Rate(snap.RamTemp, s.RamThresholds));
        var sticks = snap.RamTemps.ToList();
        CollectionSync.Sync(RamSticks, sticks, r => r.Id, vm => vm.Key, r => new TempVM { Key = r.Id },
            (vm, r) =>
            {
                vm.Apply(r, s.RamThresholds);
                var dev = snap.Devices.FirstOrDefault(d => d.Sensors.Any(x => x.Id == r.Id));
                vm.Name = dev != null && dev.Name != "Generic Memory" && dev.Name != "Total Memory" ? dev.Name : r.Name;
                vm.Sub = r.Name;
            });

        var memStats = new List<(string k, string l, string v)>();
        if (snap.Memory != null)
        {
            var load = snap.Memory.Sensors.FirstOrDefault(x => x.Type == "Load" && x.Name == "Memory");
            var used = snap.Memory.Sensors.FirstOrDefault(x => x.Type == "Data" && x.Name == "Memory Used");
            var avail = snap.Memory.Sensors.FirstOrDefault(x => x.Type == "Data" && x.Name == "Memory Available");
            if (load != null) memStats.Add(("load", "Usage", Temps.FormatValue(load)));
            if (used?.Value is double u && avail?.Value is double a)
                // Total minus available, the same basis as the usage %. (Task Manager's "In use" also
                // excludes the modified page list, so it can read slightly lower.)
                memStats.Add(("used", "Used", $"{u:0.0} / {u + a:0} GB"));
        }
        CollectionSync.Sync(MemoryStats, memStats, x => x.k, x => x.Key, x => new StatVM { Key = x.k }, (vm, x) => { vm.Label = x.l; vm.Value = x.v; });

        // ---- Drives ----
        CollectionSync.Sync(Drives, snap.Drives.ToList(), d => d.Id, vm => vm.Key, d => new DriveVM { Key = d.Id },
            (vm, d) =>
            {
                vm.Apply(d.PrimaryTemp, Temps.For(DeviceKind.Storage, d.Media));
                vm.Name = d.Name;
                vm.Media = d.Media == DriveMedia.Hdd ? "HDD"
                         : d.HardwareType == "Storage" && d.Sensors.Any(x => x.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase)) ? "NVMe SSD" : "SSD";
                if (d.Load?.Value is double used) { vm.Used = $"{used:0}% used"; vm.UsedFraction = used / 100; }
                else { vm.Used = ""; vm.UsedFraction = 0; }
            });

        // ---- Fans & board temps ----
        var fans = snap.Devices
            .Where(d => d.Kind is DeviceKind.Motherboard or DeviceKind.Gpu or DeviceKind.Other)
            .SelectMany(d => d.Sensors.Where(x => x.Type == "Fan" && x.Value is > 0).Select(x => (d, x)))
            .Select(t => (k: t.x.Id, l: t.d.Kind == DeviceKind.Gpu ? $"GPU · {t.x.Name}" : t.x.Name, v: Temps.FormatValue(t.x)))
            .ToList();
        HasFans = fans.Count > 0;
        CollectionSync.Sync(Fans, fans, x => x.k, x => x.Key, x => new StatVM { Key = x.k }, (vm, x) => { vm.Label = x.l; vm.Value = x.v; });

        var board = snap.Devices
            .Where(d => d.Kind == DeviceKind.Motherboard)
            .SelectMany(d => d.Sensors.Where(x => x.Type == "Temperature" && x.Value is > 0 and < 125))
            .Select(x => (k: x.Id, l: x.Name, v: Temps.Format(x.Value)))
            .ToList();
        HasBoardTemps = board.Count > 0;
        CollectionSync.Sync(BoardTemps, board, x => x.k, x => x.Key, x => new StatVM { Key = x.k }, (vm, x) => { vm.Label = x.l; vm.Value = x.v; });

        // ---- Headline ----
        var issues = new List<(string name, Severity sev)>();
        void Check(string n, double? v, Thresholds t) { var sv = Temps.Rate(v, t); if (sv != Severity.Normal) issues.Add((n, sv)); }
        Check("CPU", snap.CpuTemp, s.CpuThresholds);
        Check("GPU", snap.GpuTemp, s.GpuThresholds);
        Check("Memory", snap.RamTemp, s.RamThresholds);
        foreach (var d in snap.Drives) Check(OverlayViewModel.ShortDriveName(d.Name), d.PrimaryTemp?.Value, Temps.For(DeviceKind.Storage, d.Media));

        if (issues.Count == 0)
        {
            Headline = "Everything's running cool";
            HeadlineBrush = ThemeService.CoolBrush;
        }
        else
        {
            var worst = issues.Any(i => i.sev == Severity.Hot) ? Severity.Hot : Severity.Warm;
            var names = string.Join(", ", issues.Where(i => i.sev == worst).Select(i => i.name));
            Headline = worst == Severity.Hot ? $"{names} running hot" : $"{names} running warm";
            HeadlineBrush = worst == Severity.Hot ? ThemeService.HotBrush : ThemeService.WarmBrush;
        }
        int sensorCount = snap.Devices.Sum(d => d.Sensors.Count(x => x.Type == "Temperature" && x.Value.HasValue));
        HeadlineSub = $"{sensorCount} temperature sensors · updated {snap.Time:HH:mm:ss}";
    }
}
