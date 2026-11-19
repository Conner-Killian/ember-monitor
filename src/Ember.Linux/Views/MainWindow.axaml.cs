using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Ember.Services;
using Ember.Services.Linux;
using Ember.Views.Pages;

namespace Ember.Views;

public partial class MainWindow : Window
{
    private DashboardPage? _dashboard;
    private SensorsPage? _sensors;
    private OverlayPage? _overlay;
    private SettingsPage? _settings;
    private bool _forceClose;
    private readonly TransitioningContentControl _host;
    private readonly Grid _root;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _host = this.FindControl<TransitioningContentControl>("PageHost")!;
        _root = this.FindControl<Grid>("RootGrid")!;
        var s = App.Settings;
        // Never open larger than the screen.
        var wa = Screens.Primary?.WorkingArea;
        double scale = Screens.Primary?.Scaling ?? 1;
        Width = wa is { } a ? Math.Min(s.MainWidth, Math.Max(MinWidth, a.Width / scale)) : s.MainWidth;
        Height = wa is { } b ? Math.Min(s.MainHeight, Math.Max(MinHeight, b.Height / scale)) : s.MainHeight;

        _root.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut() },
        };

        Opened += (_, _) => X11.PreferDarkTitleBar(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        Activated += (_, _) => App.Current.UpdateOverlayVisibility();
        Deactivated += (_, _) => App.Current.UpdateOverlayVisibility();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) App.Current.UpdateOverlayVisibility(); };
        App.SettingsChanged += SyncChrome;
        Closed += (_, _) => App.SettingsChanged -= SyncChrome;

        ShowPage(_dashboard = new DashboardPage());
        SyncChrome();
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        // Only the page on screen is refreshed; a page catches up when it's shown.
        if (_host.Content == _dashboard) _dashboard?.UpdateSnapshot(snap);
        else if (_host.Content == _sensors) _sensors?.UpdateSnapshot(snap);
        else if (_host.Content == _overlay) _overlay?.UpdateSnapshot(snap);
    }

    private void SyncChrome()
    {
        var s = App.Settings;
        this.FindControl<ToggleSwitch>("OverlayQuickToggle")!.IsChecked = s.OverlayVisible;
        this.FindControl<LayoutTransformControl>("ScaleHost")!.LayoutTransform = new ScaleTransform(s.UiScale, s.UiScale);
        this.FindControl<TextBlock>("HotkeyHint")!.Text = OverlayPage.HotkeySummary();
    }

    private void ShowPage(UserControl page)
    {
        if (App.Sensors?.Latest is { } snap)
        {
            if (page is DashboardPage d) d.UpdateSnapshot(snap);
            if (page is SensorsPage sp) sp.UpdateSnapshot(snap);
            if (page is OverlayPage op) op.UpdateSnapshot(snap);
        }
        _host.Content = page;
    }

    private void Nav_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb || _host == null) return;
        if (rb.Name == "NavDashboard") ShowPage(_dashboard ??= new DashboardPage());
        else if (rb.Name == "NavSensors") ShowPage(_sensors ??= new SensorsPage());
        else if (rb.Name == "NavOverlay") ShowPage(_overlay ??= new OverlayPage());
        else if (rb.Name == "NavSettings") ShowPage(_settings ??= new SettingsPage());
    }

    public void Navigate(string name)
    {
        if (this.FindControl<RadioButton>(name) is { } rb) rb.IsChecked = true;
    }

    public void PlayOpenAnimation()
    {
        var t = _root.Transitions; _root.Transitions = null;
        _root.Opacity = 0;
        _root.RenderTransform = TransformOperations.Parse("scale(0.97) translateY(16px)");
        _root.Transitions = t;
        _root.Opacity = 1;
        _root.RenderTransform = TransformOperations.Parse("scale(1) translateY(0px)");
    }

    private void OverlayQuickToggle_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.OverlayVisible = this.FindControl<ToggleSwitch>("OverlayQuickToggle")!.IsChecked == true;
        App.NotifySettingsChanged();
    }

    public void ForceClose() { _forceClose = true; Close(); }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (WindowState == WindowState.Normal) { App.Settings.MainWidth = Width; App.Settings.MainHeight = Height; }
        App.Settings.Save();
        if (!_forceClose && App.Settings.CloseToTray)
        {
            // Keep running in the background (overlay, alerts, tray). Launching Ember again reopens this.
            e.Cancel = true;
            Hide();
            App.Current.UpdateOverlayVisibility();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_forceClose) App.Current.ExitApp();
    }
}
