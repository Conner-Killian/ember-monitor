using System;
using Ember.Models;

namespace Ember.Services;

public enum Severity { Normal, Warm, Hot }

/// <summary>Unit conversion, formatting and threshold helpers. All internal values are °C.</summary>
public static class Temps
{
    public static AppSettings S => App.Settings;

    public static double ToDisplay(double c) => S.Unit == TempUnit.Fahrenheit ? c * 9 / 5 + 32 : c;
    public static double FromDisplay(double d) => S.Unit == TempUnit.Fahrenheit ? (d - 32) * 5 / 9 : d;
    public static string UnitSymbol => S.Unit == TempUnit.Fahrenheit ? "°F" : "°C";

    // Round half away from zero (62.5 → 63) like every other number Ember formats with "0";
    // Math.Round's default banker's rounding would show 62.5 as 62 here but 63 elsewhere.
    private static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    public static string Format(double? c, bool withUnit = true) =>
        c is double v ? $"{Round(ToDisplay(v)):0}{(withUnit ? UnitSymbol : "°")}" : "—";

    public static string Number(double? c) => c is double v ? $"{Round(ToDisplay(v)):0}" : "—";

    public static Severity Rate(double? c, Thresholds t)
    {
        if (c is not double v) return Severity.Normal;
        if (v >= t.Critical) return Severity.Hot;
        if (v >= t.Warn) return Severity.Warm;
        return Severity.Normal;
    }

    /// <summary>0..1 position of a temperature on a gauge that spans 20 °C → critical + 10.</summary>
    public static double Fraction(double? c, Thresholds t)
    {
        if (c is not double v) return 0;
        double lo = 20, hi = Math.Max(t.Critical + 10, 50);
        return Math.Clamp((v - lo) / (hi - lo), 0, 1);
    }

    public static Thresholds For(DeviceKind kind, DriveMedia media = DriveMedia.Ssd) => kind switch
    {
        DeviceKind.Cpu => S.CpuThresholds,
        DeviceKind.Gpu => S.GpuThresholds,
        DeviceKind.Memory => S.RamThresholds,
        DeviceKind.Storage => media == DriveMedia.Hdd ? S.HddThresholds : S.SsdThresholds,
        _ => S.CpuThresholds,
    };

    public static string FormatValue(SensorReading? r) => r == null ? "—" : FormatValue(r.Type, r.Value);

    public static string FormatValue(string type, double? value)
    {
        if (value is not double v) return "—";
        return type switch
        {
            "Temperature" => Format(v),
            "Load" or "Control" or "Level" => $"{v:0}%",
            "Power" => v >= 100 ? $"{v:0} W" : $"{v:0.0} W",
            "Clock" => v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz",
            "Frequency" => $"{v:0} Hz",
            "Fan" => $"{v:0} RPM",
            "Flow" => $"{v:0.0} L/h",
            "Voltage" => $"{v:0.000} V",
            "Current" => $"{v:0.00} A",
            "Data" => $"{v:0.0} GB",
            "SmallData" => $"{v:0} MB",
            "Throughput" => v >= 1024 * 1024 ? $"{v / 1024 / 1024:0.0} MB/s" : $"{v / 1024:0} KB/s",
            "Energy" => $"{v:0} mWh",
            "Factor" => $"{v:0.00}",
            "TimeSpan" => TimeSpan.FromSeconds(v).ToString(@"hh\:mm\:ss"),
            "Timing" => $"{v * 1e9:0.0} ns",
            "Noise" => $"{v:0} dBA",
            "Humidity" => $"{v:0}%",
            _ => $"{v:0.##}",
        };
    }
}
