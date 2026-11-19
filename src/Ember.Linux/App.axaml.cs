using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Ember.Models;
using Ember.Services;
using Ember.Services.Linux;
using Ember.Views;

namespace Ember;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SensorService Sensors { get; private set; } = null!;
    public static HotkeyService Hotkey { get; private set; } = null!;
    /// <summary>Checks GitHub for new releases. On Linux updates come from the package manager or a new AppImage, so there's no installer.</summary>
    public static UpdateManager Updates { get; } = new(a => Dispatcher.UIThread.Post(a));
    private bool? _autoUpdates;
    /// <summary>The temperature log while logging is on (null when off).</summary>
    public static TemperatureLog? Log { get; private set; }
    /// <summary>Raised when the log is opened, closed, resized or deleted.</summary>
    public static event Action? LogChanged;
    private DispatcherTimer? _logTimer;
    private (bool on, int days) _logState = (false, 0);

    // Readings are reduced to one row a minute in memory and written every 10 minutes (and on exit)
    // into a fixed-size file. See TemperatureLog for the details.
    private void ApplyLogSettings()
    {
        var want = (Settings.LogEnabled, Settings.LogRetentionDays);
        if (want == _logState && (Log != null) == want.LogEnabled) return;
        _logState = want;
        try
        {
            if (want.LogEnabled)
            {
                int rows = TemperatureLog.RowsForDays(want.LogRetentionDays);
                if (Log == null) Log = TemperatureLog.Open(TemperatureLog.DefaultPath, rows);
                else if (Log.Capacity != rows) { FlushLog(final: true); Log.Resize(rows); }
                _logTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) => FlushLog(final: false));
                _logTimer.Start();
            }
            else CloseLog();
        }
        catch (Exception ex)
        {
            LogError("Temperature log: " + ex.Message);
            CloseLog();
        }
        LogChanged?.Invoke();
    }

    private static void FlushLog(bool final)
    {
        try { Log?.Flush(DateTime.UtcNow, final); }
        catch (Exception ex) { LogError("Temperature log: " + ex.Message); }
    }

    private void CloseLog()
    {
        _logTimer?.Stop();
        if (Log == null) return;
        FlushLog(final: true);
        Log.Dispose();
        Log = null;
    }

    /// <summary>Deletes the log file (Settings → Delete log). Logging continues into a fresh file if it's on.</summary>
    public static void DeleteLog()
    {
        var app = Current;
        app.CloseLog();
        try { File.Delete(TemperatureLog.DefaultPath); }
        catch (Exception ex) { LogError("Deleting the temperature log: " + ex.Message); }
        app._logState = (false, 0);
        app.ApplyLogSettings();
        LogChanged?.Invoke();
    }
    public static new App Current => (App)Application.Current!;

    /// <summary>Raised whenever settings change so windows can refresh.</summary>
    public static event Action? SettingsChanged;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private readonly AlertService _alerts = new();
    private OverlayWindow? _overlay;
    private MainWindow? _main;
    private DispatcherTimer? _saveTimer;
    private TrayIcon? _tray;
    private NativeMenuItem? _trayOverlay, _trayLock, _trayPill, _trayCard, _trayRings, _trayC, _trayF, _trayUpdate;
    private WindowIcon? _appIcon;
    private string _trayIconKey = "";
    private bool _exiting, _started;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start(desktop.Args ?? Array.Empty<string>());
            desktop.Exit += (_, _) => OnExit();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void LogError(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var path = Path.Combine(AppSettings.Folder, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTime.Now}: {msg}\n");
        }
        catch { }
    }

    private void Start(string[] args)
    {
        bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
        int snapIdx = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        bool diagnostics = snapIdx >= 0;

        if (!diagnostics) SingleInstance.Listen(cmd => Dispatcher.UIThread.Post(() => OnCommand(cmd)));

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError("Unhandled: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { LogError("Task: " + e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) => { LogError(e.Exception.ToString()); e.Handled = true; };

        Settings = AppSettings.Load();
        _started = true;
        ThemeService.Apply(Settings);

        // Start-on-login is on by default, like on Windows (the first run creates the autostart entry).
        if (!diagnostics && !Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            if (Settings.StartWithWindows) Autostart.Set(true);
            Settings.Save();
        }
        else if (!diagnostics && Settings.StartWithWindows) Autostart.Set(true); // keep the path current (AppImage moved, package updated)

        Sensors = new SensorService { IntervalMs = Settings.PollIntervalMs };
        Sensors.Updated += snap => { Log?.Add(snap, DateTime.UtcNow); OnSnapshot(snap); }; // each new reading logged once
        ApplyLogSettings();
        Sensors.Status += msg => { LogError(msg); _overlay?.ApplySettings(); };

        Hotkey = new HotkeyService();
        Hotkey.SetCombo(Settings.Hotkey);
        Hotkey.Pressed += () => Dispatcher.UIThread.Post(ToggleOverlay);
        Hotkey.Install();

        _alerts.Notify += (t, m) => Desktop.Notify(t, m);
        SetupTray();

        Updates.Changed += SyncTrayMenu;
        Updates.Found += info =>
        {
            if (Settings.NotifiedUpdateVersion == info.VersionText) return;
            Settings.NotifiedUpdateVersion = info.VersionText;
            Settings.Save();
            Desktop.Notify("Ember update available",
                $"Ember {info.VersionText} is out. Update with your package manager, or download it from the release page.", urgent: false);
        };
        _autoUpdates = Settings.AutoCheckUpdates;
        if (!diagnostics) Updates.SetAutomatic(Settings.AutoCheckUpdates);

        _overlay = new OverlayWindow();
        if (Settings.OverlayVisible) _overlay.ShowAnimated();

        Sensors.Start();
        if (!Has("--tray")) ShowMain();
        UpdateOverlayVisibility();

        if (diagnostics && snapIdx + 1 < args.Length)
        {
            var dir = args[snapIdx + 1];
            if (_main == null) ShowMain();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await SnapshotTool.Run(dir, _main!); }
                catch (Exception ex) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
                ExitApp();
            });
        }
    }

    /// <summary>Commands from other launches of ember-monitor (menu, keyboard shortcut, autostart).</summary>
    private void OnCommand(string cmd)
    {
        switch (cmd)
        {
            case "show": ShowMain(); break;
            case "toggle": ToggleOverlay(); break;
            case "exit": ExitApp(); break;
        }
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        _overlay?.UpdateSnapshot(snap);
        _main?.UpdateSnapshot(snap);
        UpdateTray(snap);
        _alerts.Evaluate(snap);
    }

    public OverlayWindow? Overlay => _overlay;

    public void ShowMain()
    {
        if (_exiting) return;
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => { _main = null; UpdateOverlayVisibility(); };
        }
        bool wasHidden = !_main.IsVisible;
        _main.Show();
        if (wasHidden) _main.PlayOpenAnimation();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (Sensors.Latest != null) _main.UpdateSnapshot(Sensors.Latest);
        UpdateOverlayVisibility();
    }

    public bool MainVisible => _main is { IsVisible: true } && _main.WindowState != WindowState.Minimized;

    public void ToggleOverlay()
    {
        Settings.OverlayVisible = !Settings.OverlayVisible;
        NotifySettingsChanged();
    }

    public void UpdateOverlayVisibility()
    {
        if (_exiting) return;
        if (Sensors != null) Sensors.LowPower = !MainVisible;
        if (_overlay == null || _overlay.IsClosed) return;
        bool show = Settings.OverlayVisible && !(Settings.OverlayHideWhenMainOpen && MainVisible && _main!.IsActive);
        if (show && (!_overlay.IsVisible || _overlay.IsHiding)) _overlay.ShowAnimated();
        else if (!show && _overlay.IsVisible && !_overlay.IsHiding) _overlay.HideAnimated();
    }

    /// <summary>Call after mutating <see cref="Settings"/>. Applies changes everywhere and saves (debounced).</summary>
    public static void NotifySettingsChanged()
    {
        var app = Current;
        ThemeService.Apply(Settings);
        Sensors.IntervalMs = Settings.PollIntervalMs;
        Hotkey.SetCombo(Settings.Hotkey);
        app.ApplyLogSettings();
        if (app._autoUpdates != Settings.AutoCheckUpdates)
        {
            app._autoUpdates = Settings.AutoCheckUpdates;
            Updates.SetAutomatic(Settings.AutoCheckUpdates);
        }
        app._overlay?.ApplySettings();
        app.UpdateOverlayVisibility();
        app.SyncTrayMenu();
        SettingsChanged?.Invoke();
        if (Sensors.Latest != null) app.OnSnapshot(Sensors.Latest);

        app._saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            app._saveTimer!.Stop();
            Settings.Save();
        });
        app._saveTimer.Stop();
        app._saveTimer.Start();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Settings.Save();
        CloseLog();
        _overlay?.Close();
        _main?.ForceClose();
        if (_tray != null) _tray.IsVisible = false;
        _desktop?.Shutdown();
    }

    private void OnExit()
    {
        if (_started) try { Settings.Save(); } catch { }
        CloseLog();
        Hotkey?.Dispose();
        Sensors?.Dispose();
        SingleInstance.Stop();
    }

    // ------------------------------------------------------------------ tray
    // A StatusNotifierItem: shown by KDE, Xfce, Cinnamon, MATE, and by GNOME with the
    // "AppIndicator and KStatusNotifierItem Support" extension (pre-installed on Ubuntu).

    private void SetupTray()
    {
        try
        {
            using (var s = AssetLoader.Open(new Uri("avares://ember-monitor/Assets/ember.png"))) _appIcon = new WindowIcon(s);
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Ember"); open.Click += (_, _) => ShowMain();
            _trayOverlay = new NativeMenuItem("Show overlay") { ToggleType = NativeMenuItemToggleType.CheckBox };
            _trayOverlay.Click += (_, _) => ToggleOverlay();
            _trayLock = new NativeMenuItem("Lock overlay (click-through)") { ToggleType = NativeMenuItemToggleType.CheckBox };
            _trayLock.Click += (_, _) => { Settings.OverlayLocked = !Settings.OverlayLocked; NotifySettingsChanged(); };

            var style = new NativeMenuItem("Overlay style") { Menu = new NativeMenu() };
            NativeMenuItem Layout(string text, OverlayLayout l)
            {
                var i = new NativeMenuItem(text) { ToggleType = NativeMenuItemToggleType.Radio };
                i.Click += (_, _) => { Settings.OverlayLayout = l; NotifySettingsChanged(); };
                style.Menu.Add(i);
                return i;
            }
            _trayPill = Layout("Pill", OverlayLayout.Pill); _trayCard = Layout("Card", OverlayLayout.Card); _trayRings = Layout("Rings", OverlayLayout.Rings);

            var units = new NativeMenuItem("Units") { Menu = new NativeMenu() };
            NativeMenuItem Unit(string text, TempUnit u)
            {
                var i = new NativeMenuItem(text) { ToggleType = NativeMenuItemToggleType.Radio };
                i.Click += (_, _) => { Settings.Unit = u; NotifySettingsChanged(); };
                units.Menu.Add(i);
                return i;
            }
            _trayC = Unit("Celsius (°C)", TempUnit.Celsius); _trayF = Unit("Fahrenheit (°F)", TempUnit.Fahrenheit);

            var exit = new NativeMenuItem("Exit"); exit.Click += (_, _) => ExitApp();
            _trayUpdate = new NativeMenuItem("Download update") { IsVisible = false };
            _trayUpdate.Click += (_, _) => Desktop.OpenUrl(Updates.Latest?.PageUrl ?? UpdateChecker.ReleasesPage);
            menu.Add(open); menu.Add(new NativeMenuItemSeparator());
            menu.Add(_trayOverlay); menu.Add(_trayLock); menu.Add(style); menu.Add(units);
            menu.Add(new NativeMenuItemSeparator()); menu.Add(_trayUpdate); menu.Add(exit);

            _tray = new TrayIcon { Icon = _appIcon, ToolTipText = "Ember", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            SyncTrayMenu();
        }
        catch (Exception ex) { LogError("Tray: " + ex.Message); }
    }

    private void SyncTrayMenu()
    {
        if (_tray == null) return;
        var s = Settings;
        _trayOverlay!.IsChecked = s.OverlayVisible;
        var keys = Views.Pages.OverlayPage.ShortcutText();
        var header = keys == null ? "Show overlay" : $"Show overlay ({keys})";
        if (_trayOverlay.Header != header) _trayOverlay.Header = header;
        _trayLock!.IsChecked = s.OverlayLocked;
        _trayPill!.IsChecked = s.OverlayLayout == OverlayLayout.Pill;
        _trayCard!.IsChecked = s.OverlayLayout == OverlayLayout.Card;
        _trayRings!.IsChecked = s.OverlayLayout == OverlayLayout.Rings;
        _trayC!.IsChecked = s.Unit == TempUnit.Celsius;
        _trayF!.IsChecked = s.Unit == TempUnit.Fahrenheit;
        bool update = Updates.IsAvailable;
        if (_trayUpdate!.IsVisible != update) _trayUpdate.IsVisible = update;
        if (update && _trayUpdate.Header != $"Download Ember {Updates.Latest!.VersionText}…")
            _trayUpdate.Header = $"Download Ember {Updates.Latest!.VersionText}…";
    }

    private void UpdateTray(SensorSnapshot snap)
    {
        if (_tray == null) return;
        var tip = $"Ember — CPU {Temps.Format(snap.CpuTemp)} · GPU {Temps.Format(snap.GpuTemp)}";
        if (_tray.ToolTipText != tip) _tray.ToolTipText = tip;

        if (!Settings.TrayShowsTemp)
        {
            if (_trayIconKey != "") { _tray.Icon = _appIcon; _trayIconKey = ""; }
            return;
        }
        var text = Temps.Number(snap.CpuTemp);
        var sev = Temps.Rate(snap.CpuTemp, Settings.CpuThresholds);
        var key = text + sev;
        if (key == _trayIconKey) return;
        _trayIconKey = key;
        try { _tray.Icon = new WindowIcon(RenderTempIcon(text, sev, 64)); } catch { }
    }

    /// <summary>The temperature drawn as a path scaled to fill a square icon (readable at tray size).</summary>
    public static Bitmap RenderTempIcon(string text, Severity sev, int size)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size));
        var col = sev switch { Severity.Hot => ThemeService.HotColor, Severity.Warm => ThemeService.WarmColor, _ => Colors.White };
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("fonts:Inter#Inter", FontStyle.Normal, FontWeight.Bold), 100, Brushes.White);
        var geo = ft.BuildGeometry(new Point(0, 0));
        using (var dc = rtb.CreateDrawingContext())
        {
            if (geo != null && geo.Bounds.Width > 0)
            {
                var b = geo.Bounds;
                double pad = size * 0.04;
                double scale = Math.Min((size - 2 * pad) / b.Width, size * 0.78 / b.Height);
                var m = Matrix.CreateTranslation(-(b.X + b.Width / 2), -(b.Y + b.Height / 2)) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(size / 2.0, size / 2.0);
                using (dc.PushTransform(m))
                {
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), size / 10.0 / scale, lineJoin: PenLineJoin.Round), geo);
                    dc.DrawGeometry(new SolidColorBrush(col), null, geo);
                }
            }
        }
        return rtb;
    }
}
