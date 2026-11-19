using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ember.Models;
using Ember.Services;
using Ember.ViewModels;
using Screen = System.Windows.Forms.Screen;

namespace Ember.Views.Pages;

public partial class OverlayPage : UserControl
{
    private readonly OverlayViewModel _previewVm = new();
    private bool _loading;
    private bool _recording;
    private readonly HashSet<int> _recDown = new();
    private readonly HashSet<int> _recCombo = new();

    public sealed class DriveRow : Observable
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        private bool _show; private string _label = "";
        public bool Show { get => _show; set => Set(ref _show, value); }
        public string Label { get => _label; set => Set(ref _label, value); }
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<DriveRow> _drives = new();

    public OverlayPage()
    {
        InitializeComponent();
        Preview.DataContext = _previewVm;
        DriveList.ItemsSource = _drives;
        Load();
        App.SettingsChanged += OnSettingsChanged;
        Unloaded += (_, _) => StopRecording(false);
    }

    private void OnSettingsChanged()
    {
        if (!_loading) Load();
    }

    private void Load()
    {
        _loading = true;
        var s = App.Settings;
        ShowToggle.IsChecked = s.OverlayVisible;
        LayoutPill.IsChecked = s.OverlayLayout == OverlayLayout.Pill;
        LayoutCard.IsChecked = s.OverlayLayout == OverlayLayout.Card;
        LayoutRings.IsChecked = s.OverlayLayout == OverlayLayout.Rings;
        AnchorTL.IsChecked = s.OverlayAnchor == OverlayAnchor.TopLeft;
        AnchorTR.IsChecked = s.OverlayAnchor == OverlayAnchor.TopRight;
        AnchorBL.IsChecked = s.OverlayAnchor == OverlayAnchor.BottomLeft;
        AnchorBR.IsChecked = s.OverlayAnchor == OverlayAnchor.BottomRight;
        AnchorDesc.Text = s.OverlayAnchor == OverlayAnchor.Free
            ? "Currently placed where you dragged it. Pick a corner to pin it there instead."
            : "Pin the overlay to a corner of the selected display.";
        ShowCpu.IsChecked = s.OverlayShowCpu;
        ShowGpu.IsChecked = s.OverlayShowGpu;
        ShowRam.IsChecked = s.OverlayShowRam;
        ShowDrives.IsChecked = s.OverlayShowDrives;
        ShowUsage.IsChecked = s.OverlayShowUsage;
        ShowPower.IsChecked = s.OverlayShowPower;
        ShowLabels.IsChecked = s.OverlayShowLabels;
        HideWhenMain.IsChecked = s.OverlayHideWhenMainOpen;
        OpacitySlider.Value = s.OverlayOpacity;
        ScaleSlider.Value = s.OverlayScale;
        OpacityText.Text = $"{s.OverlayOpacity * 100:0}%";
        ScaleText.Text = $"{s.OverlayScale * 100:0}%";
        DrivesPanel.Visibility = s.OverlayShowDrives ? Visibility.Visible : Visibility.Collapsed;
        if (!_recording) HotkeyText.Text = HotkeyService.Describe(s.Hotkey);
        LockText.Text = s.OverlayLocked ? "Unlock to move" : "Lock in place";
        LockIcon.Text = s.OverlayLocked ? "" : "";

        var screens = Screen.AllScreens;
        MonitorCombo.ItemsSource = screens.Select((sc, i) =>
            $"Display {i + 1}{(sc.Primary ? " (main)" : "")}  ·  {sc.Bounds.Width}×{sc.Bounds.Height}").ToList();
        var current = OverlayWindow.FindScreen(s.OverlayMonitor, s.OverlayMonitorBounds);
        var idx = current == null ? -1 : Array.FindIndex(screens, sc => sc.DeviceName == current.DeviceName);
        if (idx < 0) idx = Array.FindIndex(screens, sc => sc.Primary);
        MonitorCombo.SelectedIndex = Math.Max(0, idx);
        MonitorCombo.Visibility = screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;

        Preview.ApplyAppearance();
        _previewVm.Update(App.Sensors?.Latest);
        _loading = false;
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        _previewVm.Update(snap);
        RamNote.Visibility = snap.RamTemps.Count == 0 && App.Settings.OverlayShowRam ? Visibility.Visible : Visibility.Collapsed;

        // Drive list (sync with detected drives)
        var s = App.Settings;
        foreach (var d in snap.Drives.Where(d => d.PrimaryTemp != null))
        {
            if (_drives.Any(r => r.Id == d.Id)) continue;
            var opt = s.Drives.FirstOrDefault(o => o.Id == d.Id);
            _drives.Add(new DriveRow
            {
                Id = d.Id, Name = d.Name,
                Show = opt?.Show ?? true,
                Label = opt?.Label is { Length: > 0 } l ? l : OverlayViewModel.ShortDriveName(d.Name),
            });
        }
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.OverlayVisible = ShowToggle.IsChecked == true;
        s.OverlayLayout = LayoutCard.IsChecked == true ? OverlayLayout.Card : LayoutRings.IsChecked == true ? OverlayLayout.Rings : OverlayLayout.Pill;
        if (sender is RadioButton rb && rb.GroupName == "Anchor")
        {
            s.OverlayAnchor = AnchorTL.IsChecked == true ? OverlayAnchor.TopLeft
                : AnchorBL.IsChecked == true ? OverlayAnchor.BottomLeft
                : AnchorBR.IsChecked == true ? OverlayAnchor.BottomRight
                : OverlayAnchor.TopRight;
            var screens = Screen.AllScreens;
            if (MonitorCombo.SelectedIndex >= 0 && MonitorCombo.SelectedIndex < screens.Length)
                SetMonitor(screens[MonitorCombo.SelectedIndex]);
        }
        s.OverlayShowCpu = ShowCpu.IsChecked == true;
        s.OverlayShowGpu = ShowGpu.IsChecked == true;
        s.OverlayShowRam = ShowRam.IsChecked == true;
        s.OverlayShowDrives = ShowDrives.IsChecked == true;
        s.OverlayShowUsage = ShowUsage.IsChecked == true;
        s.OverlayShowPower = ShowPower.IsChecked == true;
        s.OverlayShowLabels = ShowLabels.IsChecked == true;
        s.OverlayHideWhenMainOpen = HideWhenMain.IsChecked == true;
        Commit();
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var screens = Screen.AllScreens;
        if (MonitorCombo.SelectedIndex < 0 || MonitorCombo.SelectedIndex >= screens.Length) return;
        var s = App.Settings;
        SetMonitor(screens[MonitorCombo.SelectedIndex]);
        if (s.OverlayAnchor == OverlayAnchor.Free) s.OverlayAnchor = OverlayAnchor.TopRight;
        Commit();
    }

    private static void SetMonitor(Screen sc)
    {
        App.Settings.OverlayMonitor = sc.DeviceName;
        App.Settings.OverlayMonitorBounds = OverlayWindow.BoundsId(sc);
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        App.Settings.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
        App.Settings.OverlayScale = Math.Round(ScaleSlider.Value, 2);
        OpacityText.Text = $"{App.Settings.OverlayOpacity * 100:0}%";
        ScaleText.Text = $"{App.Settings.OverlayScale * 100:0}%";
        Commit();
    }

    private void DriveChanged(object sender, RoutedEventArgs e) => SaveDrives();
    private void DriveLabelChanged(object sender, TextChangedEventArgs e) { if (!_loading) SaveDrives(); }

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
        try { App.NotifySettingsChanged(); }
        finally { _loading = false; }
        Load();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.OverlayLocked = !s.OverlayLocked;
        if (!s.OverlayLocked) s.OverlayVisible = true;
        Commit();
    }

    // ------------------------------------------------------------------ hotkey recording

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StopRecording(false); return; }
        _recording = true;
        _recDown.Clear(); _recCombo.Clear();
        HotkeyText.Text = "Press keys…";
        RecordButton.Content = "Cancel";
        HotkeyBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        App.Hotkey.Recorder = OnRecordKey;
        // Recording swallows every key on the PC, so it must end the moment Ember isn't in front
        // (otherwise the next key typed in another app would silently become the hotkey).
        if (Window.GetWindow(this) is Window w)
        {
            _recWindow = w;
            w.Deactivated += RecordWindow_Deactivated;
            w.IsVisibleChanged += RecordWindow_Deactivated;
        }
    }

    private Window? _recWindow;
    private void RecordWindow_Deactivated(object? sender, EventArgs e) => StopRecording(false);
    private void RecordWindow_Deactivated(object sender, DependencyPropertyChangedEventArgs e) => StopRecording(false);

    // Called on the UI thread by HotkeyService.
    private void OnRecordKey(int vk, bool down)
    {
        if (!_recording) return;
        if (down)
        {
            if (vk == 0x1B && _recCombo.Count == 0) { StopRecording(false); return; } // Esc
            _recDown.Add(vk);
            _recCombo.Add(vk);
            HotkeyText.Text = HotkeyService.Describe(_recCombo) + " …";
        }
        else
        {
            _recDown.Remove(vk);
            if (_recDown.Count == 0 && _recCombo.Count > 0) StopRecording(true);
        }
    }

    private void StopRecording(bool save)
    {
        if (!_recording) return;
        _recording = false;
        App.Hotkey.Recorder = null;
        if (_recWindow != null)
        {
            _recWindow.Deactivated -= RecordWindow_Deactivated;
            _recWindow.IsVisibleChanged -= RecordWindow_Deactivated;
            _recWindow = null;
        }
        RecordButton.Content = "Change";
        HotkeyBox.BorderBrush = (System.Windows.Media.Brush)FindResource("InputBorderBrush");
        if (save && _recCombo.Count > 0)
        {
            App.Settings.Hotkey = _recCombo.ToList();
            Commit();
        }
        HotkeyText.Text = HotkeyService.Describe(App.Settings.Hotkey);
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        StopRecording(false);
        App.Settings.Hotkey = new List<int> { 0xA3, 0xA5 };
        Commit();
    }
}
