using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ember.Services.Linux;

/// <summary>What kind of desktop session Ember is running in, and how to reach Ember from outside.</summary>
public static class Desktop
{
    public static string SessionType => (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "").ToLowerInvariant() is { Length: > 0 } t
        ? t : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "wayland" : "x11";

    public static bool IsWayland => SessionType == "wayland";

    private static string Current => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToUpperInvariant();
    public static bool IsGnome => Current.Split(':').Any(p => p is "GNOME" or "UNITY" or "POP" or "UBUNTU");
    public static bool IsKde => Current.Split(':').Contains("KDE");

    public static string Name =>
        IsGnome ? "GNOME" : IsKde ? "KDE Plasma" :
        Current.Contains("XFCE") ? "Xfce" : Current.Contains("CINNAMON") ? "Cinnamon" :
        Current.Contains("MATE") ? "MATE" : Current.Length > 0 ? Current.Split(':')[0] : "your desktop";

    /// <summary>How to start this Ember from a shortcut or autostart entry.</summary>
    public static string LaunchCommand
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage)) return Quote(appImage);
            if (File.Exists("/usr/bin/ember-monitor")) return "ember-monitor";
            return Quote(Environment.ProcessPath ?? "ember-monitor");
        }
    }

    public static string ToggleCommand => LaunchCommand + " --toggle-overlay";

    private static string Quote(string p) => p.Contains(' ') ? $"\"{p}\"" : p;

    /// <summary>Runs a command-line tool and returns its exit code and output (no shell involved).</summary>
    public static (int code, string output) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.HasExited ? p.ExitCode : -1, output);
        }
        catch { return (-1, ""); }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false }); } catch { }
    }

    /// <summary>Opens a web page in the default browser (https only).</summary>
    public static void OpenUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenFolder(url);
    }

    /// <summary>Desktop notification through libnotify (notify-send) or, failing that, D-Bus via gdbus.</summary>
    public static void Notify(string title, string body, bool urgent = true)
    {
        if (Run("notify-send", "-a", "Ember", "-i", "ember-monitor", "-u", urgent ? "critical" : "normal", title, body).code == 0) return;
        Run("gdbus", "call", "--session", "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
            "Ember", "0", "ember-monitor", title, body, "[]", "{}", "10000");
    }
}
