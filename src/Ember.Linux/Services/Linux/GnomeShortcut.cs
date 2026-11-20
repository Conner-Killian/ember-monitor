using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ember.Services.Linux;

/// <summary>
/// On Wayland no app may watch the whole keyboard, so Ember can't catch its own hotkeys there.
/// On GNOME it registers system keyboard shortcuts instead (Settings → Keyboard → Custom
/// Shortcuts): one that runs `ember-monitor --toggle-overlay`, and one per overlay preset that
/// runs `ember-monitor --preset ID`.
/// </summary>
public static class GnomeShortcut
{
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string BasePath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/";
    public const string DefaultBinding = "<Super><Shift>e";

    /// <summary>The show / hide shortcut (presetId null) or a preset's.</summary>
    private static string ItemPath(string? presetId) => BasePath + (presetId == null ? "ember-monitor/" : $"ember-monitor-preset-{presetId}/");
    private static string ItemSchema(string? presetId) => $"{Schema}.custom-keybinding:{ItemPath(presetId)}";

    // Checked once: it spawns gsettings, and the answer can't change while Ember runs.
    private static readonly Lazy<bool> _available = new(() => Desktop.IsGnome && Desktop.Run("gsettings", "list-keys", Schema).code == 0);
    public static bool Available => _available.Value;

    private static List<string> ReadList()
    {
        var (code, output) = Desktop.Run("gsettings", "get", Schema, "custom-keybindings");
        if (code != 0) return new();
        return Regex.Matches(output, "'([^']*)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static void WriteList(List<string> list) =>
        Desktop.Run("gsettings", "set", Schema, "custom-keybindings", list.Count == 0 ? "@as []" : "[" + string.Join(", ", list.Select(p => $"'{p}'")) + "]");

    /// <summary>Current accelerator ("&lt;Super&gt;&lt;Shift&gt;e") or null if the shortcut isn't set up.</summary>
    public static string? Current(string? presetId = null)
    {
        if (!ReadList().Contains(ItemPath(presetId))) return null;
        var (code, output) = Desktop.Run("gsettings", "get", ItemSchema(presetId), "binding");
        var b = code == 0 ? output.Trim().Trim('\'') : "";
        return b.Length > 0 ? b : null;
    }

    /// <summary>Sets the show / hide shortcut, or (with <paramref name="presetId"/>) a preset's.</summary>
    public static bool Set(string accelerator, string? presetId = null, string? presetName = null)
    {
        var schema = ItemSchema(presetId);
        var name = presetId == null ? "Ember: show / hide overlay" : $"Ember: overlay preset “{presetName}”";
        if (Desktop.Run("gsettings", "set", schema, "name", name).code != 0) return false;
        Desktop.Run("gsettings", "set", schema, "command", presetId == null ? Desktop.ToggleCommand : Desktop.PresetCommand(presetId));
        Desktop.Run("gsettings", "set", schema, "binding", accelerator);
        var list = ReadList();
        if (!list.Contains(ItemPath(presetId)))
        {
            list.Add(ItemPath(presetId));
            WriteList(list);
        }
        return Current(presetId) == accelerator;
    }

    public static void Remove(string? presetId = null)
    {
        var list = ReadList();
        if (list.Remove(ItemPath(presetId))) WriteList(list);
        Desktop.Run("gsettings", "reset-recursively", ItemSchema(presetId));
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
