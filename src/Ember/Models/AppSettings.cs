using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ember.Models;

public enum TempUnit { Celsius, Fahrenheit }
public enum OverlayLayout { Pill, Card, Rings }
public enum OverlayAnchor { Free, TopLeft, TopRight, BottomLeft, BottomRight }
public enum Backdrop { Mica, Acrylic, Solid }

public sealed class Thresholds
{
    public double Warn { get; set; }
    public double Critical { get; set; }
    public Thresholds() { }
    public Thresholds(double warn, double crit) { Warn = warn; Critical = crit; }
}

public sealed class DriveOverlayOption
{
    public string Id { get; set; } = "";
    public bool Show { get; set; } = true;
    public string Label { get; set; } = "";
}

/// <summary>
/// A saved overlay setup (style, position, readings, look) that can be switched to from the Overlay
/// tab, the tray menu or its own hotkey.
/// </summary>
public sealed class OverlayPreset
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    /// <summary>Hotkey as virtual-key codes (like <see cref="AppSettings.Hotkey"/>); empty = none.</summary>
    public List<int> Hotkey { get; set; } = new();
    /// <summary>Linux, GNOME on Wayland: the system shortcut registered for this preset ("&lt;Super&gt;1").</summary>
    public string? ShortcutAccel { get; set; }

    public OverlayLayout Layout { get; set; } = OverlayLayout.Pill;
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;
    public double FreeX { get; set; } = double.NaN;
    public double FreeY { get; set; } = double.NaN;
    public bool GrowLeft { get; set; }
    public bool GrowUp { get; set; }
    public string? Monitor { get; set; }
    public string? MonitorBounds { get; set; }
    public double Opacity { get; set; } = 0.82;
    public double Scale { get; set; } = 1.0;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; }
    public bool ShowDrives { get; set; }
    public bool ShowUsage { get; set; }
    public bool ShowPower { get; set; }
    public bool ShowLabels { get; set; } = true;
    public List<DriveOverlayOption> Drives { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The overlay as it's set up right now.</summary>
    public static OverlayPreset Capture(AppSettings s, string name)
    {
        var p = new OverlayPreset { Name = name };
        p.CopyFrom(s);
        return p;
    }

    /// <summary>Replaces this preset's setup with the overlay's current one (name and hotkey stay).</summary>
    public void CopyFrom(AppSettings s)
    {
        Layout = s.OverlayLayout; Anchor = s.OverlayAnchor;
        FreeX = s.OverlayFreeX; FreeY = s.OverlayFreeY; GrowLeft = s.OverlayGrowLeft; GrowUp = s.OverlayGrowUp;
        Monitor = s.OverlayMonitor; MonitorBounds = s.OverlayMonitorBounds;
        Opacity = s.OverlayOpacity; Scale = s.OverlayScale;
        ShowCpu = s.OverlayShowCpu; ShowGpu = s.OverlayShowGpu; ShowRam = s.OverlayShowRam; ShowDrives = s.OverlayShowDrives;
        ShowUsage = s.OverlayShowUsage; ShowPower = s.OverlayShowPower; ShowLabels = s.OverlayShowLabels;
        Drives = s.Drives.Select(d => new DriveOverlayOption { Id = d.Id, Show = d.Show, Label = d.Label }).ToList();
    }

    /// <summary>Switches the overlay to this preset (and shows it).</summary>
    public void ApplyTo(AppSettings s)
    {
        s.OverlayLayout = Layout; s.OverlayAnchor = Anchor;
        s.OverlayFreeX = FreeX; s.OverlayFreeY = FreeY; s.OverlayGrowLeft = GrowLeft; s.OverlayGrowUp = GrowUp;
        s.OverlayMonitor = Monitor; s.OverlayMonitorBounds = MonitorBounds;
        s.OverlayOpacity = Opacity; s.OverlayScale = Scale;
        s.OverlayShowCpu = ShowCpu; s.OverlayShowGpu = ShowGpu; s.OverlayShowRam = ShowRam; s.OverlayShowDrives = ShowDrives;
        s.OverlayShowUsage = ShowUsage; s.OverlayShowPower = ShowPower; s.OverlayShowLabels = ShowLabels;
        // Drive choices: the preset's wins for drives it knows; drives it doesn't know keep theirs.
        foreach (var d in Drives)
        {
            var cur = s.Drives.FirstOrDefault(x => x.Id == d.Id);
            if (cur == null) s.Drives.Add(cur = new DriveOverlayOption { Id = d.Id });
            cur.Show = d.Show; cur.Label = d.Label;
        }
        s.OverlayVisible = true;
    }

    /// <summary>Whether the overlay is set up exactly like this preset right now.</summary>
    public bool Matches(AppSettings s)
    {
        static bool Same(double a, double b) => double.IsNaN(a) ? double.IsNaN(b) : Math.Abs(a - b) < 0.001;
        bool free = Anchor == OverlayAnchor.Free;
        return s.OverlayLayout == Layout && s.OverlayAnchor == Anchor
            && (!free || (Same(s.OverlayFreeX, FreeX) && Same(s.OverlayFreeY, FreeY) && s.OverlayGrowLeft == GrowLeft && s.OverlayGrowUp == GrowUp))
            && (s.OverlayMonitor ?? "") == (Monitor ?? "")
            && Same(s.OverlayOpacity, Opacity) && Same(s.OverlayScale, Scale)
            && s.OverlayShowCpu == ShowCpu && s.OverlayShowGpu == ShowGpu && s.OverlayShowRam == ShowRam && s.OverlayShowDrives == ShowDrives
            && s.OverlayShowUsage == ShowUsage && s.OverlayShowPower == ShowPower && s.OverlayShowLabels == ShowLabels
            && Drives.All(d => s.Drives.FirstOrDefault(x => x.Id == d.Id) is { } cur && cur.Show == d.Show && cur.Label == d.Label);
    }

    /// <summary>One line for the preset list: "Card · Display 2, bottom left · CPU, GPU, usage".</summary>
    public string Describe(string displayName)
    {
        string where = Anchor switch
        {
            OverlayAnchor.TopLeft => "top left", OverlayAnchor.TopRight => "top right",
            OverlayAnchor.BottomLeft => "bottom left", OverlayAnchor.BottomRight => "bottom right",
            _ => "where you placed it",
        };
        var shows = new List<string>();
        if (ShowCpu) shows.Add("CPU");
        if (ShowGpu) shows.Add("GPU");
        if (ShowRam) shows.Add("RAM");
        if (ShowDrives) shows.Add("drives");
        if (ShowUsage) shows.Add("usage");
        if (ShowPower) shows.Add("power");
        return $"{Layout} · {displayName}, {where} · {(shows.Count > 0 ? string.Join(", ", shows) : "nothing selected")}";
    }
}

public sealed class AppSettings
{
    // ---- General ----
    public TempUnit Unit { get; set; } = TempUnit.Celsius;
    public int PollIntervalMs { get; set; } = 1000;
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayShowsTemp { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;

    // ---- Hotkey (virtual-key codes, side specific) ----
    public List<int> Hotkey { get; set; } = new() { 0xA3, 0xA5 }; // Right Ctrl + Right Alt

    // ---- Overlay ----
    public bool OverlayVisible { get; set; } = true;
    public bool OverlayLocked { get; set; } = true;
    public OverlayLayout OverlayLayout { get; set; } = OverlayLayout.Pill;
    public OverlayAnchor OverlayAnchor { get; set; } = OverlayAnchor.TopRight;
    /// <summary>Free placement: screen pixel of the overlay edge nearest the screen border (see Grow* flags).</summary>
    public double OverlayFreeX { get; set; } = double.NaN;
    public double OverlayFreeY { get; set; } = double.NaN;
    public bool OverlayGrowLeft { get; set; }
    public bool OverlayGrowUp { get; set; }
    public string? OverlayMonitor { get; set; }
    /// <summary>Bounds of <see cref="OverlayMonitor"/> ("x,y,w,h"): finds the display again if Windows renumbers it after sleep.</summary>
    public string? OverlayMonitorBounds { get; set; }
    public double OverlayOpacity { get; set; } = 0.82;
    public double OverlayScale { get; set; } = 1.0;
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowRam { get; set; } = false;
    public bool OverlayShowDrives { get; set; } = false;
    public bool OverlayShowUsage { get; set; } = false;
    public bool OverlayShowPower { get; set; } = false;
    public bool OverlayShowLabels { get; set; } = true;
    public bool OverlayHideWhenMainOpen { get; set; } = false;
    public List<DriveOverlayOption> Drives { get; set; } = new();
    public string? PrimaryGpuId { get; set; } // null = auto
    /// <summary>Saved overlay setups (Overlay tab → Presets), in the order they're listed.</summary>
    public List<OverlayPreset> OverlayPresets { get; set; } = new();

    /// <summary>Who already uses this key combination: "Show / hide overlay", a preset's name, or null.</summary>
    public string? HotkeyOwner(IEnumerable<int> combo, string? exceptPresetId)
    {
        var set = new HashSet<int>(combo);
        if (set.Count == 0) return null;
        if (exceptPresetId != "" && set.SetEquals(Hotkey)) return "Show / hide overlay";
        return OverlayPresets.FirstOrDefault(p => p.Id != exceptPresetId && p.Hotkey.Count > 0 && set.SetEquals(p.Hotkey))?.Name;
    }

    // ---- Alerts ----
    public bool AlertsColor { get; set; } = true;
    public bool AlertsNotify { get; set; } = false;
    public int AlertSustainSeconds { get; set; } = 10;
    public Thresholds CpuThresholds { get; set; } = new(80, 90);
    public Thresholds GpuThresholds { get; set; } = new(78, 86);
    public Thresholds RamThresholds { get; set; } = new(60, 75);
    public Thresholds SsdThresholds { get; set; } = new(60, 70);
    public Thresholds HddThresholds { get; set; } = new(48, 55);

    // ---- Appearance ----
    public const string DefaultAccent = "#B07CFF";
    public string AccentColor { get; set; } = DefaultAccent;
    /// <summary>Missing in old files → 0, which triggers migrations in <see cref="Sanitize"/>.</summary>
    public int SettingsVersion { get; set; }
    public Backdrop Backdrop { get; set; } = Backdrop.Mica;
    public double TintOpacity { get; set; } = 0.55;
    public double UiScale { get; set; } = 1.0;

    // ---- All sensors page ----
    public bool SensorsTemperaturesOnly { get; set; }

    // ---- Temperature log ----
    /// <summary>Record temperatures to a fixed-size log on disk (off by default).</summary>
    public bool LogEnabled { get; set; }
    /// <summary>How many days the log keeps; older data is overwritten, so the file never grows.</summary>
    public int LogRetentionDays { get; set; } = 7;
    /// <summary>How many hours the Log page's charts show (limited to <see cref="LogRetentionDays"/>).</summary>
    public int LogViewHours { get; set; } = 7 * 24;
    /// <summary>1.2.0 stored the chart length in days; read once, converted to hours, then dropped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LogViewDays { get; set; }

    // ---- Updates ----
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Version already announced with a notification (announce each version once).</summary>
    public string? NotifiedUpdateVersion { get; set; }
    /// <summary>Version whose sidebar banner was dismissed with "Later".</summary>
    public string? DismissedUpdateVersion { get; set; }

    // ---- Window ----
    public double MainWidth { get; set; } = 1180;
    public double MainHeight { get; set; } = 760;

    // ------------------------------------------------------------------
    [JsonIgnore]
    public static string Folder =>
        Environment.GetEnvironmentVariable("EMBER_CONFIG_DIR") is { Length: > 0 } custom ? custom // tests / portable use
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ember"); // %APPDATA%\Ember, ~/.config/Ember
    [JsonIgnore]
    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null) { s.Sanitize(); return s; }
            }
        }
        catch
        {
            // Corrupt settings: keep a copy before the defaults overwrite it on the next save.
            try { File.Copy(FilePath, Path.Combine(Folder, "settings.corrupt.json"), true); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    private void Sanitize()
    {
        if (SettingsVersion < 2)
        {
            // v2: default accent changed from ember orange to violet.
            if (string.Equals(AccentColor, "#FF7A45", StringComparison.OrdinalIgnoreCase)) AccentColor = DefaultAccent;
            SettingsVersion = 2;
        }
        // Older settings files stored the free position in a different format.
        if (OverlayAnchor == OverlayAnchor.Free && (double.IsNaN(OverlayFreeX) || double.IsNaN(OverlayFreeY)))
            OverlayAnchor = OverlayAnchor.TopRight;
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 10000);
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0.2, 1.0);
        OverlayScale = Math.Clamp(OverlayScale, 0.6, 2.0);
        TintOpacity = Math.Clamp(TintOpacity, 0.0, 1.0);
        UiScale = Math.Clamp(UiScale, 0.8, 1.5);
        if (!double.IsFinite(MainWidth) || MainWidth < 980) MainWidth = 1180;
        if (!double.IsFinite(MainHeight) || MainHeight < 620) MainHeight = 760;
        AlertSustainSeconds = Math.Clamp(AlertSustainSeconds, 0, 600);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
        if (LogViewDays is int oldDays) { LogViewHours = Math.Clamp(oldDays, 1, 365) * 24; LogViewDays = null; }
        LogViewHours = Math.Clamp(LogViewHours, 1, 365 * 24);
        Hotkey ??= new();
        Drives ??= new();
        OverlayPresets ??= new();
        OverlayPresets.RemoveAll(p => p == null);
        var ids = new HashSet<string>();
        foreach (var p in OverlayPresets)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id)) { p.Id = OverlayPreset.NewId(); ids.Add(p.Id); }
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Preset";
            p.Hotkey ??= new();
            p.Drives ??= new();
            p.Opacity = double.IsFinite(p.Opacity) ? Math.Clamp(p.Opacity, 0.2, 1.0) : 0.82;
            p.Scale = double.IsFinite(p.Scale) ? Math.Clamp(p.Scale, 0.6, 2.0) : 1.0;
            if (p.Anchor == OverlayAnchor.Free && (double.IsNaN(p.FreeX) || double.IsNaN(p.FreeY))) p.Anchor = OverlayAnchor.TopRight;
        }
        CpuThresholds ??= new(80, 90);
        GpuThresholds ??= new(78, 86);
        RamThresholds ??= new(60, 75);
        SsdThresholds ??= new(60, 70);
        HddThresholds ??= new(48, 55);
        foreach (var t in new[] { CpuThresholds, GpuThresholds, RamThresholds, SsdThresholds, HddThresholds })
        {
            if (!double.IsFinite(t.Warn)) t.Warn = 70;
            if (!double.IsFinite(t.Critical)) t.Critical = 85;
            if (t.Critical <= t.Warn) t.Critical = t.Warn + 1;
        }
    }
}
