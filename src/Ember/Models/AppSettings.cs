using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>How many days the Log page's charts show (limited to <see cref="LogRetentionDays"/>).</summary>
    public int LogViewDays { get; set; } = 7;

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
        LogViewDays = Math.Clamp(LogViewDays, 1, 365);
        Hotkey ??= new();
        Drives ??= new();
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
