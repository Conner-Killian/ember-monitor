using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ember.Services.Linux;

/// <summary>One readable value of a hwmon chip (e.g. temp1_input).</summary>
public sealed class HwmonInput
{
    public required string Path { get; init; }
    public required string Key { get; init; }      // "temp1", "fan2", "in0" …
    public required string Type { get; init; }     // SensorReading type: Temperature, Fan, Voltage, Power, Current, Control, Clock
    public required string Label { get; init; }
    public required double Scale { get; init; }    // multiply raw sysfs value by this

    public double? Read()
    {
        var v = SysFs.ReadDouble(Path);
        if (v is not double raw) return null;
        double x = raw * Scale;
        // Unconnected inputs on many Super I/O chips read -128 / 127 / 255 °C.
        if (Type == "Temperature" && (x <= -40 || x >= 150)) return null;
        return x;
    }
}

/// <summary>A /sys/class/hwmon/hwmonN chip.</summary>
public sealed class HwmonChip
{
    public required string Dir { get; init; }
    public required string Name { get; init; }       // driver name: k10temp, amdgpu, nvme, nct6798 …
    public string? DevicePath { get; init; }          // resolved /sys/devices/… path of the parent device
    public List<HwmonInput> Inputs { get; } = new();

    private static readonly Regex InputFile = new(@"^(temp|fan|in|power|curr|freq|pwm)(\d+)(_input|_average)?$", RegexOptions.Compiled);

    public static List<HwmonChip> Scan()
    {
        var chips = new List<HwmonChip>();
        const string root = "/sys/class/hwmon";
        if (!Directory.Exists(root)) return chips;
        // Entries are symlinks; list every name and keep the ones that lead to a directory.
        var entries = Directory.EnumerateFileSystemEntries(root).Where(Directory.Exists);
        foreach (var dir in entries.OrderBy(d => d, StringComparer.Ordinal))
        {
            try
            {
                var name = SysFs.ReadText(System.IO.Path.Combine(dir, "name"))?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                string? dev = SysFs.RealPath(System.IO.Path.Combine(dir, "device"));
                var chip = new HwmonChip { Dir = dir, Name = name, DevicePath = dev };
                chip.Discover();
                chips.Add(chip);
            }
            catch { /* a chip that vanished or can't be read */ }
        }
        return chips;
    }

    private void Discover()
    {
        var files = Directory.GetFiles(Dir).Select(System.IO.Path.GetFileName).Where(f => f != null).Cast<string>().ToHashSet();
        var seen = new HashSet<string>();
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var m = InputFile.Match(f);
            if (!m.Success) continue;
            string kind = m.Groups[1].Value, idx = m.Groups[2].Value, suffix = m.Groups[3].Value;
            string key = kind + idx;
            if (kind == "pwm" ? suffix != "" : suffix == "") continue; // pwmN has no suffix; others need _input/_average
            if (kind == "power" && suffix == "_average" && files.Contains(key + "_input")) continue; // prefer _input
            if (!seen.Add(key)) continue;

            var (type, scale) = kind switch
            {
                "temp" => ("Temperature", 0.001),
                "fan" => ("Fan", 1.0),
                "in" => ("Voltage", 0.001),
                "power" => ("Power", 0.000001),
                "curr" => ("Current", 0.001),
                "freq" => ("Clock", 0.000001), // Hz → MHz
                "pwm" => ("Control", 100.0 / 255.0),
                _ => ("", 0),
            };
            if (type == "") continue;
            var path = System.IO.Path.Combine(Dir, f);
            // Inputs that error out right away (no sensor connected, ENODATA) are skipped.
            if (SysFs.ReadDouble(path) == null) continue;
            var label = SysFs.ReadText(System.IO.Path.Combine(Dir, key + "_label"))?.Trim();
            Inputs.Add(new HwmonInput
            {
                Path = path, Key = key, Type = type, Scale = scale,
                Label = string.IsNullOrEmpty(label) ? DefaultLabel(kind, idx) : label!,
            });
        }
    }

    private static string DefaultLabel(string kind, string idx) => kind switch
    {
        "temp" => $"Temperature {idx}",
        "fan" => $"Fan #{idx}",
        "in" => $"Voltage #{idx}",
        "power" => $"Power #{idx}",
        "curr" => $"Current #{idx}",
        "freq" => $"Clock #{idx}",
        "pwm" => $"Fan control #{idx}",
        _ => kind + idx,
    };
}

/// <summary>Small sysfs / procfs helpers. Every read is tolerant: missing or unreadable → null.</summary>
public static class SysFs
{
    public static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    public static double? ReadDouble(string path)
    {
        var t = ReadText(path);
        if (t == null) return null;
        return double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr realpath_native([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "free")]
    private static extern void free_native(IntPtr p);

    /// <summary>
    /// Fully resolved path (like realpath(3)). sysfs is a forest of relative symlinks whose parents are
    /// symlinks too, so .NET's lexical ".." handling would land in the wrong place.
    /// </summary>
    public static string? RealPath(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var p = realpath_native(path, IntPtr.Zero);
            if (p == IntPtr.Zero) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p); }
            finally { free_native(p); }
        }
        catch { return null; }
    }

    /// <summary>Last path component of a symlink target, e.g. the driver name of device/driver.</summary>
    public static string? LinkName(string path) => RealPath(path) is string p ? System.IO.Path.GetFileName(p) : null;
}
