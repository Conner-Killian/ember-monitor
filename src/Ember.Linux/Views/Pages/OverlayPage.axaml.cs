using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Ember.Controls;
using Ember.Models;
using Ember.Services;
using Ember.Services.Linux;
using Ember.ViewModels;

namespace Ember.Views.Pages;

public partial class OverlayPage : UserControl
{
    private readonly OverlayViewModel _previewVm = new();
    private bool _loading, _recording;
    private readonly HashSet<int> _recDown = new(), _recCombo = new();
    private readonly ObservableCollection<DriveRow> _drives = new();
    private TopLevel? _recTop;

    public sealed class DriveRow : Observable
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        private bool _show; private string _label = "";
        public bool Show { get => _show; set => Set(ref _show, value); }
        public string Label { get => _label; set => Set(ref _label, value); }
    }

    /// <summary>How the overlay hotkey works in this session.</summary>
    /// <summary>
    /// Global: hotkeys work like on Windows (X11, or Wayland once keyboard access is allowed).
    /// Blocked: Wayland, waiting for that one-time permission (the "Turn on hotkeys" row).
    /// Gnome / Manual: the keyboard can't be read at all; fall back to a desktop shortcut.
    /// </summary>
    private enum HotkeyMode { Global, Blocked, Gnome, Manual }
    private static HotkeyMode Mode =>
        App.Hotkey?.IsActive == true ? HotkeyMode.Global
        : App.Hotkey?.EvdevBlocked == true ? HotkeyMode.Blocked
        : GnomeShortcut.Available ? HotkeyMode.Gnome : HotkeyMode.Manual;

    private static string? _gnomeBinding;
    private static bool _gnomeRead;
    private static string? GnomeBinding
    {
        get { if (!_gnomeRead) { _gnomeBinding = GnomeShortcut.Current(); _gnomeRead = true; } return _gnomeBinding; }
    }

    /// <summary>The overlay shortcut as currently set, or null when there isn't one (tray menu).</summary>
    public static string? ShortcutText() => Mode switch
    {
        HotkeyMode.Global when App.Settings.Hotkey.Count > 0 => HotkeyService.Describe(App.Settings.Hotkey),
        HotkeyMode.Gnome when GnomeBinding is string b => GnomeShortcut.Describe(b),
        _ => null,
    };

    /// <summary>One-line hint for the sidebar.</summary>
    public static string HotkeySummary() => Mode switch
    {
        HotkeyMode.Global => "Toggle with " + HotkeyService.Describe(App.Settings.Hotkey),
        HotkeyMode.Blocked => "Turn on hotkeys on the Overlay page",
        HotkeyMode.Gnome => GnomeBinding is string b ? "Toggle with " + GnomeShortcut.Describe(b) : "Set a shortcut on the Overlay page",
        _ => "Bind a keyboard shortcut on the Overlay page",
    };

    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public OverlayPage()
    {
        AvaloniaXamlLoader.Load(this);
        C<OverlayContent>("Preview").DataContext = _previewVm;
        C<ItemsControl>("DriveList").ItemsSource = _drives;
        C<ItemsControl>("PresetList").ItemsSource = _presets;
        Load();
        App.SettingsChanged += () => { if (!_loading) Load(); };
        DetachedFromVisualTree += (_, _) => StopRecording(false);
    }

    private void Load()
    {
        _loading = true;
        var s = App.Settings;
        C<ToggleSwitch>("ShowToggle").IsChecked = s.OverlayVisible;
        C<RadioButton>("LayoutPill").IsChecked = s.OverlayLayout == OverlayLayout.Pill;
        C<RadioButton>("LayoutCard").IsChecked = s.OverlayLayout == OverlayLayout.Card;
        C<RadioButton>("LayoutRings").IsChecked = s.OverlayLayout == OverlayLayout.Rings;
        C<RadioButton>("AnchorTL").IsChecked = s.OverlayAnchor == OverlayAnchor.TopLeft;
        C<RadioButton>("AnchorTR").IsChecked = s.OverlayAnchor == OverlayAnchor.TopRight;
        C<RadioButton>("AnchorBL").IsChecked = s.OverlayAnchor == OverlayAnchor.BottomLeft;
        C<RadioButton>("AnchorBR").IsChecked = s.OverlayAnchor == OverlayAnchor.BottomRight;
        C<TextBlock>("AnchorDesc").Text = s.OverlayAnchor == OverlayAnchor.Free
            ? "Currently placed where you dragged it. Pick a corner to pin it there instead."
            : "Pin the overlay to a corner of the selected display.";
        C<CheckBox>("ShowCpu").IsChecked = s.OverlayShowCpu;
        C<CheckBox>("ShowGpu").IsChecked = s.OverlayShowGpu;
        C<CheckBox>("ShowRam").IsChecked = s.OverlayShowRam;
        C<CheckBox>("ShowDrives").IsChecked = s.OverlayShowDrives;
        C<CheckBox>("ShowUsage").IsChecked = s.OverlayShowUsage;
        C<CheckBox>("ShowPower").IsChecked = s.OverlayShowPower;
        C<CheckBox>("ShowLabels").IsChecked = s.OverlayShowLabels;
        C<ToggleSwitch>("HideWhenMain").IsChecked = s.OverlayHideWhenMainOpen;
        C<Slider>("OpacitySlider").Value = s.OverlayOpacity;
        C<Slider>("ScaleSlider").Value = s.OverlayScale;
        C<TextBlock>("OpacityText").Text = $"{s.OverlayOpacity * 100:0}%";
        C<TextBlock>("ScaleText").Text = $"{s.OverlayScale * 100:0}%";
        C<StackPanel>("DrivesPanel").IsVisible = s.OverlayShowDrives;
        C<TextBlock>("LockText").Text = s.OverlayLocked ? "Unlock to move" : "Lock in place";
        C<Icon>("LockIcon").Kind = s.OverlayLocked ? "unlock" : "lock";
        LoadHotkey();

        var screens = TopLevel.GetTopLevel(this)?.Screens ?? App.Current.Overlay?.Screens;
        var all = screens?.All.ToList() ?? new();
        var combo = C<ComboBox>("MonitorCombo");
        combo.ItemsSource = all.Select((sc, i) => $"Display {i + 1}{(sc.IsPrimary ? " (main)" : "")}  ·  {sc.Bounds.Width}×{sc.Bounds.Height}").ToList();
        int idx = all.FindIndex(sc => OverlayWindow.ScreenId(sc) == s.OverlayMonitor);
        if (idx < 0) idx = all.FindIndex(sc => sc.IsPrimary);
        combo.SelectedIndex = Math.Max(0, idx);
        combo.IsVisible = all.Count > 1;

        LoadPresets();

        C<OverlayContent>("Preview").ApplyAppearance();
        _previewVm.Update(App.Sensors?.Latest);
        _loading = false;
    }

    // ------------------------------------------------------------------ presets

    private readonly ObservableCollection<PresetRow> _presets = new();

    private void LoadPresets()
    {
        var s = App.Settings;
        var mode = Mode;
        C<TextBlock>("PresetIntro").Text = "Save the overlay's current style, position, readings and look as a preset. Switch to it here, from the tray menu, or " +
            (mode == HotkeyMode.Manual ? "with a keyboard shortcut that runs its command." : "with its own hotkey.") +
            " Using a preset's shortcut while that preset is showing hides the overlay.";
        CollectionSync.Sync(_presets, s.OverlayPresets, p => p.Id, r => r.Id,
            p => new PresetRow { Id = p.Id, Name = p.Name },
            (row, p) =>
            {
                row.Summary = p.Describe(DisplayName(p));
                row.IsActive = s.OverlayVisible && p.Matches(s);
                row.Command = mode == HotkeyMode.Manual ? Desktop.PresetCommand(p.Id) : "";
                string? keys = mode == HotkeyMode.Gnome ? (p.ShortcutAccel is { } a ? GnomeShortcut.Describe(a) : null)
                             : p.Hotkey.Count > 0 ? HotkeyService.Describe(p.Hotkey) : null;
                row.HasHotkey = keys != null;
                if (!row.IsRecording) row.HotkeyText = keys ?? "No hotkey";
            });
        C<Border>("PresetEmpty").IsVisible = _presets.Count == 0;
    }

    /// <summary>"Display 2" for the display a preset puts the overlay on.</summary>
    private string DisplayName(OverlayPreset p)
    {
        var all = (TopLevel.GetTopLevel(this)?.Screens ?? App.Current.Overlay?.Screens)?.All.ToList();
        if (all == null || all.Count == 0) return "a display";
        int i = all.FindIndex(sc => OverlayWindow.ScreenId(sc) == p.Monitor);
        if (i < 0 && p.Monitor == null) i = all.FindIndex(sc => sc.IsPrimary);
        return i >= 0 ? $"Display {i + 1}" : "a display that isn't connected";
    }

    private static OverlayPreset? PresetOf(object? sender) =>
        sender is Control { DataContext: PresetRow row } ? App.Settings.OverlayPresets.FirstOrDefault(p => p.Id == row.Id) : null;
    private static PresetRow? RowOf(object? sender) => (sender as Control)?.DataContext as PresetRow;

    private void PresetAdd_Click(object? sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        int n = s.OverlayPresets.Count + 1;
        while (s.OverlayPresets.Any(p => p.Name == $"Preset {n}")) n++;
        s.OverlayPresets.Add(OverlayPreset.Capture(s, $"Preset {n}"));
        Commit();
        // Straight into naming it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var list = C<ItemsControl>("PresetList");
            if (list.ContainerFromIndex(_presets.Count - 1) is Control c &&
                c.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void PresetApply_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetOf(sender) is { } p) App.Current.ApplyPreset(p.Id);
    }

    private void PresetUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetOf(sender) is not { } p) return;
        p.CopyFrom(App.Settings);
        Commit();
    }

    private async void PresetDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetOf(sender) is not { } p || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await ConfirmDialog.Ask(owner, "Delete preset", $"Delete the preset \"{p.Name}\"?", "Delete")) return;
        StopRecording(false);
        if (p.ShortcutAccel != null) GnomeShortcut.Remove(p.Id);
        App.Settings.OverlayPresets.Remove(p);
        Commit();
    }

    private void PresetName_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SavePresetName(sender); TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); }
        else if (e.Key == Key.Escape && RowOf(sender) is { } row && PresetOf(sender) is { } p) { row.Name = p.Name; TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); }
    }

    private void PresetName_LostFocus(object? sender, RoutedEventArgs e) => SavePresetName(sender);

    private void SavePresetName(object? sender)
    {
        if (RowOf(sender) is not { } row || PresetOf(sender) is not { } p) return;
        var name = (row.Name ?? "").Trim();
        if (name.Length == 0) { row.Name = p.Name; return; } // a preset always has a name
        if (name == p.Name) return;
        p.Name = name;
        row.Name = name;
        // The GNOME shortcut's label names the preset.
        if (Mode == HotkeyMode.Gnome && p.ShortcutAccel is { } accel) GnomeShortcut.Set(accel, p.Id, p.Name);
        Commit();
    }

    private void PresetClearKey_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetOf(sender) is not { } p) return;
        StopRecording(false);
        p.Hotkey.Clear();
        if (p.ShortcutAccel != null) { GnomeShortcut.Remove(p.Id); p.ShortcutAccel = null; }
        if (RowOf(sender) is { } row) row.Note = "";
        Commit();
    }

    private void PresetRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        bool same = _recording && _recRow == row;
        StopRecording(false);
        if (!same) StartRecording(row);
    }

    private async void PresetCopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { Command.Length: > 0 } row && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(row.Command);
    }

    private void LoadHotkey()
    {
        var mode = Mode;
        var desc = C<TextBlock>("HotkeyDesc");
        C<Border>("EnableRow").IsVisible = mode == HotkeyMode.Blocked;
        C<Border>("CommandRow").IsVisible = mode == HotkeyMode.Manual;
        C<Border>("HotkeyRow").IsVisible = mode != HotkeyMode.Manual;
        switch (mode)
        {
            case HotkeyMode.Global:
            case HotkeyMode.Blocked: // shown as it will work once allowed
                desc.Text = "Works everywhere, even in games. Left and right modifier keys count as different keys, and you can use modifiers on their own (e.g. Right Ctrl + Right Alt).";
                if (!(_recording && _recRow == null)) C<TextBlock>("HotkeyText").Text = HotkeyService.Describe(App.Settings.Hotkey);
                break;
            case HotkeyMode.Gnome:
                desc.Text = "On Wayland, apps can't watch the keyboard, so Ember adds this as a GNOME keyboard shortcut (Settings → Keyboard → Custom Shortcuts). " +
                            "It needs a regular key with your modifiers, e.g. Super + Shift + E.";
                if (!(_recording && _recRow == null)) C<TextBlock>("HotkeyText").Text = GnomeBinding is string b ? GnomeShortcut.Describe(b) : "Not set";
                break;
            default:
                C<TextBlock>("CommandDesc").Text = Desktop.IsKde
                    ? "On Wayland, apps can't watch the keyboard. In System Settings → Keyboard → Shortcuts, choose Add New → Command, paste this command and pick any keys."
                    : $"On Wayland, apps can't watch the keyboard. Add a custom shortcut in {Desktop.Name}'s keyboard settings that runs this command.";
                C<TextBlock>("CommandText").Text = Desktop.ToggleCommand;
                break;
        }
    }

    // ------------------------------------------------------------------ one-time permission (Wayland)

    private async void EnableHotkeys_Click(object? sender, RoutedEventArgs e)
    {
        var button = C<Button>("EnableButton");
        // Already allowed (e.g. after running the command by hand)? Then just start.
        if (App.Hotkey.TryStartEvdev()) { HotkeysTurnedOn(); return; }
        button.IsEnabled = false;
        string? error = await HotkeyService.EnableEvdevAsync();
        if (error == null)
        {
            // The rule is in place; the access rights reach the devices a moment later.
            for (int i = 0; i < 15 && !App.Hotkey.TryStartEvdev(); i++) await System.Threading.Tasks.Task.Delay(200);
            if (App.Hotkey.IsActive) { button.IsEnabled = true; HotkeysTurnedOn(); return; }
            error = "the permission was added, but the keyboard still can't be read. Log out and back in, then press Check again";
        }
        button.IsEnabled = true;
        var note = C<TextBlock>("EnableNote");
        note.IsVisible = true;
        if (error is "cancelled" or "not authorised")
        {
            note.Text = "Not allowed, so hotkeys stay off. Press the button again whenever you like.";
            return;
        }
        note.Text = $"Ember couldn't ask for permission ({error}). Run this in a terminal instead, then press Check again:";
        C<TextBlock>("EnableCommandText").Text = HotkeyService.EnableCommand;
        C<StackPanel>("EnableManual").IsVisible = true;
        button.Content = "Check again";
    }

    private void HotkeysTurnedOn()
    {
        C<TextBlock>("EnableNote").IsVisible = false;
        C<StackPanel>("EnableManual").IsVisible = false;
        Commit(); // refreshes this page, the sidebar hint and the tray menu
    }

    private async void CopyEnableCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(HotkeyService.EnableCommand);
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        _previewVm.Update(snap);
        C<TextBlock>("RamNote").IsVisible = snap.RamTemps.Count == 0 && App.Settings.OverlayShowRam;
        var s = App.Settings;
        foreach (var d in snap.Drives.Where(d => d.PrimaryTemp != null))
        {
            if (_drives.Any(r => r.Id == d.Id)) continue;
            var opt = s.Drives.FirstOrDefault(o => o.Id == d.Id);
            _drives.Add(new DriveRow
            {
                Id = d.Id, Name = d.Name, Show = opt?.Show ?? true,
                Label = opt?.Label is { Length: > 0 } l ? l : OverlayViewModel.ShortDriveName(d.Name),
            });
        }
    }

    private void Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.OverlayVisible = C<ToggleSwitch>("ShowToggle").IsChecked == true;
        s.OverlayLayout = C<RadioButton>("LayoutCard").IsChecked == true ? OverlayLayout.Card
                        : C<RadioButton>("LayoutRings").IsChecked == true ? OverlayLayout.Rings : OverlayLayout.Pill;
        if (sender is RadioButton { GroupName: "Anchor" })
        {
            s.OverlayAnchor = C<RadioButton>("AnchorTL").IsChecked == true ? OverlayAnchor.TopLeft
                : C<RadioButton>("AnchorBL").IsChecked == true ? OverlayAnchor.BottomLeft
                : C<RadioButton>("AnchorBR").IsChecked == true ? OverlayAnchor.BottomRight : OverlayAnchor.TopRight;
            SetMonitorFromCombo();
        }
        s.OverlayShowCpu = C<CheckBox>("ShowCpu").IsChecked == true;
        s.OverlayShowGpu = C<CheckBox>("ShowGpu").IsChecked == true;
        s.OverlayShowRam = C<CheckBox>("ShowRam").IsChecked == true;
        s.OverlayShowDrives = C<CheckBox>("ShowDrives").IsChecked == true;
        s.OverlayShowUsage = C<CheckBox>("ShowUsage").IsChecked == true;
        s.OverlayShowPower = C<CheckBox>("ShowPower").IsChecked == true;
        s.OverlayShowLabels = C<CheckBox>("ShowLabels").IsChecked == true;
        s.OverlayHideWhenMainOpen = C<ToggleSwitch>("HideWhenMain").IsChecked == true;
        Commit();
    }

    private void SetMonitorFromCombo()
    {
        var all = (TopLevel.GetTopLevel(this)?.Screens ?? App.Current.Overlay?.Screens)?.All.ToList();
        int i = C<ComboBox>("MonitorCombo").SelectedIndex;
        if (all != null && i >= 0 && i < all.Count) App.Settings.OverlayMonitor = OverlayWindow.ScreenId(all[i]);
    }

    private void Monitor_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SetMonitorFromCombo();
        if (App.Settings.OverlayAnchor == OverlayAnchor.Free) App.Settings.OverlayAnchor = OverlayAnchor.TopRight;
        Commit();
    }

    private void Slider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OverlayOpacity = Math.Round(C<Slider>("OpacitySlider").Value, 2);
        App.Settings.OverlayScale = Math.Round(C<Slider>("ScaleSlider").Value, 2);
        Commit();
    }

    private void DriveChanged(object? sender, RoutedEventArgs e) => SaveDrives();
    private void DriveLabelChanged(object? sender, TextChangedEventArgs e) { if (!_loading) SaveDrives(); }

    private void SaveDrives()
    {
        var s = App.Settings;
        foreach (var r in _drives)
        {
            var opt = s.Drives.FirstOrDefault(o => o.Id == r.Id);
            if (opt == null) s.Drives.Add(opt = new DriveOverlayOption { Id = r.Id });
            opt.Show = r.Show;
            opt.Label = r.Label?.Trim() ?? "";
        }
        Commit();
    }

    private void Commit()
    {
        _loading = true;
        try { App.NotifySettingsChanged(); } finally { _loading = false; }
        Load();
    }

    private void Lock_Click(object? sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.OverlayLocked = !s.OverlayLocked;
        if (!s.OverlayLocked) s.OverlayVisible = true;
        Commit();
    }

    private async void CopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(Desktop.ToggleCommand);
    }

    // ------------------------------------------------------------------ recording
    // Keys are read from Ember's own window while recording (no system-wide grab needed).
    // One recorder for the show / hide hotkey (_recRow == null) and the presets' (_recRow = that preset).

    private PresetRow? _recRow;

    private void Record_Click(object? sender, RoutedEventArgs e)
    {
        bool wasMain = _recording && _recRow == null;
        StopRecording(false);
        if (!wasMain) StartRecording(null);
    }

    private void ShowRecording(string text)
    {
        if (_recRow != null) _recRow.HotkeyText = text;
        else C<TextBlock>("HotkeyText").Text = text;
    }

    /// <summary>A message under the hotkey being set (a preset's row, or the show / hide row).</summary>
    private void Note(PresetRow? row, string text)
    {
        if (row != null) { row.Note = text; return; }
        var note = C<TextBlock>("HotkeyNote");
        note.Text = text;
        note.IsVisible = text.Length > 0;
    }

    private void StartRecording(PresetRow? row)
    {
        if (Mode == HotkeyMode.Blocked)
        {
            Note(row, "Turn on hotkeys first (the \"Allow hotkeys\" button above), then set the keys.");
            return;
        }
        _recTop = TopLevel.GetTopLevel(this);
        if (_recTop == null) return;
        // Reading the keyboard directly (Wayland): record from the same source the hotkeys use.
        if (App.Hotkey.UsesEvdev) App.Hotkey.Recorder = OnRecordVk;
        _recording = true;
        _recRow = row;
        _recDown.Clear(); _recCombo.Clear();
        Note(null, "");
        if (row != null) { row.IsRecording = true; row.Note = ""; }
        else
        {
            C<Button>("RecordButton").Content = "Cancel";
            C<Border>("HotkeyBox").BorderBrush = (Avalonia.Media.IBrush?)this.FindResource("AccentBrush");
        }
        ShowRecording("Press keys…");
        _recTop.AddHandler(KeyDownEvent, OnRecordKeyDown, RoutingStrategies.Tunnel);
        _recTop.AddHandler(KeyUpEvent, OnRecordKeyUp, RoutingStrategies.Tunnel);
        if (_recTop is Window w) w.Deactivated += OnRecordDeactivated;
    }

    private void OnRecordDeactivated(object? sender, EventArgs e) => StopRecording(false);

    /// <summary>Keys from the keyboard reader (evdev) while recording, on the UI thread.</summary>
    private void OnRecordVk(int vk, bool down)
    {
        if (!_recording) return;
        if (down)
        {
            if (vk == 0x1B && _recCombo.Count == 0) { StopRecording(false); return; } // Esc
            _recDown.Add(vk);
            _recCombo.Add(vk);
            ShowRecording(HotkeyService.Describe(_recCombo) + " …");
        }
        else
        {
            _recDown.Remove(vk);
            if (_recDown.Count == 0 && _recCombo.Count > 0) StopRecording(true);
        }
    }

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true; // keys go to the recorder, not to buttons or text boxes
        if (App.Hotkey.Recorder != null) return; // the keyboard reader delivers them (OnRecordVk)
        if (e.Key == Key.Escape && _recCombo.Count == 0) { StopRecording(false); return; }
        if (Mode == HotkeyMode.Gnome)
        {
            var key = HotkeyService.KeyToGtkName(e.Key);
            if (key == null) { ShowRecording(ModsText(e.KeyModifiers) + "…"); return; }
            var accel = (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "<Control>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "<Alt>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "<Shift>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "<Super>" : "") + key;
            bool plainLetter = e.KeyModifiers == KeyModifiers.None && !key.StartsWith("F");
            if (plainLetter) { ShowRecording("Add a modifier…"); return; }
            var row = _recRow;
            StopRecording(false);
            SaveGnomeShortcut(accel, row);
            return;
        }
        int vk = HotkeyService.KeyToVk(e.Key);
        if (vk == 0) return;
        _recDown.Add(vk);
        _recCombo.Add(vk);
        ShowRecording(HotkeyService.Describe(_recCombo) + " …");
    }

    /// <summary>GNOME on Wayland: registers the shortcut with GNOME (the show / hide one, or a preset's).</summary>
    private void SaveGnomeShortcut(string accel, PresetRow? row)
    {
        var s = App.Settings;
        var preset = row == null ? null : s.OverlayPresets.FirstOrDefault(p => p.Id == row.Id);
        if (row != null && preset == null) return;
        string? owner = preset != null && string.Equals(GnomeBinding, accel, StringComparison.OrdinalIgnoreCase) ? "Show / hide overlay"
            : s.OverlayPresets.FirstOrDefault(p => p.Id != preset?.Id && string.Equals(p.ShortcutAccel, accel, StringComparison.OrdinalIgnoreCase))?.Name;
        if (owner != null)
        {
            Note(row, $"{GnomeShortcut.Describe(accel)} is already used by \"{owner}\". Pick other keys.");
            LoadHotkey(); LoadPresets();
            return;
        }
        if (preset == null) { if (GnomeShortcut.Set(accel)) { _gnomeBinding = accel; _gnomeRead = true; } }
        else if (GnomeShortcut.Set(accel, preset.Id, preset.Name)) preset.ShortcutAccel = accel;
        else Note(row, "GNOME didn't accept that shortcut. Try other keys.");
        Commit();
    }

    private static string ModsText(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? "Ctrl + " : "") + (m.HasFlag(KeyModifiers.Alt) ? "Alt + " : "") +
        (m.HasFlag(KeyModifiers.Shift) ? "Shift + " : "") + (m.HasFlag(KeyModifiers.Meta) ? "Super + " : "");

    private void OnRecordKeyUp(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (Mode != HotkeyMode.Global || App.Hotkey.Recorder != null) return;
        _recDown.Remove(HotkeyService.KeyToVk(e.Key));
        if (_recDown.Count == 0 && _recCombo.Count > 0) StopRecording(true);
    }

    private void StopRecording(bool save)
    {
        if (!_recording) return;
        _recording = false;
        var row = _recRow;
        _recRow = null;
        App.Hotkey.Recorder = null;
        if (_recTop != null)
        {
            _recTop.RemoveHandler(KeyDownEvent, OnRecordKeyDown);
            _recTop.RemoveHandler(KeyUpEvent, OnRecordKeyUp);
            if (_recTop is Window w) w.Deactivated -= OnRecordDeactivated;
            _recTop = null;
        }
        C<Button>("RecordButton").Content = "Change";
        C<Border>("HotkeyBox").BorderBrush = (Avalonia.Media.IBrush?)this.FindResource("InputBorderBrush");
        if (row != null) row.IsRecording = false;
        if (save && _recCombo.Count > 0)
        {
            var s = App.Settings;
            var preset = row == null ? null : s.OverlayPresets.FirstOrDefault(p => p.Id == row.Id);
            // The same keys can't do two things: say who has them instead of saving.
            if (s.HotkeyOwner(_recCombo, preset?.Id ?? "") is string owner)
                Note(row, $"{HotkeyService.Describe(_recCombo)} is already used by \"{owner}\". Pick other keys.");
            else
            {
                if (preset != null) preset.Hotkey = _recCombo.ToList();
                else if (row == null) s.Hotkey = _recCombo.ToList();
                Commit();
            }
        }
        LoadHotkey();
        LoadPresets();
    }

    private void ResetHotkey_Click(object? sender, RoutedEventArgs e)
    {
        StopRecording(false);
        var s = App.Settings;
        if (Mode == HotkeyMode.Gnome)
        {
            SaveGnomeShortcut(GnomeShortcut.DefaultBinding, null);
            return;
        }
        var def = new List<int> { 0xA3, 0xA5 };
        if (s.HotkeyOwner(def, "") is string owner)
        {
            Note(null, $"{HotkeyService.Describe(def)} is already used by \"{owner}\". Change that preset's hotkey first.");
            return;
        }
        Note(null, "");
        s.Hotkey = def;
        Commit();
    }
}
