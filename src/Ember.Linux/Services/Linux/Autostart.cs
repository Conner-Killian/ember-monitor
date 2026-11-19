using System;
using System.IO;

namespace Ember.Services.Linux;

/// <summary>"Start on login" through an XDG autostart entry (works on GNOME, KDE, Xfce, Cinnamon, MATE…).</summary>
public static class Autostart
{
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "ember-monitor.desktop");

    public static bool IsEnabled() => File.Exists(FilePath);

    public static bool Set(bool enable)
    {
        try
        {
            if (!enable) { if (File.Exists(FilePath)) File.Delete(FilePath); return true; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"""
                [Desktop Entry]
                Type=Application
                Name=Ember
                Comment=Start Ember's temperature overlay in the background
                Exec={Desktop.LaunchCommand} --tray
                Icon=ember-monitor
                Terminal=false
                X-GNOME-Autostart-enabled=true
                X-GNOME-Autostart-Delay=3
                X-KDE-autostart-after=panel

                """);
            return true;
        }
        catch { return false; }
    }
}
