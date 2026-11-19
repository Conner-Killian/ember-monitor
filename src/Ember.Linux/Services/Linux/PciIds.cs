using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Ember.Services.Linux;

/// <summary>Graphics card names from the system's pci.ids database (hwdata / pciutils).</summary>
public static class PciIds
{
    private static readonly string[] Files =
        { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids", "/usr/share/hwdata/pci.ids.gz" };

    private static readonly Dictionary<string, string?> Cache = new();

    /// <summary>Raw device name, e.g. "Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]".</summary>
    public static string? Lookup(string vendorId, string deviceId)
    {
        vendorId = vendorId.Replace("0x", "").ToLowerInvariant();
        deviceId = deviceId.Replace("0x", "").ToLowerInvariant();
        var key = vendorId + ":" + deviceId;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            string? found = null;
            foreach (var f in Files)
            {
                if (!File.Exists(f) || f.EndsWith(".gz")) continue;
                try
                {
                    bool inVendor = false;
                    foreach (var line in File.ReadLines(f))
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        if (line[0] != '\t')
                        {
                            if (inVendor) break;
                            inVendor = line.StartsWith(vendorId + "  ", StringComparison.Ordinal);
                        }
                        else if (inVendor && line.Length > 1 && line[1] != '\t' &&
                                 line.StartsWith("\t" + deviceId + "  ", StringComparison.Ordinal))
                        {
                            found = line[(deviceId.Length + 3)..].Trim();
                            break;
                        }
                    }
                }
                catch { }
                if (found != null) break;
            }
            Cache[key] = found;
            return found;
        }
    }

    /// <summary>Friendly GPU name, e.g. "AMD Radeon RX 7900 XT/7900 XTX" or "Intel UHD Graphics 770".</summary>
    public static string GpuName(string vendorId, string deviceId)
    {
        var raw = Lookup(vendorId, deviceId);
        var vendor = vendorId.Replace("0x", "").ToLowerInvariant() switch
        {
            "1002" => "AMD", "10de" => "NVIDIA", "8086" => "Intel", _ => "",
        };
        if (raw == null) return vendor == "" ? "Graphics card" : $"{vendor} graphics";
        // Marketing name is usually in the brackets: "Navi 31 [Radeon RX 7900 XT/7900 XTX]".
        var m = Regex.Match(raw, @"\[([^\]]+)\]");
        var name = m.Success ? m.Groups[1].Value : raw;
        return vendor != "" && !name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? $"{vendor} {name}" : name;
    }
}
