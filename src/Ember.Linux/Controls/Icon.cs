using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>
/// Small line icons drawn as vector strokes on a 24×24 grid (Linux has no Segoe icon font).
/// Colour follows the inherited text colour unless Foreground is set.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 1.8);

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    static Icon() => AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeProperty);

    public Icon() { Width = 16; Height = 16; }

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["dashboard"] = "M3 12h4l3-8 4 16 3-8h4",
        ["list"] = "M9 6h12 M9 12h12 M9 18h12 M4 6h.01 M4 12h.01 M4 18h.01",
        ["monitor"] = "M3 4h18v12H3z M8 20h8 M12 16v4",
        ["gear"] = "M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0 M12 2.5v3 M12 18.5v3 M2.5 12h3 M18.5 12h3 M5.3 5.3l2.1 2.1 M16.6 16.6l2.1 2.1 M5.3 18.7l2.1-2.1 M16.6 7.4l2.1-2.1 M5.5 12a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0 -13 0",
        ["refresh"] = "M20 12a8 8 0 1 1 -2.4 -5.7 M20 4v5h-5",
        ["warning"] = "M12 3.5L21.5 20H2.5Z M12 10v4 M12 17h.01",
        ["drive"] = "M3 13h18v6H3z M3 13l3-8h12l3 8 M7 16h.01 M11 16h.01",
        ["thermometer"] = "M14 4a2 2 0 0 0 -4 0v10.3a4 4 0 1 0 4 0z M12 9v8",
        ["lock"] = "M5 11h14v10H5z M8 11V7a4 4 0 0 1 8 0v4",
        ["unlock"] = "M5 11h14v10H5z M8 11V7a4 4 0 0 1 7.6 -1.7",
        ["chevron"] = "M6 9l6 6 6-6",
        ["search"] = "M4 10a6 6 0 1 0 12 0a6 6 0 1 0 -12 0 M20 20l-5.5-5.5",
        ["arrow"] = "M12 19V5 M5 12l7-7 7 7",
        ["cpu"] = "M6 6h12v12H6z M9.5 9.5h5v5h-5z M9 2v4 M15 2v4 M9 18v4 M15 18v4 M2 9h4 M2 15h4 M18 9h4 M18 15h4",
        ["gpu"] = "M2 6h20v11H2z M6 17v3 M14 17v3 M7 11.5a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0 -5 0 M15.5 9.5h3 M15.5 13.5h3",
        ["memory"] = "M3 7h18v10H3z M7 17v3 M12 17v3 M17 17v3 M7 10.5v3 M10.3 10.5v3 M13.6 10.5v3 M17 10.5v3",
        ["board"] = "M4 4h16v16H4z M8 8h4v4H8z M15 8h1.5 M15 11.5h1.5 M8 16h8",
        ["device"] = "M5 3h14v18H5z M9 7h6 M9 11h6 M12 17h.01",
        ["copy"] = "M9 9h11v11H9z M5 15H4V4h11v1",
        ["keyboard"] = "M2 6h20v12H2z M6 10h.01 M10 10h.01 M14 10h.01 M18 10h.01 M7 14h10",
        ["folder"] = "M3 6h6l2 2h10v11H3z",
        ["check"] = "M5 12.5l4.5 4.5L19 7.5",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public override void Render(DrawingContext dc)
    {
        if (Kind == null || !Paths.TryGetValue(Kind, out var data)) return;
        if (!Cache.TryGetValue(Kind, out var geo)) Cache[Kind] = geo = Geometry.Parse(data);
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (s <= 0) return;
        var brush = Foreground ?? Brushes.White;
        double ox = (Bounds.Width - 24 * s) / 2, oy = (Bounds.Height - 24 * s) / 2;
        using (dc.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            dc.DrawGeometry(null, new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
    }
}
