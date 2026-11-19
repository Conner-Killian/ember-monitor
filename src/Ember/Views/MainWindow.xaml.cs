using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ember.Services;
using Ember.Views.Pages;

namespace Ember.Views;

public partial class MainWindow : Window
{
    private DashboardPage? _dashboard;
    private SensorsPage? _sensors;
    private OverlayPage? _overlay;
    private SettingsPage? _settings;
    private bool _forceClose, _closeNow;

    public MainWindow()
    {
        InitializeComponent();
        var s = App.Settings;
        // Never open larger than the screen (e.g. size saved on a big monitor, opened on a laptop).
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(s.MainWidth, Math.Max(MinWidth, wa.Width));
        Height = Math.Min(s.MainHeight, Math.Max(MinHeight, wa.Height));

        SourceInitialized += (_, _) => ThemeService.ApplyBackdrop(this, s.Backdrop);
        StateChanged += (_, _) =>
        {
            // WindowChrome windows overhang the screen when maximized – pad them back in.
            var t = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            RootBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(t.Left + 2, t.Top + 2, t.Right + 2, t.Bottom + 2) : new Thickness(0);
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
            App.Current.UpdateOverlayVisibility();
        };
        Activated += (_, _) => App.Current.UpdateOverlayVisibility();
        Deactivated += (_, _) => App.Current.UpdateOverlayVisibility();
        App.SettingsChanged += SyncChrome;
        Closed += (_, _) => App.SettingsChanged -= SyncChrome;

        ShowPage(_dashboard = new DashboardPage());
        SyncChrome();
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        // Only the page on screen is refreshed; a page catches up when it's shown (ShowPage).
        if (PageHost.Content == _dashboard) _dashboard?.UpdateSnapshot(snap);
        if (PageHost.Content == _sensors) _sensors?.UpdateSnapshot(snap);
        if (PageHost.Content == _overlay) _overlay?.UpdateSnapshot(snap);
    }

    private void SyncChrome()
    {
        var s = App.Settings;
        OverlayQuickToggle.IsChecked = s.OverlayVisible;
        HotkeyHint.Text = $"Toggle with {HotkeyService.Describe(s.Hotkey)}";
    }

    // ------------------------------------------------------------------ page transitions
    private int _pageVersion;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private static DoubleAnimation Anim(double to, int ms, IEasingFunction ease) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

    private void ShowPage(UserControl page)
    {
        if (App.Sensors?.Latest is { } snap)
        {
            if (page is DashboardPage d) d.UpdateSnapshot(snap);
            if (page is SensorsPage sp) sp.UpdateSnapshot(snap);
            if (page is OverlayPage op) op.UpdateSnapshot(snap);
        }

        if (PageHost.Content == null || !IsLoaded)
        {
            PageHost.Content = page;
            return;
        }

        // Quick fade-out of the old page, swap, then fade + slide the new one in.
        int version = ++_pageVersion;
        var outFade = Anim(0, 90, EaseIn);
        outFade.Completed += (_, _) =>
        {
            if (version != _pageVersion) return; // a newer click took over
            PageHost.Content = page;
            PageShift.Y = 14;
            PageHost.BeginAnimation(OpacityProperty, Anim(1, 240, EaseOut));
            PageShift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 300, EaseOut));
        };
        PageHost.BeginAnimation(OpacityProperty, outFade);
        PageShift.BeginAnimation(TranslateTransform.YProperty, Anim(-6, 90, EaseIn));
    }

    // ------------------------------------------------------------------ window open / close
    private bool _closingAnimated;

    public void PlayOpenAnimation()
    {
        _closingAnimated = false;
        RootBorder.Opacity = 0;
        WinScale.ScaleX = WinScale.ScaleY = 0.97;
        WinShift.Y = 16;
        RootBorder.BeginAnimation(OpacityProperty, Anim(1, 220, EaseOut));
        WinScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 320, EaseOut));
        WinScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 320, EaseOut));
        WinShift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 320, EaseOut));
    }

    private void PlayCloseAnimation(Action done)
    {
        _closingAnimated = true;
        var fade = Anim(0, 160, EaseIn);
        fade.Completed += (_, _) => { if (_closingAnimated) done(); };
        RootBorder.BeginAnimation(OpacityProperty, fade);
        WinScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.97, 160, EaseIn));
        WinScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.97, 160, EaseIn));
        WinShift.BeginAnimation(TranslateTransform.YProperty, Anim(10, 160, EaseIn));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (sender == NavDashboard) ShowPage(_dashboard ??= new DashboardPage());
        else if (sender == NavSensors) ShowPage(_sensors ??= new SensorsPage());
        else if (sender == NavOverlay) ShowPage(_overlay ??= new OverlayPage());
        else if (sender == NavSettings) ShowPage(_settings ??= new SettingsPage());
    }

    public void NavigateToSettings() => NavSettings.IsChecked = true;

    private void OverlayQuickToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.OverlayVisible = OverlayQuickToggle.IsChecked == true;
        App.NotifySettingsChanged();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void ForceClose() { _forceClose = true; Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            App.Settings.MainWidth = Width;
            App.Settings.MainHeight = Height;
        }
        App.Settings.Save();
        if (!_forceClose && !_closeNow)
        {
            // Animate out first. Then either hide to the tray (window kept alive, cheaper to re-show)
            // or really close, which exits Ember when tray mode is off.
            e.Cancel = true;
            if (!_closingAnimated)
                PlayCloseAnimation(() =>
                {
                    if (App.Settings.CloseToTray)
                    {
                        Hide();
                        App.Current.UpdateOverlayVisibility();
                    }
                    else
                    {
                        _closeNow = true;
                        Close();
                    }
                });
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!App.Settings.CloseToTray && !_forceClose) App.Current.ExitApp();
    }
}
