using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Ember.Models;

namespace Ember.Services;

/// <summary>Accent colour, UI scale and severity colours (shared by overlay + dashboard).</summary>
public static class ThemeService
{
    public static readonly string[] AccentPresets =
    {
        "#B07CFF", "#FF4D6D", "#F5B400", "#3DDC97", "#2EC4FF", "#6C8CFF", "#FF7A45", "#E8E8E8",
    };

    public static Color ParseColor(string hex, Color fallback) => Color.TryParse(hex, out var c) ? c : fallback;

    public static void Apply(AppSettings s)
    {
        var res = Application.Current!.Resources;
        var accent = ParseColor(s.AccentColor, Color.FromRgb(0xB0, 0x7C, 0xFF));
        res["AccentColor"] = accent;
        res["AccentBrush"] = new ImmutableSolidColorBrush(accent);
        res["AccentSoftBrush"] = new ImmutableSolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        res["AccentLightBrush"] = new ImmutableSolidColorBrush(Lerp(accent, Colors.White, 0.25));
        res["AccentForegroundBrush"] = new ImmutableSolidColorBrush(Luma(accent) > 0.6 ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White);
        // Fluent theme controls (toggle switches, sliders, checkboxes, focus rings) follow these.
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Lerp(accent, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Lerp(accent, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Lerp(accent, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Lerp(accent, Colors.Black, 0.15);
        res["SystemAccentColorDark2"] = Lerp(accent, Colors.Black, 0.30);
        res["SystemAccentColorDark3"] = Lerp(accent, Colors.Black, 0.45);
        res["UiScale"] = s.UiScale;
    }

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static readonly Color CoolColor = Color.FromRgb(0x4C, 0xD9, 0xA0);
    public static readonly Color WarmColor = Color.FromRgb(0xFF, 0xB0, 0x3B);
    public static readonly Color HotColor = Color.FromRgb(0xFF, 0x4D, 0x5E);

    public static readonly IBrush CoolBrush = new ImmutableSolidColorBrush(CoolColor);
    public static readonly IBrush WarmBrush = new ImmutableSolidColorBrush(WarmColor);
    public static readonly IBrush HotBrush = new ImmutableSolidColorBrush(HotColor);
    public static readonly IBrush NeutralBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7));

    public static IBrush BrushFor(Severity s)
    {
        if (!App.Settings.AlertsColor) return NeutralBrush;
        return s switch { Severity.Hot => HotBrush, Severity.Warm => WarmBrush, _ => NeutralBrush };
    }

    public static Color GaugeColorFor(Severity s) =>
        !App.Settings.AlertsColor ? ParseColor(App.Settings.AccentColor, CoolColor)
        : s switch { Severity.Hot => HotColor, Severity.Warm => WarmColor, _ => ParseColor(App.Settings.AccentColor, CoolColor) };
}
