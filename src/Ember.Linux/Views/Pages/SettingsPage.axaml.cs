using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Ember.Controls;
using Ember.Models;
using Ember.Services;
using Ember.Services.Linux;

namespace Ember.Views.Pages;

public partial class SettingsPage : UserControl
{
    private bool _loading;
    private sealed record GpuChoice(string? Id, string Name) { public override string ToString() => Name; }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private static readonly string[] ThresholdBoxes = { "CpuWarn", "CpuCrit", "GpuWarn", "GpuCrit", "RamWarn", "RamCrit", "SsdWarn", "SsdCrit", "HddWarn", "HddCrit" };

    public SettingsPage()
    {
        AvaloniaXamlLoader.Load(this);
        C<ItemsControl>("Swatches").ItemsSource = ThemeService.AccentPresets;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        C<TextBlock>("VersionText").Text = $"Ember {v?.Major}.{v?.Minor}.{v?.Build} for Linux";
        C<TextBlock>("SessionText").Text = $"{Desktop.Name} · {(Desktop.IsWayland ? "Wayland" : "X11")} session" +
            (App.Sensors?.IsDemo == true ? " · demo readings" : "");
        foreach (var n in ThresholdBoxes)
        {
            var tb = C<TextBox>(n);
            tb.LostFocus += (_, _) => Threshold_Commit();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Threshold_Commit(); };
        }
        var hex = C<TextBox>("AccentHex");
        hex.LostFocus += (_, _) => AccentHex_Commit();
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) AccentHex_Commit(); };
        App.SettingsChanged += () => { if (!_loading && IsLoaded) Load(); };
        Loaded += (_, _) => { Load(); App.Updates.Changed += SyncUpdates; };
        Unloaded += (_, _) => App.Updates.Changed -= SyncUpdates;
        Load();
    }

    private void SyncUpdates()
    {
        var u = App.Updates;
        C<TextBlock>("UpdateTitle").Text = $"Ember {UpdateChecker.Format(UpdateChecker.Current)}";
        C<TextBlock>("UpdateDesc").Text = u.IsAvailable && !u.IsBusy
            ? $"Ember {u.Latest!.VersionText} is available. Download it from the release page, or update with your package manager."
            : u.Describe();
        C<Button>("CheckUpdatesButton").IsEnabled = !u.IsBusy;
        C<Button>("DownloadUpdateButton").IsVisible = u.IsAvailable;
    }

    private void CheckUpdates_Click(object? sender, RoutedEventArgs e) => _ = App.Updates.CheckAsync();
    private void DownloadUpdate_Click(object? sender, RoutedEventArgs e) => Desktop.OpenUrl(App.Updates.Latest?.PageUrl ?? UpdateChecker.ReleasesPage);

    private void Load()
    {
        _loading = true;
        var s = App.Settings;
        C<RadioButton>("UnitC").IsChecked = s.Unit == TempUnit.Celsius;
        C<RadioButton>("UnitF").IsChecked = s.Unit == TempUnit.Fahrenheit;
        SelectByTag(C<ComboBox>("IntervalCombo"), s.PollIntervalMs.ToString());
        SelectByTag(C<ComboBox>("SustainCombo"), s.AlertSustainSeconds.ToString());

        var gpus = App.Sensors?.Latest?.Gpus ?? Array.Empty<DeviceSnapshot>();
        var choices = new[] { new GpuChoice(null, "Auto") }.Concat(gpus.Select(g => new GpuChoice(g.Id, g.Name))).ToList();
        var gc = C<ComboBox>("GpuCombo");
        gc.ItemsSource = choices;
        gc.SelectedItem = choices.FirstOrDefault(c => c.Id == s.PrimaryGpuId) ?? choices[0];

        C<ToggleSwitch>("StartupToggle").IsChecked = s.StartWithWindows;
        C<ToggleSwitch>("TrayToggle").IsChecked = s.CloseToTray;
        C<ToggleSwitch>("TrayTempToggle").IsChecked = s.TrayShowsTemp;
        C<ToggleSwitch>("ColorToggle").IsChecked = s.AlertsColor;
        C<ToggleSwitch>("NotifyToggle").IsChecked = s.AlertsNotify;
        C<TextBlock>("TrayDesc").Text = Desktop.IsGnome
            ? "The overlay and alerts keep running. Open Ember from the app menu to get the window back. (GNOME shows tray icons only with the AppIndicator extension.)"
            : "The overlay and alerts keep running; use the tray icon or the app menu to get the window back.";

        string F(double c) => Math.Round(Temps.ToDisplay(c), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        C<TextBlock>("WarnHeader").Text = $"WARNING ({Temps.UnitSymbol})";
        C<TextBlock>("CritHeader").Text = $"CRITICAL ({Temps.UnitSymbol})";
        C<TextBox>("CpuWarn").Text = F(s.CpuThresholds.Warn); C<TextBox>("CpuCrit").Text = F(s.CpuThresholds.Critical);
        C<TextBox>("GpuWarn").Text = F(s.GpuThresholds.Warn); C<TextBox>("GpuCrit").Text = F(s.GpuThresholds.Critical);
        C<TextBox>("RamWarn").Text = F(s.RamThresholds.Warn); C<TextBox>("RamCrit").Text = F(s.RamThresholds.Critical);
        C<TextBox>("SsdWarn").Text = F(s.SsdThresholds.Warn); C<TextBox>("SsdCrit").Text = F(s.SsdThresholds.Critical);
        C<TextBox>("HddWarn").Text = F(s.HddThresholds.Warn); C<TextBox>("HddCrit").Text = F(s.HddThresholds.Critical);

        C<TextBox>("AccentHex").Text = s.AccentColor;
        C<Slider>("UiScaleSlider").Value = s.UiScale;
        C<TextBlock>("UiScaleText").Text = $"{s.UiScale * 100:0}%";
        C<ToggleSwitch>("AutoUpdateToggle").IsChecked = s.AutoCheckUpdates;
        SyncUpdates();
        BuildHints();
        _loading = false;
    }

    private static void SelectByTag(ComboBox cb, string tag)
    {
        foreach (var o in cb.Items)
            if (o is ComboBoxItem { Tag: string t } it && t == tag) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 1;
    }

    private void Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.Unit = C<RadioButton>("UnitF").IsChecked == true ? TempUnit.Fahrenheit : TempUnit.Celsius;
        if (C<ComboBox>("IntervalCombo").SelectedItem is ComboBoxItem { Tag: string it }) s.PollIntervalMs = int.Parse(it);
        if (C<ComboBox>("SustainCombo").SelectedItem is ComboBoxItem { Tag: string st }) s.AlertSustainSeconds = int.Parse(st);
        if (C<ComboBox>("GpuCombo").SelectedItem is GpuChoice g) s.PrimaryGpuId = g.Id;
        s.CloseToTray = C<ToggleSwitch>("TrayToggle").IsChecked == true;
        s.TrayShowsTemp = C<ToggleSwitch>("TrayTempToggle").IsChecked == true;
        s.AlertsColor = C<ToggleSwitch>("ColorToggle").IsChecked == true;
        s.AlertsNotify = C<ToggleSwitch>("NotifyToggle").IsChecked == true;
        s.AutoCheckUpdates = C<ToggleSwitch>("AutoUpdateToggle").IsChecked == true;
        Commit();
    }

    private void Commit()
    {
        _loading = true;
        try { App.NotifySettingsChanged(); } finally { _loading = false; }
        Load();
    }

    private void Startup_Click(object? sender, RoutedEventArgs e)
    {
        bool want = C<ToggleSwitch>("StartupToggle").IsChecked == true;
        Autostart.Set(want);
        App.Settings.StartWithWindows = Autostart.IsEnabled();
        Commit();
    }

    private void Slider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.UiScale = Math.Round(C<Slider>("UiScaleSlider").Value, 2);
        C<TextBlock>("UiScaleText").Text = $"{App.Settings.UiScale * 100:0}%";
        _loading = true;
        try { App.NotifySettingsChanged(); } finally { _loading = false; }
    }

    private void Threshold_Commit()
    {
        if (_loading) return;
        var s = App.Settings;
        void Read(string warn, string crit, Thresholds t)
        {
            if (double.TryParse(C<TextBox>(warn).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) t.Warn = Math.Clamp(Temps.FromDisplay(w), 20, 130);
            if (double.TryParse(C<TextBox>(crit).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) t.Critical = Math.Clamp(Temps.FromDisplay(c), 20, 130);
            if (t.Critical <= t.Warn) t.Critical = t.Warn + 1;
        }
        Read("CpuWarn", "CpuCrit", s.CpuThresholds);
        Read("GpuWarn", "GpuCrit", s.GpuThresholds);
        Read("RamWarn", "RamCrit", s.RamThresholds);
        Read("SsdWarn", "SsdCrit", s.SsdThresholds);
        Read("HddWarn", "HddCrit", s.HddThresholds);
        Commit();
    }

    private void ResetThresholds_Click(object? sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        var s = App.Settings;
        s.CpuThresholds = d.CpuThresholds; s.GpuThresholds = d.GpuThresholds; s.RamThresholds = d.RamThresholds;
        s.SsdThresholds = d.SsdThresholds; s.HddThresholds = d.HddThresholds;
        Commit();
    }

    private void Swatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) { App.Settings.AccentColor = hex; Commit(); }
    }

    private void AccentHex_Commit()
    {
        var box = C<TextBox>("AccentHex");
        var txt = (box.Text ?? "").Trim();
        if (!txt.StartsWith("#")) txt = "#" + txt;
        if (Color.TryParse(txt, out var c))
        {
            App.Settings.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Commit();
        }
        else box.Text = App.Settings.AccentColor;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.Folder);
        Desktop.OpenFolder(AppSettings.Folder);
    }

    // ------------------------------------------------------------------ sensor setup tips

    private void BuildHints()
    {
        var panel = C<StackPanel>("HintsPanel");
        panel.Children.Clear();
        var h = App.Sensors?.Hints ?? new SensorHints();
        var items = new List<(string title, string text, string? cmd)>();
        if (h.NoCpuTemperature)
            items.Add(("CPU temperature", "No CPU temperature driver is loaded. For AMD load k10temp, for Intel coretemp:", "sudo modprobe k10temp"));
        if (h.NvidiaWithoutNvml)
            items.Add(("NVIDIA graphics", "NVIDIA readings come from the proprietary NVIDIA driver (Debian/Ubuntu: nvidia-driver, Fedora: akmod-nvidia from RPM Fusion). The open-source nouveau driver reports little beyond temperature.", null));
        if (h.SataWithoutDrivetemp)
            items.Add(("SATA drive temperatures", "Linux reads SATA drive temperatures through the drivetemp module. Ember's .deb/.rpm/Arch packages load it at boot; to load it now (or if you use the AppImage):", "sudo modprobe drivetemp"));
        if (h.CpuPowerDenied)
            items.Add(("CPU power draw", "The kernel lets only root read the CPU energy counter (it can leak timing information to other programs). To allow it until the next reboot:", "sudo chmod o+r /sys/class/powercap/intel-rapl:0/energy_uj"));
        if (h.NoDimmSensors)
            items.Add(("RAM temperatures", "DDR5 modules have temperature sensors, read by the spd5118 driver (Linux 6.11 or newer). Most DDR4 modules have none.", "sudo modprobe spd5118"));
        if (h.NoBoardSensors)
            items.Add(("Motherboard fans and temperatures", "Your board's sensor chip needs a driver. sensors-detect (lm-sensors package) finds the right one. Some chips, like the NCT6687D on recent MSI boards, need an out-of-tree driver (nct6687d).", "sudo sensors-detect"));
        if (items.Count == 0)
            items.Add(("All set", "Every sensor driver Ember knows about is available on this system.", null));

        foreach (var (title, text, cmd) in items)
        {
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = title, Classes = { "rowTitle" } });
            info.Children.Add(new TextBlock { Text = text, Classes = { "rowDesc" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(info);
            if (cmd != null)
            {
                info.Margin = new Thickness(0, 0, 24, 0);
                var copy = new Button { Classes = { "ghost" }, Content = new Icon { Kind = "copy", Width = 15, Height = 15 } };
                ToolTip.SetTip(copy, "Copy");
                copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(cmd); };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new Border { Classes = { "code" }, Child = new TextBlock { Text = cmd, Classes = { "mono" } } });
                row.Children.Add(copy);
                Grid.SetColumn(row, 1);
                grid.Children.Add(row);
            }
            panel.Children.Add(new Border { Classes = { "row" }, Child = grid });
        }
    }
}
