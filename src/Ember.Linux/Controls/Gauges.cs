using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>Circular arc gauge with animated value (0..1).</summary>
public sealed class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<RingGauge, double>(nameof(Value));
    public static readonly StyledProperty<double> ShownProperty = AvaloniaProperty.Register<RingGauge, double>("Shown");
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<RingGauge, double>(nameof(Thickness), 8);
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<RingGauge, Color>(nameof(Color), Colors.OrangeRed);
    public static readonly StyledProperty<double> SweepProperty = AvaloniaProperty.Register<RingGauge, double>(nameof(Sweep), 270);
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<RingGauge, bool>(nameof(Animate), true);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Sweep { get => GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    /// <summary>Animate value changes (off for the overlay to keep its redraw cost minimal).</summary>
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    private static readonly IBrush Track = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    static RingGauge() => AffectsRender<RingGauge>(ShownProperty, ThicknessProperty, ColorProperty, SweepProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) SetValue(ShownProperty, Math.Clamp(Value, 0, 1));
        else if (change.Property == AnimateProperty) Transitions = Animate ? Smooth(ShownProperty, 450) : null;
    }

    public RingGauge() { Transitions = Smooth(ShownProperty, 450); }

    internal static Transitions Smooth(AvaloniaProperty p, int ms) =>
        new() { new DoubleTransition { Property = p, Duration = TimeSpan.FromMilliseconds(ms), Easing = new CubicEaseOut() } };

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height, size = Math.Min(w, h);
        if (size <= 0) return;
        double t = Thickness, r = (size - t) / 2;
        var c = new Point(w / 2, h / 2);
        double sweep = Math.Clamp(Sweep, 10, 360);
        double start = 90 + (360 - sweep) / 2;
        DrawArc(dc, new Pen(Track, t, lineCap: PenLineCap.Round), c, r, start, sweep);

        double shown = GetValue(ShownProperty);
        if (shown > 0.001)
        {
            var col = Color;
            var light = Color.FromArgb(col.A, (byte)Math.Min(255, col.R + 40), (byte)Math.Min(255, col.G + 40), (byte)Math.Min(255, col.B + 40));
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(light, 0), new GradientStop(col, 1) },
            };
            DrawArc(dc, new Pen(brush, t, lineCap: PenLineCap.Round), c, r, start, Math.Max(0.5, sweep * shown));
        }
    }

    private static void DrawArc(DrawingContext dc, IPen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99) { dc.DrawEllipse(null, pen, c, r, r); return; }
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Rounded horizontal bar with animated fill (0..1).</summary>
public sealed class FractionBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<FractionBar, double>(nameof(Value));
    public static readonly StyledProperty<double> ShownProperty = AvaloniaProperty.Register<FractionBar, double>("Shown");
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<FractionBar, Color>(nameof(Color), Colors.OrangeRed);
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<FractionBar, bool>(nameof(Animate), true);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    private static readonly IBrush Track = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    static FractionBar() => AffectsRender<FractionBar>(ShownProperty, ColorProperty);

    public FractionBar() { Height = 6; Transitions = RingGauge.Smooth(ShownProperty, 400); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) SetValue(ShownProperty, Math.Clamp(Value, 0, 1));
        else if (change.Property == AnimateProperty) Transitions = Animate ? RingGauge.Smooth(ShownProperty, 400) : null;
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), h / 2));
        double shown = GetValue(ShownProperty);
        if (shown <= 0.001) return;
        var c = Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x99, c.R, c.G, c.B), 0), new GradientStop(c, 1) },
        };
        dc.DrawRectangle(brush, null, new RoundedRect(new Rect(0, 0, Math.Max(h, w * shown), h), h / 2));
    }
}

/// <summary>Smooth history graph with gradient fill.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<Sparkline, Color>(nameof(Color), Colors.OrangeRed);
    public static readonly StyledProperty<double> MinSpanProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(MinSpan), 10);
    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 120);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double MinSpan { get => GetValue(MinSpanProperty); set => SetValue(MinSpanProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    private static readonly IPen GridPen = new Avalonia.Media.Immutable.ImmutablePen(
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1,
        new Avalonia.Media.Immutable.ImmutableDashStyle(new double[] { 3, 4 }, 0));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, ColorProperty, MinSpanProperty, CapacityProperty);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }
        var vals = Values;
        int cap = Math.Max(2, Math.Min(Capacity, Math.Max(60, vals?.Count ?? 0)));
        if (vals == null || vals.Count < 2) return;
        int n = Math.Min(vals.Count, cap), off = vals.Count - n;

        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = off; i < vals.Count; i++) { lo = Math.Min(lo, vals[i]); hi = Math.Max(hi, vals[i]); }
        if (hi - lo < MinSpan) { double mid = (hi + lo) / 2; lo = mid - MinSpan / 2; hi = mid + MinSpan / 2; }
        double pad = (hi - lo) * 0.12; lo -= pad; hi += pad;

        double step = w / (cap - 1), x0 = w - (n - 1) * step;
        Point Pt(int i) => new(x0 + i * step, h - (Math.Clamp(vals[off + i], lo, hi) - lo) / (hi - lo) * (h - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var last = Pt(n - 1);
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var p0 = Pt(0);
            l.BeginFigure(p0, false);
            a.BeginFigure(new Point(p0.X, h), true);
            a.LineTo(p0);
            for (int i = 1; i < n; i++)
            {
                var prev = Pt(i - 1); var cur = Pt(i);
                var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
                l.QuadraticBezierTo(prev, mid);
                a.QuadraticBezierTo(prev, mid);
            }
            l.LineTo(last);
            a.LineTo(last);
            a.LineTo(new Point(last.X, h));
            l.EndFigure(false);
            a.EndFigure(true);
        }
        var c = Color;
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x55, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
        };
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.8, lineJoin: PenLineJoin.Round), line);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)), null, last, 6, 6);
        dc.DrawEllipse(new SolidColorBrush(c), null, last, 3, 3);
    }
}
