using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private enum HotkeyMode { X11, Gnome, Manual }
    private static HotkeyMode Mode =>
        App.Hotkey?.IsActive == true ? HotkeyMode.X11 : GnomeShortcut.Available ? HotkeyMode.Gnome : HotkeyMode.Manual;

    private static string? _gnomeBinding;
    private static bool _gnomeRead;
    private static string? GnomeBinding
    {
        get { if (!_gnomeRead) { _gnomeBinding = GnomeShortcut.Current(); _gnomeRead = true; } return _gnomeBinding; }
    }

    /// <summary>The overlay shortcut as currently set, or null when there isn't one (tray menu).</summary>
    public static string? ShortcutText() => Mode switch
    {
        HotkeyMode.X11 when App.Settings.Hotkey.Count > 0 => HotkeyService.Describe(App.Settings.Hotkey),
        HotkeyMode.Gnome when GnomeBinding is string b => GnomeShortcut.Describe(b),
        _ => null,
    };

    /// <summary>One-line hint for the sidebar.</summary>
    public static string HotkeySummary() => Mode switch
    {
        HotkeyMode.X11 => "Toggle with " + HotkeyService.Describe(App.Settings.Hotkey),
        HotkeyMode.Gnome => GnomeBinding is string b ? "Toggle with " + GnomeShortcut.Describe(b) : "Set a shortcut on the Overlay page",
        _ => "Bind a keyboard shortcut on the Overlay page",
    };

    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public OverlayPage()
    {
        AvaloniaXamlLoader.Load(this);
        C<OverlayContent>("Preview").DataContext = _previewVm;
        C<ItemsControl>("DriveList").ItemsSource = _drives;
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

        C<OverlayContent>("Preview").ApplyAppearance();
        _previewVm.Update(App.Sensors?.Latest);
        _loading = false;
    }

    private void LoadHotkey()
    {
        var mode = Mode;
        var desc = C<TextBlock>("HotkeyDesc");
        C<Border>("CommandRow").IsVisible = mode == HotkeyMode.Manual;
        C<Border>("HotkeyRow").IsVisible = mode != HotkeyMode.Manual;
        switch (mode)
        {
            case HotkeyMode.X11:
                desc.Text = "Works everywhere, even in games. Left and right modifier keys count as different keys, and you can use modifiers on their own (e.g. Right Ctrl + Right Alt).";
                if (!_recording) C<TextBlock>("HotkeyText").Text = HotkeyService.Describe(App.Settings.Hotkey);
                break;
            case HotkeyMode.Gnome:
                desc.Text = "On Wayland, apps can't watch the keyboard, so Ember adds this as a GNOME keyboard shortcut (Settings → Keyboard → Custom Shortcuts). " +
                            "It needs a regular key with your modifiers, e.g. Super + Shift + E.";
                if (!_recording) C<TextBlock>("HotkeyText").Text = GnomeBinding is string b ? GnomeShortcut.Describe(b) : "Not set";
                break;
            default:
                C<TextBlock>("CommandDesc").Text = Desktop.IsKde
                    ? "On Wayland, apps can't watch the keyboard. In System Settings → Keyboard → Shortcuts, choose Add New → Command, paste this command and pick any keys."
                    : $"On Wayland, apps can't watch the keyboard. Add a custom shortcut in {Desktop.Name}'s keyboard settings that runs this command.";
                C<TextBlock>("CommandText").Text = Desktop.ToggleCommand;
                break;
        }
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

    private void Record_Click(object? sender, RoutedEventArgs e)
    {
        if (_recording) { StopRecording(false); return; }
        _recTop = TopLevel.GetTopLevel(this);
        if (_recTop == null) return;
        _recording = true;
        _recDown.Clear(); _recCombo.Clear();
        C<TextBlock>("HotkeyText").Text = "Press keys…";
        C<Button>("RecordButton").Content = "Cancel";
        C<Border>("HotkeyBox").BorderBrush = (Avalonia.Media.IBrush?)this.FindResource("AccentBrush");
        _recTop.AddHandler(KeyDownEvent, OnRecordKeyDown, RoutingStrategies.Tunnel);
        _recTop.AddHandler(KeyUpEvent, OnRecordKeyUp, RoutingStrategies.Tunnel);
        if (_recTop is Window w) w.Deactivated += OnRecordDeactivated;
    }

    private void OnRecordDeactivated(object? sender, EventArgs e) => StopRecording(false);

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape && _recCombo.Count == 0) { StopRecording(false); return; }
        if (Mode == HotkeyMode.Gnome)
        {
            var key = HotkeyService.KeyToGtkName(e.Key);
            if (key == null) { C<TextBlock>("HotkeyText").Text = ModsText(e.KeyModifiers) + "…"; return; }
            var accel = (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "<Control>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "<Alt>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "<Shift>" : "") +
                        (e.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "<Super>" : "") + key;
            bool plainLetter = e.KeyModifiers == KeyModifiers.None && !key.StartsWith("F");
            if (plainLetter) { C<TextBlock>("HotkeyText").Text = "Add a modifier…"; return; }
            StopRecording(false);
            if (GnomeShortcut.Set(accel)) { _gnomeBinding = accel; _gnomeRead = true; }
            Commit();
            return;
        }
        int vk = HotkeyService.KeyToVk(e.Key);
        if (vk == 0) return;
        _recDown.Add(vk);
        _recCombo.Add(vk);
        C<TextBlock>("HotkeyText").Text = HotkeyService.Describe(_recCombo) + " …";
    }

    private static string ModsText(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? "Ctrl + " : "") + (m.HasFlag(KeyModifiers.Alt) ? "Alt + " : "") +
        (m.HasFlag(KeyModifiers.Shift) ? "Shift + " : "") + (m.HasFlag(KeyModifiers.Meta) ? "Super + " : "");

    private void OnRecordKeyUp(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (Mode != HotkeyMode.X11) return;
        _recDown.Remove(HotkeyService.KeyToVk(e.Key));
        if (_recDown.Count == 0 && _recCombo.Count > 0) StopRecording(true);
    }

    private void StopRecording(bool save)
    {
        if (!_recording) return;
        _recording = false;
        if (_recTop != null)
        {
            _recTop.RemoveHandler(KeyDownEvent, OnRecordKeyDown);
            _recTop.RemoveHandler(KeyUpEvent, OnRecordKeyUp);
            if (_recTop is Window w) w.Deactivated -= OnRecordDeactivated;
            _recTop = null;
        }
        C<Button>("RecordButton").Content = "Change";
        C<Border>("HotkeyBox").BorderBrush = (Avalonia.Media.IBrush?)this.FindResource("InputBorderBrush");
        if (save && _recCombo.Count > 0)
        {
            App.Settings.Hotkey = _recCombo.ToList();
            Commit();
        }
        LoadHotkey();
    }

    private void ResetHotkey_Click(object? sender, RoutedEventArgs e)
    {
        StopRecording(false);
        if (Mode == HotkeyMode.Gnome)
        {
            if (GnomeShortcut.Set(GnomeShortcut.DefaultBinding)) { _gnomeBinding = GnomeShortcut.DefaultBinding; _gnomeRead = true; }
        }
        else App.Settings.Hotkey = new List<int> { 0xA3, 0xA5 };
        Commit();
    }
}
