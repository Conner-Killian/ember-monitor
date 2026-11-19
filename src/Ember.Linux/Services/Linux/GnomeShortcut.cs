using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ember.Services.Linux;

/// <summary>
/// On Wayland no app may watch the whole keyboard, so Ember can't catch its own hotkey there.
/// On GNOME it registers a system keyboard shortcut instead (Settings → Keyboard → Custom
/// Shortcuts), which runs `ember-monitor --toggle-overlay`.
/// </summary>
public static class GnomeShortcut
{
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string ItemPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/ember-monitor/";
    private static string ItemSchema => $"{Schema}.custom-keybinding:{ItemPath}";
    public const string DefaultBinding = "<Super><Shift>e";

    // Checked once: it spawns gsettings, and the answer can't change while Ember runs.
    private static readonly Lazy<bool> _available = new(() => Desktop.IsGnome && Desktop.Run("gsettings", "list-keys", Schema).code == 0);
    public static bool Available => _available.Value;

    private static List<string> ReadList()
    {
        var (code, output) = Desktop.Run("gsettings", "get", Schema, "custom-keybindings");
        if (code != 0) return new();
        return Regex.Matches(output, "'([^']*)'").Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Current accelerator ("&lt;Super&gt;&lt;Shift&gt;e") or null if Ember's shortcut isn't set up.</summary>
    public static string? Current()
    {
        if (!ReadList().Contains(ItemPath)) return null;
        var (code, output) = Desktop.Run("gsettings", "get", ItemSchema, "binding");
        var b = code == 0 ? output.Trim().Trim('\'') : "";
        return b.Length > 0 ? b : null;
    }

    public static bool Set(string accelerator)
    {
        if (Desktop.Run("gsettings", "set", ItemSchema, "name", "Ember: show / hide overlay").code != 0) return false;
        Desktop.Run("gsettings", "set", ItemSchema, "command", Desktop.ToggleCommand);
        Desktop.Run("gsettings", "set", ItemSchema, "binding", accelerator);
        var list = ReadList();
        if (!list.Contains(ItemPath))
        {
            list.Add(ItemPath);
            Desktop.Run("gsettings", "set", Schema, "custom-keybindings", "[" + string.Join(", ", list.Select(p => $"'{p}'")) + "]");
        }
        return Current() == accelerator;
    }

    public static void Remove()
    {
        var list = ReadList();
        if (list.Remove(ItemPath))
            Desktop.Run("gsettings", "set", Schema, "custom-keybindings", list.Count == 0 ? "@as []" : "[" + string.Join(", ", list.Select(p => $"'{p}'")) + "]");
        Desktop.Run("gsettings", "reset-recursively", ItemSchema);
    }

    /// <summary>"&lt;Super&gt;&lt;Shift&gt;e" → "Super + Shift + E".</summary>
    public static string Describe(string accel)
    {
        var mods = Regex.Matches(accel, "<([^>]+)>").Select(m => m.Groups[1].Value switch
        {
            "Primary" or "Control" or "Ctrl" => "Ctrl", "Super" => "Super", "Alt" => "Alt", "Shift" => "Shift", var x => x,
        });
        var key = Regex.Replace(accel, "<[^>]+>", "");
        key = key.Length == 1 ? key.ToUpperInvariant() : key;
        return string.Join(" + ", mods.Append(key));
    }
}
