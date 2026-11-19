using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ember.Controls;

/// <summary>Circular arc gauge with animated value (0..1).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, e) => ((RingGauge)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(RingGauge),
        new FrameworkPropertyMetadata(Colors.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Sweep of the gauge in degrees (360 = full ring, 270 = open bottom).</summary>
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    /// <summary>Animate value changes (off for the overlay to keep its redraw cost minimal).</summary>
    public bool Animate { get; set; } = true;

    private void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (!Animate) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, v); return; }
        var anim = new DoubleAnimation(v, TimeSpan.FromMilliseconds(450))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(ShownProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double t = Thickness;
        double r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double sweep = Math.Clamp(Sweep, 10, 360);
        double start = 90 + (360 - sweep) / 2; // degrees, 0 = +X, clockwise (screen coords)

        var trackPen = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArc(dc, trackPen, c, r, start, sweep);

        double shown = (double)GetValue(ShownProperty);
        if (shown > 0.001)
        {
            var col = Color;
            var brush = new LinearGradientBrush(
                Color.FromArgb(col.A, (byte)Math.Min(255, col.R + 40), (byte)Math.Min(255, col.G + 40), (byte)Math.Min(255, col.B + 40)),
                col, new Point(0, 1), new Point(1, 0));
            var pen = new Pen(brush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, pen, c, r, start, Math.Max(0.5, sweep * shown));
        }
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Rounded horizontal bar with animated fill (0..1).</summary>
public sealed class FractionBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(FractionBar),
        new PropertyMetadata(0.0, (d, e) => ((FractionBar)d).AnimateTo((double)e.NewValue)));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(FractionBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(FractionBar),
        new FrameworkPropertyMetadata(Colors.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public FractionBar() { Height = 6; }

    public bool Animate { get; set; } = true;

    private void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (!Animate) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, v); return; }
        BeginAnimation(ShownProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(400))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        double shown = (double)GetValue(ShownProperty);
        double fw = Math.Max(h, w * shown);
        if (shown <= 0.001) return;
        var c = Color;
        var brush = new LinearGradientBrush(Color.FromArgb(0x99, c.R, c.G, c.B), c, 0);
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, fw, h), h / 2, h / 2);
    }
}

/// <summary>Smooth history graph with gradient fill.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(System.Collections.Generic.IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinSpanProperty = DependencyProperty.Register(
        nameof(MinSpan), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
        nameof(FixedMax), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Collections.Generic.IReadOnlyList<double> Values { get => (System.Collections.Generic.IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double MinSpan { get => (double)GetValue(MinSpanProperty); set => SetValue(MinSpanProperty, value); }
    /// <summary>Number of samples that span the full width (newest at right).</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    /// <summary>If set (e.g. 100 for %), the Y axis is fixed to 0..FixedMax.</summary>
    public double FixedMax { get => (double)GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }

    private static readonly Pen GridPen = MakeGridPen();
    private static Pen MakeGridPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        var vals = Values;
        // Start with a 60-sample window and widen it as history accumulates, so the graph
        // looks meaningful right after launch.
        int cap = Math.Max(2, Math.Min(Capacity, Math.Max(60, vals?.Count ?? 0)));
        if (vals == null || vals.Count < 2) return;
        int n = Math.Min(vals.Count, cap);
        int off = vals.Count - n;

        double lo, hi;
        if (!double.IsNaN(FixedMax)) { lo = 0; hi = FixedMax; }
        else
        {
            lo = double.MaxValue; hi = double.MinValue;
            for (int i = off; i < vals.Count; i++) { lo = Math.Min(lo, vals[i]); hi = Math.Max(hi, vals[i]); }
            double span = hi - lo;
            if (span < MinSpan) { double mid = (hi + lo) / 2; lo = mid - MinSpan / 2; hi = mid + MinSpan / 2; }
            double pad = (hi - lo) * 0.12; lo -= pad; hi += pad;
        }

        double step = w / (cap - 1);
        double x0 = w - (n - 1) * step;
        Point Pt(int i) => new(x0 + i * step, h - (Math.Clamp(vals[off + i], lo, hi) - lo) / (hi - lo) * (h - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var last = Pt(n - 1);
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var p0 = Pt(0);
            l.BeginFigure(p0, false, false);
            a.BeginFigure(new Point(p0.X, h), true, true);
            a.LineTo(p0, false, false);
            for (int i = 1; i < n; i++)
            {
                // Smooth with a mid-point quadratic curve
                var prev = Pt(i - 1);
                var cur = Pt(i);
                var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
                l.QuadraticBezierTo(prev, mid, true, true);
                a.QuadraticBezierTo(prev, mid, false, true);
            }
            l.LineTo(last, true, true);
            a.LineTo(last, false, true);
            a.LineTo(new Point(last.X, h), false, false);
        }
        line.Freeze(); area.Freeze();

        var c = Color;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.8) { LineJoin = PenLineJoin.Round }, line);

        // Live dot
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)), null, last, 6, 6);
        dc.DrawEllipse(new SolidColorBrush(c), null, last, 3, 3);
    }
}
