using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Ember.Services;

/// <summary>
/// Ember needs administrator rights for CPU / RAM sensors. To avoid a UAC prompt every time, it
/// uses two Task Scheduler tasks that run with highest privileges (registered once, while elevated):
///  • the logon task  – "Start with Windows" (elevated apps in the Run key are blocked by Windows);
///  • the launch task – on-demand; a normal, non-elevated start of Ember.exe (Start menu, Raycast,
///    desktop shortcut…) just triggers this task, which starts the elevated copy without a prompt.
/// </summary>
public static class StartupService
{
    public const string TaskName = "Ember Temperature Monitor";

    /// <summary>
    /// The launch task is tied to one Ember.exe location (hash of its path), so a second copy
    /// somewhere else (e.g. a dev build) never starts the wrong executable.
    /// </summary>
    public static string LaunchTaskName => "Ember Launcher " + PathHash(ExePath);

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static bool IsEnabled() => Run("/Query", "/TN", Quote(TaskName)) == 0;

    /// <summary>Create or remove the logon task.</summary>
    public static bool Set(bool enable)
    {
        if (!enable) return Run("/Delete", "/TN", Quote(TaskName), "/F") == 0 || !IsEnabled();
        return Register(TaskName, "Starts Ember temperature monitor at logon.", "--tray", logon: true);
    }

    /// <summary>Create (or refresh) the on-demand launch task. Must be called while elevated.</summary>
    public static bool EnsureLaunchTask() =>
        Register(LaunchTaskName, "Opens Ember without a UAC prompt. Ember.exe triggers this when started normally.", "--show", logon: false);

    /// <summary>Start the elevated copy through the launch task. Returns false if the task isn't there.</summary>
    public static bool RunLaunchTask() => Run("/Run", "/TN", Quote(LaunchTaskName)) == 0;

    /// <summary>Remove both tasks (used by the uninstaller via `Ember.exe --cleanup`).</summary>
    public static void RemoveAll()
    {
        Run("/Delete", "/TN", Quote(TaskName), "/F");
        Run("/Delete", "/TN", Quote(LaunchTaskName), "/F");
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ------------------------------------------------------------------

    private static bool Register(string name, string description, string args, bool logon)
    {
        try
        {
            var exe = ExePath;
            var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            var trigger = logon
                ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>"
                : "";
            // Parallel: the launch task's own earlier run is usually the Ember that's still open;
            // a new run must still start (it then just tells that instance to show its window).
            var policy = logon ? "IgnoreNew" : "Parallel";
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>{SecurityElement.Escape(description)}</Description></RegistrationInfo>
  {trigger}
  <Principals>
    <Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>{policy}</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{args}</Arguments><WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory></Exec>
  </Actions>
</Task>";
            var tmp = Path.Combine(Path.GetTempPath(), $"ember_task_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var rc = Run("/Create", "/TN", Quote(name), "/XML", Quote(tmp), "/F");
            try { File.Delete(tmp); } catch { }
            return rc == 0;
        }
        catch { return false; }
    }

    private static string PathHash(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return string.Concat(bytes.Take(4).Select(b => b.ToString("x2")));
    }

    private static string Quote(string s) => "\"" + s + "\"";

    private static int Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", string.Join(' ', args))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
