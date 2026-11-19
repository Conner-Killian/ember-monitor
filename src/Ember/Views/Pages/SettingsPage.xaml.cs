using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ember.Models;
using Ember.Services;

namespace Ember.Views.Pages;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    private sealed record GpuChoice(string? Id, string Name) { public override string ToString() => Name; }

    public SettingsPage()
    {
        InitializeComponent();
        Swatches.ItemsSource = ThemeService.AccentPresets;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Ember {v?.Major}.{v?.Minor}.{v?.Build}";
        Load();
        App.SettingsChanged += () => { if (!_loading && IsLoaded) Load(); };
        // Refresh when shown: the GPU list and driver status may not have been known when the page
        // was first built (sensors still starting).
        Loaded += (_, _) => Load();
        // Enter commits a threshold, like leaving the box does.
        foreach (var tb in new[] { CpuWarn, CpuCrit, GpuWarn, GpuCrit, RamWarn, RamCrit, SsdWarn, SsdCrit, HddWarn, HddCrit })
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) Threshold_LostFocus(s, e); };
    }

    private void Load()
    {
        _loading = true;
        var s = App.Settings;
        UnitC.IsChecked = s.Unit == TempUnit.Celsius;
        UnitF.IsChecked = s.Unit == TempUnit.Fahrenheit;
        SelectByTag(IntervalCombo, s.PollIntervalMs.ToString());
        SelectByTag(SustainCombo, s.AlertSustainSeconds.ToString());

        var gpus = App.Sensors?.Latest?.Gpus ?? Array.Empty<DeviceSnapshot>();
        var choices = new[] { new GpuChoice(null, "Auto") }.Concat(gpus.Select(g => new GpuChoice(g.Id, g.Name))).ToList();
        GpuCombo.ItemsSource = choices;
        GpuCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == s.PrimaryGpuId) ?? choices[0];

        StartupToggle.IsChecked = s.StartWithWindows;
        TrayToggle.IsChecked = s.CloseToTray;
        TrayTempToggle.IsChecked = s.TrayShowsTemp;
        ColorToggle.IsChecked = s.AlertsColor;
        NotifyToggle.IsChecked = s.AlertsNotify;

        LoadThresholds();

        AccentHex.Text = s.AccentColor;
        BdMica.IsChecked = s.Backdrop == Backdrop.Mica;
        BdAcrylic.IsChecked = s.Backdrop == Backdrop.Acrylic;
        BdSolid.IsChecked = s.Backdrop == Backdrop.Solid;
        TintRow.IsEnabled = s.Backdrop != Backdrop.Solid;
        TintSlider.Value = s.TintOpacity;
        TintText.Text = $"{s.TintOpacity * 100:0}%";
        UiScaleSlider.Value = s.UiScale;
        UiScaleText.Text = $"{s.UiScale * 100:0}%";

        bool installed = App.Sensors?.DriverInstalled ?? false;
        DriverTitle.Text = installed ? "PawnIO driver installed" : "PawnIO driver not installed";
        DriverDesc.Text = installed
            ? "CPU, memory and motherboard sensors are available."
            : "Needed for CPU, memory (DIMM) and motherboard temperatures. GPU and drive temperatures work without it.";
        DriverButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        _loading = false;
    }

    private void LoadThresholds()
    {
        var s = App.Settings;
        string F(double c) => Math.Round(Temps.ToDisplay(c), MidpointRounding.AwayFromZero).ToString(CultureInfo.CurrentCulture);
        WarnHeader.Text = $"WARNING ({Temps.UnitSymbol})";
        CritHeader.Text = $"CRITICAL ({Temps.UnitSymbol})";
        CpuWarn.Text = F(s.CpuThresholds.Warn); CpuCrit.Text = F(s.CpuThresholds.Critical);
        GpuWarn.Text = F(s.GpuThresholds.Warn); GpuCrit.Text = F(s.GpuThresholds.Critical);
        RamWarn.Text = F(s.RamThresholds.Warn); RamCrit.Text = F(s.RamThresholds.Critical);
        SsdWarn.Text = F(s.SsdThresholds.Warn); SsdCrit.Text = F(s.SsdThresholds.Critical);
        HddWarn.Text = F(s.HddThresholds.Warn); HddCrit.Text = F(s.HddThresholds.Critical);
    }

    private static void SelectByTag(ComboBox cb, string tag)
    {
        foreach (ComboBoxItem it in cb.Items)
            if ((string)it.Tag == tag) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 1;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var s = App.Settings;
        s.Unit = UnitF.IsChecked == true ? TempUnit.Fahrenheit : TempUnit.Celsius;
        if (IntervalCombo.SelectedItem is ComboBoxItem ii) s.PollIntervalMs = int.Parse((string)ii.Tag);
        if (SustainCombo.SelectedItem is ComboBoxItem si) s.AlertSustainSeconds = int.Parse((string)si.Tag);
        if (GpuCombo.SelectedItem is GpuChoice g) s.PrimaryGpuId = g.Id;
        s.CloseToTray = TrayToggle.IsChecked == true;
        s.TrayShowsTemp = TrayTempToggle.IsChecked == true;
        s.AlertsColor = ColorToggle.IsChecked == true;
        s.AlertsNotify = NotifyToggle.IsChecked == true;
        s.Backdrop = BdAcrylic.IsChecked == true ? Backdrop.Acrylic : BdSolid.IsChecked == true ? Backdrop.Solid : Backdrop.Mica;
        Commit();
    }

    private void Commit()
    {
        _loading = true;
        try { App.NotifySettingsChanged(); } finally { _loading = false; }
        Load();
    }

    private async void Startup_Click(object sender, RoutedEventArgs e)
    {
        // schtasks takes a moment; run it off the UI thread so the window doesn't hitch.
        bool want = StartupToggle.IsChecked == true;
        StartupToggle.IsEnabled = false;
        try
        {
            if (!await System.Threading.Tasks.Task.Run(() => StartupService.Set(want)))
            {
                MessageBox.Show(Window.GetWindow(this)!, "Couldn't update the startup task.", "Ember", MessageBoxButton.OK, MessageBoxImage.Warning);
                StartupToggle.IsChecked = await System.Threading.Tasks.Task.Run(StartupService.IsEnabled);
            }
            App.Settings.StartWithWindows = StartupToggle.IsChecked == true;
            Commit();
        }
        finally { StartupToggle.IsEnabled = true; }
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        App.Settings.TintOpacity = Math.Round(TintSlider.Value, 2);
        App.Settings.UiScale = Math.Round(UiScaleSlider.Value, 2);
        TintText.Text = $"{App.Settings.TintOpacity * 100:0}%";
        UiScaleText.Text = $"{App.Settings.UiScale * 100:0}%";
        _loading = true;
        try { App.NotifySettingsChanged(); } finally { _loading = false; }
    }

    private void Threshold_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        void Read(TextBox warn, TextBox crit, Thresholds t)
        {
            if (double.TryParse(warn.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var w)) t.Warn = Math.Clamp(Temps.FromDisplay(w), 20, 130);
            if (double.TryParse(crit.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var c)) t.Critical = Math.Clamp(Temps.FromDisplay(c), 20, 130);
            if (t.Critical <= t.Warn) t.Critical = t.Warn + 1;
        }
        Read(CpuWarn, CpuCrit, s.CpuThresholds);
        Read(GpuWarn, GpuCrit, s.GpuThresholds);
        Read(RamWarn, RamCrit, s.RamThresholds);
        Read(SsdWarn, SsdCrit, s.SsdThresholds);
        Read(HddWarn, HddCrit, s.HddThresholds);
        Commit();
    }

    private void ResetThresholds_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        var s = App.Settings;
        s.CpuThresholds = d.CpuThresholds; s.GpuThresholds = d.GpuThresholds; s.RamThresholds = d.RamThresholds;
        s.SsdThresholds = d.SsdThresholds; s.HddThresholds = d.HddThresholds;
        Commit();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) { App.Settings.AccentColor = hex; Commit(); }
    }

    private void AccentHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AccentHex_LostFocus(sender, e);
    }

    private void AccentHex_LostFocus(object sender, RoutedEventArgs e)
    {
        var txt = AccentHex.Text.Trim();
        if (!txt.StartsWith("#")) txt = "#" + txt;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(txt);
            App.Settings.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Commit();
        }
        catch { AccentHex.Text = App.Settings.AccentColor; }
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        DriverButton.IsEnabled = false;
        await DriverService.InstallInteractive(Window.GetWindow(this));
        DriverButton.IsEnabled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
    }
}
