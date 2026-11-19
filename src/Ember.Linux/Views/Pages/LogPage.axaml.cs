using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

/// <summary>The Log tab: temperatures recorded over the last day / week / whole log.</summary>
public partial class LogPage : UserControl
{
    private readonly DispatcherTimer _refresh;
    private int _version;
    private string _lastSettings = "";
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public LogPage()
    {
        AvaloniaXamlLoader.Load(this);
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Refresh());
        Loaded += (_, _) => { Refresh(); _refresh.Start(); App.SettingsChanged += OnSettings; App.LogChanged += Refresh; };
        Unloaded += (_, _) => { _refresh.Stop(); App.SettingsChanged -= OnSettings; App.LogChanged -= Refresh; };
    }

    private static string SettingsKey()
    {
        var s = App.Settings;
        return $"{s.Unit}|{s.LogEnabled}|{s.LogRetentionDays}|{s.LogViewDays}|{s.CpuThresholds.Warn}|{s.CpuThresholds.Critical}|{s.GpuThresholds.Warn}|{s.GpuThresholds.Critical}|{s.RamThresholds.Warn}|{s.SsdThresholds.Warn}|{s.HddThresholds.Warn}";
    }

    private void OnSettings() { if (SettingsKey() != _lastSettings) Refresh(); }

    /// <summary>Shows the last <paramref name="days"/> days (remembered).</summary>
    public void SetRange(int days)
    {
        App.Settings.LogViewDays = days;
        App.Settings.Save();
        Refresh();
    }

    public async void Refresh()
    {
        var s = App.Settings;
        _lastSettings = SettingsKey();
        int version = ++_version;

        // Chart length: standard steps up to what the log keeps (1 day … 1 year).
        int days = LogViewModel.ViewDays(s);
        var combo = C<ComboBox>("RangeCombo");
        _syncingRange = true;
        combo.ItemsSource = LogViewModel.ViewOptions(s.LogRetentionDays)
            .Select(d => new ComboBoxItem { Content = d == 1 ? "24 hours" : LogViewModel.DaysText(d), Tag = d }).ToList();
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int t && t == days);
        _syncingRange = false;

        var log = App.Log;
        if (!s.LogEnabled || log == null)
        {
            bool hasFile = System.IO.File.Exists(TemperatureLog.DefaultPath);
            C<TextBlock>("StatusText").Text = "Records CPU, GPU, memory and drive temperatures so you can look back over the last days.";
            C<TextBlock>("OffText").Text = "Turn it on to record one entry a minute, kept for " + LogViewModel.DaysText(s.LogRetentionDays) +
                $". The log is a single file of fixed size ({LogViewModel.SizeText(TemperatureLog.FileBytesForDays(s.LogRetentionDays))}) that's updated every 10 minutes, so it never grows and barely touches your drive." +
                (hasFile ? " Your earlier log is still on disk and shows again when logging is back on." : "");
            C<Border>("OffCard").IsVisible = true;
            C<Border>("EmptyCard").IsVisible = false;
            C<StackPanel>("RangeHost").IsVisible = false;
            C<ItemsControl>("Cards").ItemsSource = null;
            C<Border>("DailyCard").IsVisible = false;
            return;
        }

        C<Border>("OffCard").IsVisible = false;
        C<StackPanel>("RangeHost").IsVisible = true;
        LogView view;
        try { view = await Task.Run(() => LogViewModel.Build(log, days, s, DateTime.UtcNow)); }
        catch (Exception ex) { App.LogError("Log page: " + ex); C<TextBlock>("StatusText").Text = "Couldn't read the log: " + ex.Message; return; }
        if (version != _version) return;

        C<TextBlock>("StatusText").Text = $"{view.Status} · keeps {LogViewModel.DaysText(s.LogRetentionDays)} · {LogViewModel.SizeText(log.FileBytes)} on disk";
        C<Border>("EmptyCard").IsVisible = !view.HasData;
        C<ItemsControl>("Cards").ItemsSource = view.Cards;
        BuildDaily(view);
    }

    private IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.White;

    private void BuildDaily(LogView view)
    {
        C<Border>("DailyCard").IsVisible = view.Days.Count > 0;
        var g = C<Grid>("DailyGrid");
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition(1.4, GridUnitType.Star));
        foreach (var _ in view.DayColumns) g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var muted = Brush("MutedBrush"); var warm = Brush("WarmBrush"); var divider = Brush("DividerBrush");

        void Cell(int row, int col, string text, IBrush? fg = null, bool bold = false, bool header = false)
        {
            var tb = new TextBlock
            {
                Text = text, Margin = new Thickness(0, 7, 8, 7),
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            if (header) tb.Classes.Add("overline"); else tb.FontSize = 13;
            if (fg != null) tb.Foreground = fg;
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(0, 0, "DAY", header: true);
        for (int c = 0; c < view.DayColumns.Length; c++) Cell(0, c + 1, view.DayColumns[c].ToUpperInvariant(), header: true);
        for (int r = 0; r < view.Days.Count; r++)
        {
            var d = view.Days[r];
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var line = new Border { BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(line, r + 1); Grid.SetColumnSpan(line, view.DayColumns.Length + 1);
            g.Children.Add(line);
            Cell(r + 1, 0, d.Day, bold: true);
            for (int c = 0; c < d.Cells.Length; c++)
                Cell(r + 1, c + 1, d.Cells[c], d.Cells[c] == "—" ? muted : d.Hot[c] ? warm : null);
        }
    }

    private bool _syncingRange;
    private void Range_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingRange || C<ComboBox>("RangeCombo").SelectedItem is not ComboBoxItem { Tag: int days } || days == LogViewModel.ViewDays(App.Settings)) return;
        SetRange(days);
    }

    private void TurnOn_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.LogEnabled = true;
        App.NotifySettingsChanged();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.Navigate("NavSettings");
}
