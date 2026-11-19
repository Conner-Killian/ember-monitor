using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ember.Models;
using Ember.Services;
using Ember.Services.Linux;
using Ember.ViewModels;

namespace Ember.Views;

public partial class OverlayWindow : Window
{
    private const int ShadowMargin = 16; // DIPs, matches the XAML margin
    private const int EdgeGap = 6;       // visual gap to the screen edge when pinned to a corner

    private readonly OverlayViewModel _vm = new();
    private readonly OverlayContent _ov;
    private readonly IntPtr _xid;
    private readonly DispatcherTimer _topTimer;
    private bool _dragging;
    private Point _dragOffset;

    public bool IsHiding { get; private set; }
    public bool IsClosed { get; private set; }

    public OverlayWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _ov = this.FindControl<OverlayContent>("Ov")!;
        DataContext = _vm;
        _ov.DataContext = _vm;
        _vm.Update(null);

        _ov.RenderTransformOrigin = RelativePoint.Center;
        _ov.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(240), Easing = new BackEaseOut() },
        };

        // X11 hints must be set before the window is mapped.
        _xid = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        X11.MarkAsOverlay(_xid);

        Opened += (_, _) => { X11.KeepOnTop(_xid); ApplyClickThrough(); Reposition(); };
        _ov.SizeChanged += (_, _) => OnContentSizeChanged();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        if (Screens != null) Screens.Changed += (_, _) => Dispatcher.UIThread.Post(Reposition);

        // Window managers occasionally restack after fullscreen apps or workspace switches.
        _topTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => { if (IsVisible) X11.KeepOnTop(_xid); });
        _topTimer.Start();

        ApplySettings();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            Reposition();
            Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
        }
    }

    // ------------------------------------------------------------------ show / hide
    public void ShowAnimated()
    {
        IsHiding = false;
        if (!IsVisible)
        {
            var t = _ov.Transitions; _ov.Transitions = null;
            _ov.Opacity = 0;
            _ov.RenderTransform = TransformOperations.Parse("scale(0.92)");
            _ov.Transitions = t;
            Show();
            X11.KeepOnTop(_xid);
            ApplyClickThrough();
        }
        _ov.Opacity = 1;
        _ov.RenderTransform = TransformOperations.Parse("scale(1)");
    }

    public void HideAnimated()
    {
        if (!IsVisible || IsHiding) return;
        IsHiding = true;
        _ov.Opacity = 0;
        _ov.RenderTransform = TransformOperations.Parse("scale(0.92)");
        DispatcherTimer.RunOnce(() => { if (IsHiding) { IsHiding = false; Hide(); } }, TimeSpan.FromMilliseconds(190));
    }

    public void UpdateSnapshot(SensorSnapshot snap) => _vm.Update(snap);

    // ------------------------------------------------------------------ size stability
    // Keep the largest size seen ("high-water mark") so the overlay never shrinks and jiggles as
    // numbers change; reset only when the overlay's content settings change.
    private string _layoutSignature = "";

    private void OnContentSizeChanged()
    {
        if (_vm.HasStatus || _vm.Items.Count == 0) return;
        if (_ov.Bounds.Width > _ov.MinWidth + 0.5) _ov.MinWidth = Math.Ceiling(_ov.Bounds.Width);
        if (_ov.Bounds.Height > _ov.MinHeight + 0.5) _ov.MinHeight = Math.Ceiling(_ov.Bounds.Height);
    }

    private static string LayoutSignature(AppSettings s) => string.Join("|",
        s.OverlayLayout, s.OverlayScale, s.Unit, s.OverlayShowCpu, s.OverlayShowGpu, s.OverlayShowRam,
        s.OverlayShowDrives, s.OverlayShowUsage, s.OverlayShowPower, s.OverlayShowLabels, s.PrimaryGpuId,
        string.Join(",", s.Drives.Select(d => d.Id + d.Show + d.Label)));

    public void ApplySettings()
    {
        var sig = LayoutSignature(App.Settings);
        if (sig != _layoutSignature) { _layoutSignature = sig; _ov.MinWidth = 0; _ov.MinHeight = 0; }
        _vm.Update(App.Sensors?.Latest);
        _ov.ApplyAppearance();
        ApplyClickThrough();
        Cursor = App.Settings.OverlayLocked ? Cursor.Default : new Cursor(StandardCursorType.SizeAll);
        Reposition();
    }

    private void ApplyClickThrough() => X11.SetClickThrough(_xid, App.Settings.OverlayLocked);

    // ------------------------------------------------------------------ positioning
    // Same rules as on Windows: corner pins sit inside the work area; a dragged overlay stays where it
    // was dropped (panels allowed), grows toward the screen's interior and never leaves the screen.

    private double Scale => RenderScaling > 0 ? RenderScaling : 1;
    private int Px(double dip) => (int)Math.Round(dip * Scale);

    public static string ScreenId(Screen s) => $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}";

    public void Reposition()
    {
        if (IsClosed || Screens == null || Screens.All.Count == 0) return;
        int w = Px(ClientSize.Width), h = Px(ClientSize.Height);
        if (w <= 0 || h <= 0 || _dragging) return;
        var (x, y) = Target(w, h);
        if (Position.X != x || Position.Y != y) Position = new PixelPoint(x, y);
    }

    private (int x, int y) Target(int w, int h)
    {
        var s = App.Settings;
        int sh = Px(ShadowMargin), gap = Px(EdgeGap);
        int vw = w - 2 * sh, vh = h - 2 * sh;
        int vx, vy;
        PixelRect area;

        var pt = new PixelPoint((int)s.OverlayFreeX, (int)s.OverlayFreeY);
        var freeScreen = s.OverlayAnchor == OverlayAnchor.Free && !double.IsNaN(s.OverlayFreeX) && !double.IsNaN(s.OverlayFreeY)
            ? Screens!.All.FirstOrDefault(sc => Contains(sc.Bounds, pt))
            : null;

        if (freeScreen != null)
        {
            area = freeScreen.Bounds; // whole display, panels included
            vx = s.OverlayGrowLeft ? pt.X - vw : pt.X;
            vy = s.OverlayGrowUp ? pt.Y - vh : pt.Y;
        }
        else
        {
            // Corner pin – or a dragged position whose display is missing right now (asleep or
            // unplugged): shown top-right on the main display meanwhile, but the saved position is
            // kept so it returns when that display does.
            var screen = (s.OverlayAnchor == OverlayAnchor.Free ? null : Screens!.All.FirstOrDefault(sc => ScreenId(sc) == s.OverlayMonitor))
                         ?? Screens!.Primary ?? Screens.All[0];
            area = screen.WorkingArea;
            var anchor = s.OverlayAnchor == OverlayAnchor.Free ? OverlayAnchor.TopRight : s.OverlayAnchor;
            bool right = anchor is OverlayAnchor.TopRight or OverlayAnchor.BottomRight;
            bool bottom = anchor is OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight;
            vx = right ? area.Right - vw - gap : area.X + gap;
            vy = bottom ? area.Bottom - vh - gap : area.Y + gap;
        }

        vx = vw >= area.Width ? area.X : Math.Clamp(vx, area.X, area.Right - vw);
        vy = vh >= area.Height ? area.Y : Math.Clamp(vy, area.Y, area.Bottom - vh);
        return (vx - sh, vy - sh);
    }

    // Points on a display's far edge (stored for "grow left/up") must still count as on it.
    private static bool Contains(PixelRect r, PixelPoint p) => p.X >= r.X && p.Y >= r.Y && p.X <= r.Right && p.Y <= r.Bottom;

    // ------------------------------------------------------------------ dragging (unlocked)
    // Moved by hand rather than by the window manager: "dock" windows can't be WM-dragged everywhere.

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (App.Settings.OverlayLocked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
        _dragging = true;
        _dragOffset = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var screenPt = this.PointToScreen(e.GetPosition(this));
        Position = new PixelPoint(screenPt.X - Px(_dragOffset.X), screenPt.Y - Px(_dragOffset.Y));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        SaveDraggedPosition();
    }

    private void SaveDraggedPosition()
    {
        if (Screens == null) return;
        var s = App.Settings;
        int sh = Px(ShadowMargin);
        int w = Px(ClientSize.Width), h = Px(ClientSize.Height);
        var v = new PixelRect(Position.X + sh, Position.Y + sh, w - 2 * sh, h - 2 * sh);
        var screen = Screens.ScreenFromPoint(v.Center) ?? Screens.Primary ?? Screens.All[0];
        var b = screen.Bounds;
        int x = v.Width >= b.Width ? b.X : Math.Clamp(v.X, b.X, b.Right - v.Width);
        int y = v.Height >= b.Height ? b.Y : Math.Clamp(v.Y, b.Y, b.Bottom - v.Height);
        s.OverlayMonitor = ScreenId(screen);
        s.OverlayAnchor = OverlayAnchor.Free;
        s.OverlayGrowLeft = x + v.Width / 2 > b.X + b.Width / 2;
        s.OverlayGrowUp = y + v.Height / 2 > b.Y + b.Height / 2;
        s.OverlayFreeX = s.OverlayGrowLeft ? x + v.Width : x;
        s.OverlayFreeY = s.OverlayGrowUp ? y + v.Height : y;
        App.NotifySettingsChanged();
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _topTimer.Stop();
        base.OnClosed(e);
    }
}
