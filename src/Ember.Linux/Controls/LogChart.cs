using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ember.Services;

namespace Ember.Controls;

/// <summary>
/// Temperature history chart for the Log page (same design as on Windows): a band from the lowest to
/// the highest reading, the average as a line, dashed warning / critical lines, time labels, and a
/// readout under the pointer. Stretches without a recording are left empty.
/// </summary>
public sealed class LogChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<LogAnalysis.Bin>?> BinsProperty = AvaloniaProperty.Register<LogChart, IReadOnlyList<LogAnalysis.Bin>?>(nameof(Bins));
    public static readonly StyledProperty<long> FromMinuteProperty = AvaloniaProperty.Register<LogChart, long>(nameof(FromMinute));
    public static readonly StyledProperty<long> ToMinuteProperty = AvaloniaProperty.Register<LogChart, long>(nameof(ToMinute));
    public static readonly StyledProperty<double> WarnProperty = AvaloniaProperty.Register<LogChart, double>(nameof(Warn), double.NaN);
    public static readonly StyledProperty<double> CriticalProperty = AvaloniaProperty.Register<LogChart, double>(nameof(Critical), double.NaN);

    public IReadOnlyList<LogAnalysis.Bin>? Bins { get => GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public long FromMinute { get => GetValue(FromMinuteProperty); set => SetValue(FromMinuteProperty, value); }
    public long ToMinute { get => GetValue(ToMinuteProperty); set => SetValue(ToMinuteProperty, value); }
    public double Warn { get => GetValue(WarnProperty); set => SetValue(WarnProperty, value); }
    public double Critical { get => GetValue(CriticalProperty); set => SetValue(CriticalProperty, value); }

    static LogChart() => AffectsRender<LogChart>(BinsProperty, FromMinuteProperty, ToMinuteProperty, WarnProperty, CriticalProperty);

    private const double PadLeft = 38, PadRight = 8, PadTop = 8, PadBottom = 22;
    private double _hoverX = double.NaN;

    public LogChart()
    {
        PointerMoved += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        PointerExited += (_, _) => { _hoverX = double.NaN; InvalidateVisual(); };
    }

    private Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : fallback;

    private static FormattedText Text(string s, double size, Color c, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, new SolidColorBrush(c));

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        var bins = Bins;
        double pw = w - PadLeft - PadRight, ph = h - PadTop - PadBottom;
        if (pw <= 10 || ph <= 10 || bins == null || bins.Count == 0) return;

        var accent = Res("AccentBrush", Color.FromRgb(0xB0, 0x7C, 0xFF));
        var muted = Res("MutedBrush", Color.FromRgb(0x6F, 0x72, 0x82));
        var text = Res("TextBrush", Colors.White);
        var warm = Res("WarmBrush", Color.FromRgb(0xFF, 0xB0, 0x3B));
        var hot = Res("HotBrush", Color.FromRgb(0xFF, 0x4D, 0x5E));

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var b in bins) if (!double.IsNaN(b.Avg)) { lo = Math.Min(lo, b.Min); hi = Math.Max(hi, b.Max); }
        if (lo > hi) return;
        if (!double.IsNaN(Warn) && Warn - hi < 8) hi = Math.Max(hi, Warn);
        double yMin = Math.Floor((lo - 3) / 5) * 5, yMax = Math.Ceiling((hi + 3) / 5) * 5;
        if (yMax - yMin < 15) yMax = yMin + 15;
        double Y(double c) => PadTop + ph - (c - yMin) / (yMax - yMin) * ph;
        double X(int i) => PadLeft + (i + 0.5) / bins.Count * pw;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1);
        double step = (yMax - yMin) > 45 ? 20 : (yMax - yMin) > 24 ? 10 : 5;
        for (double v = Math.Ceiling(yMin / step) * step; v <= yMax; v += step)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(PadLeft, y), new Point(w - PadRight, y));
            var t = Text(Temps.Number(v) + "°", 10.5, muted);
            dc.DrawText(t, new Point(PadLeft - 6 - t.Width, y - t.Height / 2));
        }

        long span = Math.Max(1, ToMinute - FromMinute + 1);
        double XAt(long minute) => PadLeft + (minute - FromMinute) / (double)span * pw;
        // Time axis: spacing adapts to the range (3 h … daily … weekly … monthly).
        double lastLabelRight = double.MinValue;
        foreach (var tick in LogAnalysis.Ticks(FromMinute, ToMinute))
        {
            double x = Math.Round(XAt(tick.Minute)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, PadTop), new Point(x, PadTop + ph));
            var t = Text(tick.Label, 10.5, muted);
            double left = x - t.Width / 2;
            if (left < PadLeft || left + t.Width > w - PadRight || left < lastLabelRight + 10) continue; // no overlaps
            dc.DrawText(t, new Point(left, PadTop + ph + 5));
            lastLabelRight = left + t.Width;
        }

        void Threshold(double c, Color col)
        {
            if (double.IsNaN(c) || c < yMin || c > yMax) return;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, col.R, col.G, col.B)), 1, new DashStyle(new double[] { 4, 4 }, 0));
            double y = Math.Round(Y(c)) + 0.5;
            dc.DrawLine(pen, new Point(PadLeft, y), new Point(w - PadRight, y));
        }
        Threshold(Warn, warm);
        Threshold(Critical, hot);

        var band = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        var line = new Pen(new SolidColorBrush(accent), 1.6, lineJoin: PenLineJoin.Round);
        double half = pw / bins.Count / 2;
        int i0 = 0;
        while (i0 < bins.Count)
        {
            if (double.IsNaN(bins[i0].Avg)) { i0++; continue; }
            int i1 = i0;
            while (i1 + 1 < bins.Count && !double.IsNaN(bins[i1 + 1].Avg)) i1++;
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                bool single = i0 == i1;
                g.BeginFigure(new Point(X(i0) - (single ? half : 0), Y(bins[i0].Max)), true);
                if (single) g.LineTo(new Point(X(i0) + half, Y(bins[i0].Max)));
                for (int i = i0 + 1; i <= i1; i++) g.LineTo(new Point(X(i), Y(bins[i].Max)));
                if (single) g.LineTo(new Point(X(i0) + half, Y(bins[i0].Min)));
                for (int i = i1; i >= i0; i--) g.LineTo(new Point(X(i) - (single && i == i0 ? half : 0), Y(bins[i].Min)));
                g.EndFigure(true);
            }
            dc.DrawGeometry(band, null, area);
            if (i1 > i0)
            {
                var avg = new StreamGeometry();
                using (var g = avg.Open())
                {
                    g.BeginFigure(new Point(X(i0), Y(bins[i0].Avg)), false);
                    for (int i = i0 + 1; i <= i1; i++) g.LineTo(new Point(X(i), Y(bins[i].Avg)));
                    g.EndFigure(false);
                }
                dc.DrawGeometry(null, line, avg);
            }
            else dc.DrawEllipse(new SolidColorBrush(accent), null, new Point(X(i0), Y(bins[i0].Avg)), 1.6, 1.6);
            i0 = i1 + 1;
        }

        if (double.IsNaN(_hoverX) || _hoverX < PadLeft || _hoverX > w - PadRight) return;
        int hb = Math.Clamp((int)((_hoverX - PadLeft) / pw * bins.Count), 0, bins.Count - 1);
        double hx = Math.Round(X(hb)) + 0.5;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1), new Point(hx, PadTop), new Point(hx, PadTop + ph));
        var bin = bins[hb];
        long m0 = FromMinute + (long)(hb * (double)span / bins.Count), m1 = FromMinute + (long)((hb + 1) * (double)span / bins.Count) - 1;
        var a = DateTime.UnixEpoch.AddMinutes(m0).ToLocalTime();
        var z = DateTime.UnixEpoch.AddMinutes(m1).ToLocalTime();
        string when = m1 <= m0
            ? $"{a.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {a.ToString("t", CultureInfo.CurrentCulture)}" // one-minute column
            : a.Date == z.Date
            ? $"{a.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {a.ToString("t", CultureInfo.CurrentCulture)}–{z.ToString("t", CultureInfo.CurrentCulture)}"
            : $"{a.ToString("g", CultureInfo.CurrentCulture)} – {z.ToString("g", CultureInfo.CurrentCulture)}";
        string val = double.IsNaN(bin.Avg) ? "Not recorded" : $"avg {Temps.Number(bin.Avg)}°   peak {Temps.Number(bin.Max)}°   low {Temps.Number(bin.Min)}°";
        var t1 = Text(when, 11, muted);
        var t2 = Text(val, 12, text, bold: true);
        double bw = Math.Max(t1.Width, t2.Width) + 20, bh = t1.Height + t2.Height + 14;
        double bx = hx + 10 + bw > w - PadRight ? hx - 10 - bw : hx + 10;
        var box = new Rect(bx, PadTop + 4, bw, bh);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x24, 0x2C)), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1), box, 8, 8);
        dc.DrawText(t1, new Point(box.X + 10, box.Y + 6));
        dc.DrawText(t2, new Point(box.X + 10, box.Y + 8 + t1.Height));
        if (!double.IsNaN(bin.Avg)) dc.DrawEllipse(new SolidColorBrush(accent), new Pen(Brushes.White, 1.5), new Point(hx, Y(bin.Avg)), 3.5, 3.5);
    }
}
