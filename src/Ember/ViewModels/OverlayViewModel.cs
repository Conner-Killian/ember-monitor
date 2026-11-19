using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using Ember.Models;
using Ember.Services;

namespace Ember.ViewModels;

public sealed class OverlayItem : Observable
{
    public string Key { get; init; } = "";

    private string _label = "", _value = "—", _unit = "°", _extra = "", _extraReserve = "";
    private Brush _valueBrush = ThemeService.NeutralBrush;
    private Color _gaugeColor = Colors.OrangeRed;
    private double _fraction;

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public string Extra { get => _extra; set { if (Set(ref _extra, value)) Raise(nameof(HasExtra)); } }
    public bool HasExtra => !string.IsNullOrEmpty(_extraReserve);
    /// <summary>
    /// Invisible placeholder laid out under <see cref="Extra"/> ("88% · 888W"): reserves the width of
    /// the widest usual value so the overlay doesn't grow and the readings don't shift when usage
    /// or power gains a digit. Digits are tabular, so every digit is as wide as "8".
    /// </summary>
    public string ExtraReserve { get => _extraReserve; set { if (Set(ref _extraReserve, value)) Raise(nameof(HasExtra)); } }
    public Brush ValueBrush { get => _valueBrush; set => Set(ref _valueBrush, value); }
    public Color GaugeColor { get => _gaugeColor; set => Set(ref _gaugeColor, value); }
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    private bool _isFirst;
    /// <summary>First item (no divider before it). The Linux UI uses this; WPF uses PreviousData.</summary>
    public bool IsFirst { get => _isFirst; set => Set(ref _isFirst, value); }
}

public sealed class OverlayViewModel : Observable
{
    public ObservableCollection<OverlayItem> Items { get; } = new();

    private OverlayLayout _layout = OverlayLayout.Pill;
    private bool _showLabels = true;
    private bool _unlocked;
    private string _status = "";

    public OverlayLayout Layout
    {
        get => _layout;
        set { if (Set(ref _layout, value)) { Raise(nameof(IsPill)); Raise(nameof(IsCard)); Raise(nameof(IsRings)); } }
    }
    public bool IsPill => _layout == OverlayLayout.Pill;
    public bool IsCard => _layout == OverlayLayout.Card;
    public bool IsRings => _layout == OverlayLayout.Rings;
    public bool ShowLabels { get => _showLabels; set => Set(ref _showLabels, value); }
    public bool Unlocked { get => _unlocked; set => Set(ref _unlocked, value); }
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    private sealed record Row(string Key, string Label, double? Temp, Thresholds T, (string Text, string Reserve) Extra);
    private static readonly (string, string) NoExtra = ("", "");

    public void Update(SensorSnapshot? snap)
    {
        var s = App.Settings;
        Layout = s.OverlayLayout;
        ShowLabels = s.OverlayShowLabels;
        Unlocked = !s.OverlayLocked;

        if (snap == null)
        {
            Status = App.Sensors?.FailureMessage ?? "Starting…";
            CollectionSync.Sync(Items, Array.Empty<Row>(), r => r.Key, i => i.Key, r => new OverlayItem { Key = r.Key }, (_, _) => { });
            return;
        }
        Status = "";

        var rows = new List<Row>();
        if (s.OverlayShowCpu)
            rows.Add(new Row("cpu", "CPU", snap.CpuTemp, s.CpuThresholds, Extras(snap.Cpu)));
        if (s.OverlayShowGpu && snap.Gpu != null)
            rows.Add(new Row("gpu", "GPU", snap.GpuTemp, s.GpuThresholds, Extras(snap.Gpu)));
        if (s.OverlayShowRam && snap.RamTemps.Count > 0)
            rows.Add(new Row("ram", "RAM", snap.RamTemp, s.RamThresholds,
                s.OverlayShowUsage && snap.Memory?.Load != null ? Join(new() { Part(snap.Memory.Load.Value, "%", 2) }) : NoExtra));
        if (s.OverlayShowDrives)
        {
            foreach (var d in snap.Drives)
            {
                if (d.PrimaryTemp == null) continue;
                var opt = s.Drives.FirstOrDefault(o => o.Id == d.Id);
                if (opt != null && !opt.Show) continue;
                var label = !string.IsNullOrWhiteSpace(opt?.Label) ? opt!.Label : ShortDriveName(d.Name);
                rows.Add(new Row("drive:" + d.Id, label, d.PrimaryTemp.Value, Temps.For(DeviceKind.Storage, d.Media), NoExtra));
            }
        }

        CollectionSync.Sync(Items, rows, r => r.Key, i => i.Key,
            r => new OverlayItem { Key = r.Key },
            (item, r) =>
            {
                var sev = Temps.Rate(r.Temp, r.T);
                item.Label = r.Label;
                item.Value = Temps.Number(r.Temp);
                item.Unit = s.Unit == TempUnit.Fahrenheit ? "°F" : "°C";
                item.Extra = r.Extra.Text;
                item.ExtraReserve = r.Extra.Reserve;
                item.ValueBrush = ThemeService.BrushFor(sev);
                item.GaugeColor = ThemeService.GaugeColorFor(sev);
                item.Fraction = Temps.Fraction(r.Temp, r.T);
            });
        for (int i = 0; i < Items.Count; i++) Items[i].IsFirst = i == 0;
    }

    private static (string Text, string Reserve) Extras(DeviceSnapshot? d)
    {
        if (d == null) return NoExtra;
        var s = App.Settings;
        // A part is included whenever the device has the sensor, even if a reading is momentarily
        // missing, so the layout doesn't jump when a value drops out for one tick.
        var parts = new List<(string, string)>(2);
        if (s.OverlayShowUsage && d.Load != null) parts.Add(Part(d.Load.Value, "%", 2));
        if (s.OverlayShowPower && d.Power != null) parts.Add(Part(d.Power.Value, "W", 3));
        return Join(parts);
    }

    /// <summary>Formats a value and its placeholder, which has at least <paramref name="digits"/> digits.</summary>
    private static (string Text, string Reserve) Part(double? value, string unit, int digits)
    {
        var text = value is double v && !double.IsNaN(v) ? $"{v:0}{unit}" : "–" + unit;
        int len = Math.Max(digits, text.Length - unit.Length);
        return (text, new string('8', len) + unit);
    }

    private static (string Text, string Reserve) Join(List<(string Text, string Reserve)> parts) =>
        parts.Count == 0 ? NoExtra
            : (string.Join(" · ", parts.Select(p => p.Text)), string.Join(" · ", parts.Select(p => p.Reserve)));

    public static string ShortDriveName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Split('-')[0])
                        .Where(p => p.Length > 0)
                        .ToList();
        if (parts.Count == 0) return "Drive";
        static bool IsCapacity(string p) => System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d+(\.\d+)?(GB|TB|G|T)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Prefer a model-number token (letters + digits), e.g. "SN570", "990", "ST3000DM001"; skip "1TB".
        var tok = parts.LastOrDefault(p => p.Any(char.IsDigit) && p.Any(char.IsLetter) && !IsCapacity(p))
               ?? parts.LastOrDefault(p => p.Any(char.IsDigit) && !IsCapacity(p))
               ?? parts[0];
        return tok.Length > 11 ? tok[..11] : tok;
    }
}
