using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using Ember.Services;

namespace Ember.ViewModels;

/// <summary>An entry in the flat, virtualized "All sensors" list.</summary>
public interface ISensorListItem { string Key { get; } }

/// <summary>Device card header (click to expand / collapse).</summary>
public sealed class SensorGroupVM : Observable, ISensorListItem
{
    public string Key { get; init; } = "";
    private string _name = "", _kind = "", _icon = "", _summary = "";
    private bool _expanded = true;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    /// <summary>Icon name for the Linux app's vector icons (cpu, gpu, memory, board, drive, device).</summary>
    public string IconKind { get; set; } = "device";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>The SENSOR / TYPE / CURRENT / MIN / MAX column header under an expanded device.</summary>
public sealed class SensorColumnsVM : ISensorListItem
{
    public string Key { get; init; } = "";
}

public sealed class SensorRowVM : Observable, ISensorListItem
{
    public string Key { get; init; } = "";
    private string _name = "", _type = "", _value = "", _min = "", _max = "";
    private Brush _brush = ThemeService.NeutralBrush;
    private bool _isLast;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Min { get => _min; set => Set(ref _min, value); }
    public string Max { get => _max; set => Set(ref _max, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
    /// <summary>Last row of its device card (rounded bottom corners).</summary>
    public bool IsLast { get => _isLast; set => Set(ref _isLast, value); }
}

/// <summary>Sorts "Core #2" before "Core #10" (digit runs compare as numbers). Pure C#: shared with Linux.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = a.CompareTo(b, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>
/// The All sensors page. A PC exposes several hundred sensors; building a visual for every one of
/// them at once froze the window for a moment the first time the page opened. Everything is now a
/// single flat list (device header, column header, rows…) shown in a virtualizing panel, so only
/// the rows on screen get visuals.
/// </summary>
public sealed class SensorsViewModel : Observable
{
    public ObservableCollection<ISensorListItem> Items { get; } = new();

    private string _filter = "";
    private bool _tempsOnly = App.Settings.SensorsTemperaturesOnly; // remembered between visits and restarts
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Refresh(); } }
    public bool TempsOnly
    {
        get => _tempsOnly;
        set
        {
            if (!Set(ref _tempsOnly, value)) return;
            App.Settings.SensorsTemperaturesOnly = value;
            App.Settings.Save();
            Refresh();
        }
    }

    private SensorSnapshot? _last;
    private readonly Dictionary<string, SensorGroupVM> _groups = new();
    private readonly Dictionary<string, SensorColumnsVM> _columns = new();
    private readonly Dictionary<string, SensorRowVM> _rows = new();

    private static readonly string[] TypeOrder =
        { "Temperature", "Load", "Power", "Clock", "Fan", "Control", "Voltage", "Current", "Data", "SmallData", "Throughput", "Level", "Frequency", "Factor", "Energy", "TimeSpan", "Timing" };

    private static int Order(string t) { int i = Array.IndexOf(TypeOrder, t); return i < 0 ? 99 : i; }

    public void Update(SensorSnapshot snap) { _last = snap; Refresh(); }

    public void Toggle(SensorGroupVM g)
    {
        g.IsExpanded = !g.IsExpanded;
        Refresh();
    }

    private void Refresh()
    {
        if (_last == null) return;
        var f = _filter.Trim();
        var list = new List<ISensorListItem>(Items.Count + 16);

        foreach (var d in _last.Devices)
        {
            var rows = d.Sensors
                .Where(s => s.Value.HasValue)
                .Where(s => !_tempsOnly || s.Type == "Temperature")
                .Where(s => f.Length == 0 || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                          || d.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                          || s.Type.Contains(f, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => Order(s.Type)).ThenBy(s => s.Name, NaturalComparer.Instance)
                .ToList();
            if (rows.Count == 0) continue;

            if (!_groups.TryGetValue(d.Id, out var g)) _groups[d.Id] = g = new SensorGroupVM { Key = "g:" + d.Id };
            g.Name = d.Name;
            // Segoe Fluent / MDL2 icon glyphs: U+E950 CPU, U+E7F4 GPU, U+E964 memory, U+E9F5 board, U+EDA2 storage, U+E7F8 device.
            (g.Kind, g.Icon) = d.Kind switch
            {
                DeviceKind.Cpu => ("Processor", ""),
                DeviceKind.Gpu => ("Graphics", ""),
                DeviceKind.Memory => ("Memory", ""),
                DeviceKind.Motherboard => ("Motherboard", ""),
                DeviceKind.Storage => ("Storage", ""),
                _ => ("Device", ""),
            };
            g.IconKind = d.Kind switch
            {
                DeviceKind.Cpu => "cpu", DeviceKind.Gpu => "gpu", DeviceKind.Memory => "memory",
                DeviceKind.Motherboard => "board", DeviceKind.Storage => "drive", _ => "device",
            };
            var temps = rows.Where(r => r.Type == "Temperature").ToList();
            g.Summary = temps.Count > 0
                ? $"{rows.Count} sensors · hottest {Temps.Format(temps.Max(t => t.Value))}"
                : $"{rows.Count} sensors";
            list.Add(g);
            if (!g.IsExpanded) continue;

            if (!_columns.TryGetValue(d.Id, out var cols)) _columns[d.Id] = cols = new SensorColumnsVM { Key = "c:" + d.Id };
            list.Add(cols);

            var thresholds = Temps.For(d.Kind, d.Media);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!_rows.TryGetValue(r.Id, out var row)) _rows[r.Id] = row = new SensorRowVM { Key = "r:" + r.Id };
                // Session min/max tracked by Ember (cleared by "Reset min/max" on the dashboard).
                var (mn, mx) = App.Sensors.GetMinMax(r.Id);
                row.Name = r.Name;
                row.Type = r.Type == "SmallData" ? "Data" : r.Type;
                row.Value = Temps.FormatValue(r.Type, r.Value);
                row.Min = Temps.FormatValue(r.Type, mn ?? r.Value);
                row.Max = Temps.FormatValue(r.Type, mx ?? r.Value);
                row.Brush = r.Type == "Temperature" ? ThemeService.BrushFor(Temps.Rate(r.Value, thresholds)) : ThemeService.NeutralBrush;
                row.IsLast = i == rows.Count - 1;
                list.Add(row);
            }
        }

        CollectionSync.Sync(Items, list, x => x.Key, x => x.Key, x => x, (_, _) => { });
    }
}
