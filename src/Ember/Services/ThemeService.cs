using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Ember.Models;

namespace Ember.Services;

/// <summary>Applies accent / backdrop customisation to the shared resource dictionary and windows.</summary>
public static class ThemeService
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int L, R, T, B; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public static readonly string[] AccentPresets =
    {
        "#B07CFF", // violet (default)
        "#FF4D6D", // rose
        "#F5B400", // amber
        "#3DDC97", // mint
        "#2EC4FF", // sky
        "#6C8CFF", // indigo
        "#FF7A45", // ember orange
        "#E8E8E8", // mono
    };

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    public static void Apply(AppSettings s)
    {
        var res = Application.Current.Resources;
        var accent = ParseColor(s.AccentColor, Color.FromRgb(0xB0, 0x7C, 0xFF));
        var accentSoft = Color.FromArgb(0x33, accent.R, accent.G, accent.B);
        var accentLight = Lerp(accent, Colors.White, 0.25);

        res["AccentColor"] = accent;
        res["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        res["AccentSoftBrush"] = Freeze(new SolidColorBrush(accentSoft));
        res["AccentLightBrush"] = Freeze(new SolidColorBrush(accentLight));
        res["AccentForegroundBrush"] = Freeze(new SolidColorBrush(Luma(accent) > 0.6 ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White));
        var grad = new LinearGradientBrush(accentLight, accent, 0);
        res["AccentGradientBrush"] = Freeze(grad);

        // Window tint: for Solid backdrop the tint is fully opaque.
        byte a = s.Backdrop == Backdrop.Solid ? (byte)255 : (byte)Math.Round(Math.Clamp(s.TintOpacity, 0, 1) * 255);
        res["WindowTintBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(a, 0x12, 0x13, 0x18)));
        res["UiScale"] = s.UiScale;

        foreach (Window w in Application.Current.Windows)
            if (w is Views.MainWindow mw) ApplyBackdrop(mw, s.Backdrop);
    }

    /// <summary>Enables dark title-bar, rounded corners and Mica / Acrylic system backdrop.</summary>
    public static void ApplyBackdrop(Window w, Backdrop b)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int type = b switch { Backdrop.Mica => 2, Backdrop.Acrylic => 3, _ => 1 };
        bool supported = Environment.OSVersion.Version.Build >= 22621;
        if (!supported) type = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));

        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null)
            src.CompositionTarget.BackgroundColor = type == 1 ? Color.FromRgb(0x12, 0x13, 0x18) : Colors.Transparent;
        var m = type == 1 ? new MARGINS() : new MARGINS { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);

        // With the frame extended into the client area DWM paints its own caption buttons on top of
        // ours. Dropping WS_SYSMENU hides them (minimise/maximise/close remain via our buttons,
        // Alt+F4 and the taskbar).
        const int GWL_STYLE = -16, WS_SYSMENU = 0x80000;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_SYSMENU) != 0) SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
    }

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // Severity colours (shared by overlay + dashboard)
    public static readonly Color CoolColor = Color.FromRgb(0x4C, 0xD9, 0xA0);
    public static readonly Color WarmColor = Color.FromRgb(0xFF, 0xB0, 0x3B);
    public static readonly Color HotColor = Color.FromRgb(0xFF, 0x4D, 0x5E);

    public static readonly SolidColorBrush CoolBrush = Freeze(new SolidColorBrush(CoolColor));
    public static readonly SolidColorBrush WarmBrush = Freeze(new SolidColorBrush(WarmColor));
    public static readonly SolidColorBrush HotBrush = Freeze(new SolidColorBrush(HotColor));
    public static readonly SolidColorBrush NeutralBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)));

    public static Brush BrushFor(Severity s, bool colorize = true)
    {
        if (!colorize || !App.Settings.AlertsColor) return NeutralBrush;
        return s switch { Severity.Hot => HotBrush, Severity.Warm => WarmBrush, _ => NeutralBrush };
    }

    public static Color GaugeColorFor(Severity s) =>
        !App.Settings.AlertsColor ? ParseColor(App.Settings.AccentColor, CoolColor)
        : s switch { Severity.Hot => HotColor, Severity.Warm => WarmColor, _ => ParseColor(App.Settings.AccentColor, CoolColor) };
}
