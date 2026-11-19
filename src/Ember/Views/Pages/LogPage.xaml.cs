using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

/// <summary>The Log tab: temperatures recorded over the last day / week / whole log.</summary>
public partial class LogPage : UserControl
{
    private readonly DispatcherTimer _refresh;
    private int _version;

    public LogPage()
    {
        InitializeComponent();
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Loaded += (_, _) => { Refresh(); _refresh.Start(); App.SettingsChanged += OnSettings; App.LogChanged += Refresh; };
        Unloaded += (_, _) => { _refresh.Stop(); App.SettingsChanged -= OnSettings; App.LogChanged -= Refresh; };
    }

    private string _lastSettings = "";
    private void OnSettings()
    {
        // Only redraw for changes that affect this page (unit, thresholds, logging options).
        var s = App.Settings;
        var key = $"{s.Unit}|{s.LogEnabled}|{s.LogRetentionDays}|{s.LogViewDays}|{s.CpuThresholds.Warn}|{s.CpuThresholds.Critical}|{s.GpuThresholds.Warn}|{s.GpuThresholds.Critical}|{s.RamThresholds.Warn}|{s.SsdThresholds.Warn}|{s.HddThresholds.Warn}";
        if (key != _lastSettings) Refresh();
    }

    public async void Refresh()
    {
        var s = App.Settings;
        _lastSettings = $"{s.Unit}|{s.LogEnabled}|{s.LogRetentionDays}|{s.LogViewDays}|{s.CpuThresholds.Warn}|{s.CpuThresholds.Critical}|{s.GpuThresholds.Warn}|{s.GpuThresholds.Critical}|{s.RamThresholds.Warn}|{s.SsdThresholds.Warn}|{s.HddThresholds.Warn}";
        int version = ++_version;

        // Chart length: standard steps up to what the log keeps (1 day … 1 year).
        int days = LogViewModel.ViewDays(s);
        _syncingRange = true;
        RangeCombo.ItemsSource = LogViewModel.ViewOptions(s.LogRetentionDays)
            .Select(d => new ComboBoxItem { Content = d == 1 ? "24 hours" : LogViewModel.DaysText(d), Tag = d }).ToList();
        RangeCombo.SelectedItem = RangeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == days);
        _syncingRange = false;

        var log = App.Log;
        if (!s.LogEnabled || log == null)
        {
            bool hasFile = System.IO.File.Exists(TemperatureLog.DefaultPath);
            StatusText.Text = "Records CPU, GPU, memory and drive temperatures so you can look back over the last days.";
            OffText.Text = "Turn it on to record one entry a minute, kept for " + LogViewModel.DaysText(s.LogRetentionDays) +
                           $". The log is a single file of fixed size ({LogViewModel.SizeText(TemperatureLog.FileBytesForDays(s.LogRetentionDays))}) that's updated every 10 minutes, so it never grows and barely touches your drive." +
                           (hasFile ? " Your earlier log is still on disk and shows again when logging is back on." : "");
            OffCard.Visibility = Visibility.Visible;
            EmptyCard.Visibility = Visibility.Collapsed;
            RangeHost.Visibility = Visibility.Collapsed;
            Cards.ItemsSource = null;
            DailyCard.Visibility = Visibility.Collapsed;
            return;
        }

        OffCard.Visibility = Visibility.Collapsed;
        RangeHost.Visibility = Visibility.Visible;
        LogView view;
        try { view = await Task.Run(() => LogViewModel.Build(log, days, s, DateTime.UtcNow)); }
        catch (Exception ex) { App.LogError("Log page: " + ex); StatusText.Text = "Couldn't read the log: " + ex.Message; return; }
        if (version != _version) return; // a newer refresh started meanwhile

        StatusText.Text = $"{view.Status} · keeps {LogViewModel.DaysText(s.LogRetentionDays)} · {LogViewModel.SizeText(log.FileBytes)} on disk";
        EmptyCard.Visibility = view.HasData ? Visibility.Collapsed : Visibility.Visible;
        Cards.ItemsSource = view.Cards;
        BuildDaily(view);
    }

    private void BuildDaily(LogView view)
    {
        DailyCard.Visibility = view.Days.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var g = DailyGrid;
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        foreach (var _ in view.DayColumns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var muted = (Brush)FindResource("MutedBrush");
        var warm = (Brush)FindResource("WarmBrush");
        var divider = (Brush)FindResource("DividerBrush");
        void Cell(int row, int col, string text, Brush? fg = null, bool bold = false, bool header = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = header ? 11.5 : 13, Margin = new Thickness(0, 7, 8, 7),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            if (fg != null) tb.Foreground = fg;
            if (header) tb.Style = (Style)FindResource("Overline");
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, "DAY", header: true);
        for (int c = 0; c < view.DayColumns.Length; c++) Cell(0, c + 1, view.DayColumns[c].ToUpperInvariant(), header: true);
        for (int r = 0; r < view.Days.Count; r++)
        {
            var d = view.Days[r];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(line, r + 1); Grid.SetColumnSpan(line, view.DayColumns.Length + 1);
            g.Children.Add(line);
            Cell(r + 1, 0, d.Day, bold: true);
            for (int c = 0; c < d.Cells.Length; c++)
                Cell(r + 1, c + 1, d.Cells[c], d.Cells[c] == "—" ? muted : d.Hot[c] ? warm : null);
        }
    }

    /// <summary>Shows the last <paramref name="days"/> days (remembered).</summary>
    public void SetRange(int days)
    {
        App.Settings.LogViewDays = days;
        App.Settings.Save();
        Refresh();
    }

    private bool _syncingRange;
    private void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRange || RangeCombo.SelectedItem is not ComboBoxItem { Tag: int days } || days == LogViewModel.ViewDays(App.Settings)) return;
        SetRange(days);
    }

    private void TurnOn_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.LogEnabled = true;
        App.NotifySettingsChanged();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.Navigate("NavSettings");
}
