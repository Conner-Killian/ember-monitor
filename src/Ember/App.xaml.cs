using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ember.Models;
using Ember.Services;
using Ember.Views;

namespace Ember;

public partial class App : Application
{
    private const string MutexName = @"Global\Ember.TempMonitor.SingleInstance";
    private const string ShowEventName = @"Global\Ember.TempMonitor.Show";

    public static AppSettings Settings { get; private set; } = new();
    public static SensorService Sensors { get; private set; } = null!;
    public static HotkeyService Hotkey { get; private set; } = null!;
    public static UpdateManager Updates { get; private set; } = new(a => a());
    public static new App Current => (App)Application.Current;

    /// <summary>Raised whenever settings change so windows can refresh.</summary>
    public static event Action? SettingsChanged;

    private Mutex? _mutex;
    private const string ExitEventName = @"Global\Ember.TempMonitor.Exit";
    private EventWaitHandle? _showEvent, _exitEvent;
    private TrayService? _tray;
    private readonly AlertService _alerts = new();
    private OverlayWindow? _overlay;
    private MainWindow? _main;
    private DispatcherTimer? _saveTimer;

    /// <summary>
    /// Show / exit signals of the running (elevated) instance. Created so that the same user's
    /// non-elevated Ember.exe may signal them (medium integrity label, DACL = this user + SYSTEM);
    /// by default Windows blocks lower-integrity processes from touching an elevated app's objects.
    /// </summary>
    private static EventWaitHandle CreateSignal(string name)
    {
        EventWaitHandle h;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
            sec.SetSecurityDescriptorSddlForm($"D:(A;;0x1F0003;;;{id.User!.Value})(A;;0x1F0003;;;SY)");
            h = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
        }
        catch
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        // Objects made by an elevated process carry a High integrity label, which blocks writes from
        // normal processes. Lower it to Medium. (Passing the label at creation time needs
        // SeSecurityPrivilege; setting it afterwards on our own object does not.)
        try { SetMediumLabel(h.SafeWaitHandle.DangerousGetHandle()); }
        catch (Exception ex) { LogError("Lowering signal label failed: " + ex.Message); }
        return h;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr sd, out bool present, out IntPtr sacl, out bool defaulted);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private static void SetMediumLabel(IntPtr handle)
    {
        const int SE_KERNEL_OBJECT = 6;
        const uint LABEL_SECURITY_INFORMATION = 0x10;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;ME)", 1, out var sd, out _))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            if (!GetSecurityDescriptorSacl(sd, out _, out var sacl, out _)) throw new System.ComponentModel.Win32Exception();
            uint rc = SetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (rc != 0) throw new System.ComponentModel.Win32Exception((int)rc);
        }
        finally { LocalFree(sd); }
    }

    public static void LogError(string msg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            var path = System.IO.Path.Combine(AppSettings.Folder, "error.log");
            // Keep the log from growing forever on a PC that hits the same error repeatedly.
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 1_000_000)
                System.IO.File.Move(path, path + ".old", true);
            System.IO.File.AppendAllText(path, $"{DateTime.Now}: {msg}\n");
        }
        catch { }
    }

    /// <summary>`--exit` returns only once the running Ember has really quit (so the installer can replace files).</summary>
    private static void WaitForOtherInstances()
    {
        int me = Environment.ProcessId;
        var until = DateTime.Now.AddSeconds(10);
        while (DateTime.Now < until &&
               System.Diagnostics.Process.GetProcessesByName("Ember").Any(p => p.Id != me))
            Thread.Sleep(150);
    }

    private static bool TrySignal(string name)
    {
        try
        {
            if (!System.Threading.EventWaitHandleAcl.TryOpenExisting(name,
                    System.Security.AccessControl.EventWaitHandleRights.Modify | System.Security.AccessControl.EventWaitHandleRights.Synchronize,
                    out var h)) return false;
            using (h) return h.Set();
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    private const int ASFW_ANY = -1;

    /// <summary>
    /// Update diagnostics (no UI): <c>--verify-update FILE</c> checks FILE.sig; <c>--update-from URL
    /// [--dry-run]</c> runs the update flow against a release feed (URL in GitHub's API format;
    /// http://127.0.0.1 allowed for tests). Results go to update-test.log; the exit code is 0 on success.
    /// </summary>
    private static int? RunUpdateTool(string[] args, bool elevated)
    {
        int vi = Array.FindIndex(args, a => a.Equals("--verify-update", StringComparison.OrdinalIgnoreCase));
        int ui = Array.FindIndex(args, a => a.Equals("--update-from", StringComparison.OrdinalIgnoreCase));
        bool dry = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        if (vi < 0 && ui < 0) return null;
        if (ui >= 0 && !dry && !elevated) return null; // a real install needs the elevated copy
        void Log(string m) { try { System.IO.Directory.CreateDirectory(AppSettings.Folder); System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "update-test.log"), $"{DateTime.Now:HH:mm:ss} {m}\n"); } catch { } }
        try
        {
            if (vi >= 0 && vi + 1 < args.Length)
            {
                var file = args[vi + 1];
                using var fs = System.IO.File.OpenRead(file);
                bool ok = UpdateSignature.Verify(System.IO.Path.GetFileName(file), UpdateSignature.Hash(fs), System.IO.File.ReadAllText(file + ".sig"));
                Log($"verify {System.IO.Path.GetFileName(file)}: {(ok ? "VALID" : "INVALID")}");
                return ok ? 0 : 1;
            }
            if (ui + 1 >= args.Length) return 2;
            var url = args[ui + 1];
            bool loopback = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback;
            var info = System.Threading.Tasks.Task.Run(() => UpdateChecker.GetLatestAsync(url)).GetAwaiter().GetResult();
            Log($"feed {url}: latest {info?.VersionText ?? "none"}, running {UpdateChecker.Format(UpdateChecker.Current)}");
            if (info == null || info.Version <= UpdateChecker.Current) { Log("no newer version"); return 3; }
            if (!UpdateInstaller.HasSignedInstaller(info)) { Log("no signed installer in release"); return 4; }
            if (dry)
            {
                using var locked = System.Threading.Tasks.Task.Run(() => UpdateInstaller.DownloadVerifiedAsync(info, null, default, loopback)).GetAwaiter().GetResult();
                Log($"download verified: {locked.Name}");
                return 0;
            }
            // Start the installer and get out of its way: it replaces this very executable.
            var t = System.Threading.Tasks.Task.Run(() => UpdateInstaller.InstallAsync(info, false, null, default, loopback));
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (!t.IsCompleted && started.Elapsed < TimeSpan.FromMinutes(5) &&
                   !System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.StartsWith(System.IO.Path.GetFileNameWithoutExtension(UpdateInstaller.InstallerName(info.Version)), StringComparison.OrdinalIgnoreCase)))
                Thread.Sleep(100);
            if (t.IsFaulted) { Log("install failed: " + t.Exception!.GetBaseException().Message); return 5; }
            Log("installer started");
            return 0;
        }
        catch (Exception ex)
        {
            Log("failed: " + ex.GetBaseException().Message);
            return 1;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (RunUpdateTool(e.Args, StartupService.IsElevated()) is int toolExit)
        {
            Shutdown(toolExit);
            return;
        }

        // ---- started without admin rights (Start menu, Raycast, shortcut…) ----
        // Hand off to the elevated copy through the pre-approved launch task: no UAC prompt. That copy
        // either starts Ember or, if it's already running, just brings the dashboard to the front.
        if (!StartupService.IsElevated())
        {
            AllowSetForegroundWindow(ASFW_ANY); // let the elevated copy take focus
            bool plainLaunch = e.Args.All(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
            // "--show" means we ARE the launch task's copy, yet still not elevated: the account isn't
            // an administrator (standard user), so the task can't elevate. Running it again would
            // start another copy forever – fall through to the normal UAC prompt instead.
            bool fromLaunchTask = e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
            if (e.Args.Contains("--exit"))
            {
                if (TrySignal(ExitEventName)) WaitForOtherInstances();
            }
            else if (plainLaunch && TrySignal(ShowEventName))
            {
                Thread.Sleep(400); // already running: it shows its window instantly
            }
            else if (!(plainLaunch && !fromLaunchTask && StartupService.RunLaunchTask()))
            {
                // No launch task yet (first run after install) or a diagnostics switch: ask normally.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(StartupService.ExePath,
                        string.Join(' ', e.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                    { UseShellExecute = true, Verb = "runas" });
                }
                catch { /* UAC cancelled */ }
            }
            else
            {
                Thread.Sleep(1200); // keep the foreground permission alive while the elevated copy starts
            }
            Shutdown();
            return;
        }
        if (e.Args.Contains("--cleanup"))
        {
            StartupService.RemoveAll(); // uninstaller
            Shutdown();
            return;
        }
        if (e.Args.Contains("--register"))
        {
            // Installer: set up the no-UAC launch task (and autostart, if enabled) right away.
            StartupService.EnsureLaunchTask();
            if (AppSettings.Load().StartWithWindows) StartupService.Set(true);
            Shutdown();
            return;
        }

        // ---- single instance ----
        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created && e.Args.Contains("--restart"))
        {
            // Previous instance is shutting down – wait for it to release the mutex.
            try { created = _mutex.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { created = true; }
        }
        bool exitRequest = e.Args.Contains("--exit");
        if (!created || exitRequest)
        {
            // --exit asks a running instance to quit (used by the installer / uninstaller).
            bool signalled = false;
            try { using var ev = EventWaitHandle.OpenExisting(exitRequest ? ExitEventName : ShowEventName); signalled = ev.Set(); } catch { }
            if (created) try { _mutex.ReleaseMutex(); } catch { }
            if (exitRequest && signalled) WaitForOtherInstances();
            Shutdown();
            return;
        }
        _showEvent = CreateSignal(ShowEventName);
        _exitEvent = CreateSignal(ExitEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true }.Start();
        new Thread(() =>
        {
            if (_exitEvent.WaitOne()) Dispatcher.BeginInvoke(ExitApp);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            LogError(ex.Exception + "\n");
        };
        // Errors on background threads would otherwise vanish (or end the process) without a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogError("Background thread: " + ex.ExceptionObject + "\n");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { LogError("Task: " + ex.Exception + "\n"); ex.SetObserved(); };

        base.OnStartup(e);

        Settings = AppSettings.Load();
        _started = true;
        ThemeService.Apply(Settings);

        // Keep the autostart task pointing at this executable (handles first run, updates and moved installs).
        bool diagnostics = e.Args.Any(a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        bool startWithWindows = Settings.StartWithWindows && !diagnostics;
        if (!diagnostics && !Settings.FirstRunDone) { Settings.FirstRunDone = true; Settings.Save(); }
        System.Threading.Tasks.Task.Run(() =>
        {
            StartupService.EnsureLaunchTask();
            if (startWithWindows) StartupService.Set(true);
        });

        Sensors = new SensorService(Dispatcher) { IntervalMs = Settings.PollIntervalMs };
        Sensors.Updated += OnSnapshot;
        Sensors.Status += msg => { LogError(msg); _overlay?.ApplySettings(); };

        Hotkey = new HotkeyService();
        Hotkey.SetCombo(Settings.Hotkey);
        Hotkey.Pressed += () => Dispatcher.BeginInvoke(ToggleOverlay);
        Hotkey.Install();

        _tray = new TrayService();
        _tray.OpenRequested += ShowMain;
        _tray.ToggleOverlay += ToggleOverlay;
        _tray.ToggleLock += () => { Settings.OverlayLocked = !Settings.OverlayLocked; NotifySettingsChanged(); };
        _tray.LayoutRequested += l => { Settings.OverlayLayout = l; NotifySettingsChanged(); };
        _tray.UnitRequested += u => { Settings.Unit = u; NotifySettingsChanged(); };
        _tray.ExitRequested += ExitApp;
        _alerts.Notify += (t, m) => _tray?.ShowBalloon(t, m);
        _tray.UpdateRequested += () => _ = Updates.InstallAsync();

        Updates = new UpdateManager(a => Dispatcher.BeginInvoke(a))
        {
            Installer = (info, progress, ct) => UpdateInstaller.InstallAsync(info, MainVisible, progress, ct),
            CanInstallRelease = UpdateInstaller.HasSignedInstaller,
        };
        Updates.Changed += () => _tray?.SetUpdate(Updates.CanInstall && !Updates.IsBusy ? Updates.Latest!.VersionText : null);
        Updates.Found += info =>
        {
            if (Settings.NotifiedUpdateVersion == info.VersionText) return;
            Settings.NotifiedUpdateVersion = info.VersionText;
            Settings.Save();
            _tray?.ShowBalloon("Ember update available",
                $"Ember {info.VersionText} is ready to install. Open Ember, or right-click the tray icon to update.", warning: false);
        };
        _autoUpdates = Settings.AutoCheckUpdates;
        if (!diagnostics) Updates.SetAutomatic(Settings.AutoCheckUpdates);
        System.Threading.Tasks.Task.Run(UpdateInstaller.CleanUp);
        if (e.Args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase)))
            _tray.ShowBalloon("Ember updated", $"You're now running Ember {UpdateChecker.Format(UpdateChecker.Current)}.", warning: false);

        _overlay = new OverlayWindow();
        if (Settings.OverlayVisible) _overlay.ShowAnimated();

        Sensors.Start();

        bool tray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!tray) ShowMain();
        UpdateOverlayVisibility();

        int profIdx = Array.FindIndex(e.Args, a => a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        if (profIdx >= 0 && profIdx + 1 < e.Args.Length)
        {
            var file = e.Args[profIdx + 1];
            if (_main == null) ShowMain();
            Dispatcher.BeginInvoke(async () =>
            {
                try { await SnapshotTool.Profile(file, _main!); }
                catch (Exception ex) { System.IO.File.AppendAllText(file, ex.ToString()); }
                ExitApp();
            });
        }

        int snapIdx = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapIdx >= 0 && snapIdx + 1 < e.Args.Length)
        {
            var dir = e.Args[snapIdx + 1];
            if (_main == null) ShowMain();
            Dispatcher.BeginInvoke(async () =>
            {
                try { await SnapshotTool.Run(dir, _main!); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "error.txt"), ex.ToString()); }
                ExitApp();
            });
        }
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        _overlay?.UpdateSnapshot(snap);
        _main?.UpdateSnapshot(snap);
        _tray?.Update(snap);
        _alerts.Evaluate(snap);
    }

    public void ShowMain()
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => { _main = null; UpdateOverlayVisibility(); };
            if (Sensors.Latest != null) _main.UpdateSnapshot(Sensors.Latest);
        }
        bool wasHidden = !_main.IsVisible;
        _main.Show();
        if (wasHidden) _main.PlayOpenAnimation();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;
        UpdateOverlayVisibility();
    }

    public OverlayWindow? Overlay => _overlay;

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
        if (app._autoUpdates != Settings.AutoCheckUpdates)
        {
            app._autoUpdates = Settings.AutoCheckUpdates;
            Updates.SetAutomatic(Settings.AutoCheckUpdates);
        }
        app._overlay?.ApplySettings();
        app.UpdateOverlayVisibility();
        SettingsChanged?.Invoke();
        if (Sensors.Latest != null) app.OnSnapshot(Sensors.Latest);

        app._saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            app._saveTimer!.Stop();
            Settings.Save();
        }, app.Dispatcher);
        app._saveTimer.Stop();
        app._saveTimer.Start();
    }

    private bool _exiting, _started;
    private bool? _autoUpdates;

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Settings.Save();
        _overlay?.Close();
        _main?.ForceClose();
        Shutdown();
    }

    // Windows is signing out / shutting down: it closes our windows itself, so stop reacting.
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        if (_started) try { Settings.Save(); } catch { }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the real instance may save: a second launch / `--exit` call never loaded the user's
        // settings, and saving its defaults would wipe them.
        if (_started) try { Settings.Save(); } catch { }
        _tray?.Dispose();
        Hotkey?.Dispose();
        Sensors?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
