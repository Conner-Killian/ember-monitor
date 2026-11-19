using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ember.Models;
using Ember.Services;
using Ember.ViewModels;
using Screen = System.Windows.Forms.Screen;

namespace Ember.Views;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const int ShadowMargin = 16; // DIPs, matches XAML margin
    private const int EdgeGap = 6;       // visual gap to screen edge (DIPs) in addition to shadow margin

    private readonly OverlayViewModel _vm = new();
    private readonly ScaleTransform PopScale = new(1, 1);
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Ov.DataContext = _vm;
        Ov.RenderTransform = PopScale;
        _vm.Update(null);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        SizeChanged += (_, _) =>
        {
            Reposition();
            // …and once more after the native resize has happened, using the real window size.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
        };
        Ov.SizeChanged += OnContentSizeChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) Reposition(); };
        MouseLeftButtonDown += OnDragStart;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Re-assert topmost regularly: other topmost windows (e.g. the taskbar when clicked) can cover us.
        _topmostTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => BringToTop(), Dispatcher);
        _topmostTimer.Start();

        // Clicking the taskbar (or anything else) raises it above us within the topmost band. React
        // the instant the foreground window changes instead of waiting for the timer.
        _winEventProc = (_, _, _, _, _, _, _) => RaiseAfterForegroundChange();
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        ApplySettings();
    }

    // Displays going to sleep can drop out of the desktop for a moment (DisplayPort especially) and
    // come back a little later, sometimes one at a time. Re-check a few times as the layout settles.
    // The saved position is never changed here, so the overlay returns to its place once its display is back.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RepositionSoon);

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(RepositionSoon);
    }

    private int _settleVersion;
    private async void RepositionSoon()
    {
        int v = ++_settleVersion;
        Reposition();
        foreach (var ms in new[] { 500, 1500, 4000 })
        {
            await System.Threading.Tasks.Task.Delay(ms);
            if (v != _settleVersion || IsClosed) return;
            Reposition();
        }
    }

    // ------------------------------------------------------------------ stay on top
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private readonly WinEventDelegate _winEventProc;
    private readonly IntPtr _winEventHook;

    private void BringToTop()
    {
        if (IsVisible && _hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private async void RaiseAfterForegroundChange()
    {
        // Raise now, and again shortly after in case the shell re-orders itself once more.
        BringToTop();
        await System.Threading.Tasks.Task.Delay(30);
        BringToTop();
        await System.Threading.Tasks.Task.Delay(150);
        BringToTop();
    }

    // ------------------------------------------------------------------ show / hide animation
    public bool IsHiding { get; private set; }

    /// <summary>True once the window is gone (app exit, or Windows closing it at sign-out / shutdown).</summary>
    public bool IsClosed { get; private set; }

    public void ShowAnimated()
    {
        IsHiding = false;
        if (!IsVisible)
        {
            Opacity = 0;
            PopScale.ScaleX = PopScale.ScaleY = 0.92;
            Show();
        }
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        var grow = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    public void HideAnimated()
    {
        if (!IsVisible || IsHiding) return;
        IsHiding = true;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (IsHiding) { IsHiding = false; Hide(); } };
        var shrink = new System.Windows.Media.Animation.DoubleAnimation(0.92, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        BeginAnimation(OpacityProperty, fade);
    }

    public void UpdateSnapshot(SensorSnapshot snap) => _vm.Update(snap);

    // ---- size stability ----
    // Readings change width as numbers change (41 → 100, 3% → 12%). Tabular digits handle most of
    // it; on top of that the overlay keeps its largest size ("high-water mark") so it never shrinks
    // and jiggles back and forth. The mark resets only when the overlay's content settings change.
    private string _layoutSignature = "";

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only remember sizes of real readings (not the "Starting…" placeholder).
        if (_vm.HasStatus || _vm.Items.Count == 0) return;
        if (Ov.ActualWidth > Ov.MinWidth + 0.5) { Ov.MinWidth = Math.Ceiling(Ov.ActualWidth); LogSize(); }
        if (Ov.ActualHeight > Ov.MinHeight + 0.5) { Ov.MinHeight = Math.Ceiling(Ov.ActualHeight); LogSize(); }
    }

    /// <summary>History of high-water-mark changes (diagnostics, shown by --snapshot).</summary>
    public static readonly System.Collections.Generic.List<string> SizeLog = new();
    private void LogSize()
    {
        if (SizeLog.Count < 50) SizeLog.Add($"{DateTime.Now:HH:mm:ss.fff} content min {Ov.MinWidth}x{Ov.MinHeight}, window {ActualWidth:0}x{ActualHeight:0}");
    }

    private static string LayoutSignature(AppSettings s) => string.Join("|",
        s.OverlayLayout, s.OverlayScale, s.Unit, s.OverlayShowCpu, s.OverlayShowGpu, s.OverlayShowRam,
        s.OverlayShowDrives, s.OverlayShowUsage, s.OverlayShowPower, s.OverlayShowLabels, s.PrimaryGpuId,
        string.Join(",", s.Drives.Select(d => d.Id + d.Show + d.Label)));

    public void ApplySettings()
    {
        var sig = LayoutSignature(App.Settings);
        if (sig != _layoutSignature)
        {
            _layoutSignature = sig;
            Ov.MinWidth = 0;
            Ov.MinHeight = 0;
        }
        _vm.Update(App.Sensors?.Latest);
        Ov.ApplyAppearance();
        ApplyClickThrough();
        Cursor = App.Settings.OverlayLocked ? null : Cursors.SizeAll;
        Reposition();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = App.Settings.OverlayLocked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    // ------------------------------------------------------------------ positioning
    //
    // All maths works on the *visible* rectangle (window minus the transparent shadow margin).
    // • Corner pins (chosen on the Overlay page) sit inside the display's work area.
    // • Dragged placement goes exactly where you drop it — no snapping — and may sit over the
    //   taskbar. It remembers which side of the screen it's on so any size change grows toward the
    //   screen's interior, and it is clamped to the display so it can never end up off-screen.

    private int Px(double dip) => (int)Math.Round(dip * Dpi);

    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || !IsLoaded && !IsVisible) return;
        if (!GetWindowRect(_hwnd, out var r)) return;
        // When SizeChanged fires, WPF already knows the new size but the native window hasn't been
        // resized yet, so GetWindowRect still returns the OLD size. Position for WPF's size.
        int w = ActualWidth > 0 ? (int)Math.Round(ActualWidth * Dpi) : r.R - r.L;
        int h = ActualHeight > 0 ? (int)Math.Round(ActualHeight * Dpi) : r.B - r.T;
        if (w <= 0 || h <= 0) return;
        if (TraceReposition && SizeLog.Count < 200)
            SizeLog.Add($"{DateTime.Now:HH:mm:ss.fff} reposition: hwnd {w}x{h}, wpf {ActualWidth * Dpi:0}x{ActualHeight * Dpi:0}");
        var (x, y) = Target(w, h);
        if (r.L != x || r.T != y)
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public static bool TraceReposition;

    /// <summary>Diagnostics: where the window is vs where it should be for its current size.</summary>
    public string CheckPlacement()
    {
        if (!GetWindowRect(_hwnd, out var r)) return "no window";
        var (x, y) = Target(r.R - r.L, r.B - r.T);
        bool ok = Math.Abs(r.L - x) <= 1 && Math.Abs(r.T - y) <= 1;
        return $"{(ok ? "OK " : "OFF")} window at ({r.L},{r.T}) size {r.R - r.L}x{r.B - r.T}, expected ({x},{y})";
    }

    /// <summary>Window position (incl. shadow margin) for a window of the given pixel size.</summary>
    private (int x, int y) Target(int w, int h)
    {
        var s = App.Settings;
        int sh = Px(ShadowMargin), gap = Px(EdgeGap);
        int vw = w - 2 * sh, vh = h - 2 * sh;
        int vx, vy;
        System.Drawing.Rectangle area;

        var freePt = new System.Drawing.Point((int)s.OverlayFreeX, (int)s.OverlayFreeY);
        var freeScreen = s.OverlayAnchor == OverlayAnchor.Free && !double.IsNaN(s.OverlayFreeX) && !double.IsNaN(s.OverlayFreeY)
            ? Screen.AllScreens.FirstOrDefault(sc => Inflate(sc.Bounds).Contains(freePt))
            : null;

        if (freeScreen != null)
        {
            area = freeScreen.Bounds; // full display, taskbar included
            vx = s.OverlayGrowLeft ? freePt.X - vw : freePt.X;
            vy = s.OverlayGrowUp ? freePt.Y - vh : freePt.Y;
        }
        else
        {
            // Corner pin – or a dragged position whose display is currently missing (asleep or
            // unplugged). Then it shows top-right on the main display *for now*; the saved position
            // is kept, so it goes back as soon as that display returns.
            var screen = (s.OverlayAnchor == OverlayAnchor.Free ? null : FindScreen(s.OverlayMonitor, s.OverlayMonitorBounds))
                         ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
            area = screen.WorkingArea;
            var anchor = s.OverlayAnchor == OverlayAnchor.Free ? OverlayAnchor.TopRight : s.OverlayAnchor;
            bool right = anchor is OverlayAnchor.TopRight or OverlayAnchor.BottomRight;
            bool bottom = anchor is OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight;
            vx = right ? area.Right - vw - gap : area.Left + gap;
            vy = bottom ? area.Bottom - vh - gap : area.Top + gap;
        }

        // Never leave the screen.
        vx = vw >= area.Width ? area.Left : Math.Clamp(vx, area.Left, area.Right - vw);
        vy = vh >= area.Height ? area.Top : Math.Clamp(vy, area.Top, area.Bottom - vh);
        return (vx - sh, vy - sh);
    }

    public static string BoundsId(Screen sc) => $"{sc.Bounds.X},{sc.Bounds.Y},{sc.Bounds.Width},{sc.Bounds.Height}";

    /// <summary>
    /// The chosen display. Windows can renumber displays (\\.\DISPLAY2 → \\.\DISPLAY3) when they wake,
    /// so the one in the same place on the desktop wins over a bare name match.
    /// </summary>
    public static Screen? FindScreen(string? deviceName, string? bounds)
    {
        var all = Screen.AllScreens;
        return all.FirstOrDefault(sc => sc.DeviceName == deviceName && BoundsId(sc) == bounds)
            ?? (bounds == null ? null : all.FirstOrDefault(sc => BoundsId(sc) == bounds))
            ?? all.FirstOrDefault(sc => sc.DeviceName == deviceName);
    }

    // Edge points (e.g. the right/bottom edge stored for a "grow left/up" overlay) lie exactly on the
    // display's far boundary, which Rectangle.Contains excludes.
    private static System.Drawing.Rectangle Inflate(System.Drawing.Rectangle b) =>
        new(b.X, b.Y, b.Width + 1, b.Height + 1);

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (App.Settings.OverlayLocked) return;
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Button>(d) != null) return;
        try { DragMove(); } catch { return; }
        SaveDraggedPosition();
    }

    private void SaveDraggedPosition()
    {
        if (!GetWindowRect(_hwnd, out var r)) return;
        var s = App.Settings;
        int sh = Px(ShadowMargin);
        var v = new System.Drawing.Rectangle(r.L + sh, r.T + sh, r.R - r.L - 2 * sh, r.B - r.T - 2 * sh);
        var screen = Screen.FromRectangle(v);
        var b = screen.Bounds;

        // Keep it fully on the display it was dropped on (taskbar area allowed).
        v.X = v.Width >= b.Width ? b.Left : Math.Clamp(v.X, b.Left, b.Right - v.Width);
        v.Y = v.Height >= b.Height ? b.Top : Math.Clamp(v.Y, b.Top, b.Bottom - v.Height);

        // Remember the edge nearest the screen border so future size changes grow inward.
        s.OverlayMonitor = screen.DeviceName;
        s.OverlayMonitorBounds = BoundsId(screen);
        s.OverlayAnchor = OverlayAnchor.Free;
        s.OverlayGrowLeft = v.Left + v.Width / 2 > b.Left + b.Width / 2;
        s.OverlayGrowUp = v.Top + v.Height / 2 > b.Top + b.Height / 2;
        s.OverlayFreeX = s.OverlayGrowLeft ? v.Right : v.Left;
        s.OverlayFreeY = s.OverlayGrowUp ? v.Bottom : v.Top;
        App.NotifySettingsChanged();
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _topmostTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        base.OnClosed(e);
    }
}
