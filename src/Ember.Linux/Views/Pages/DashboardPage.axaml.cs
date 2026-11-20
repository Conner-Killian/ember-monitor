using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

public partial class DashboardPage : UserControl
{
    private readonly DashboardViewModel _vm = new();
    private readonly TextBlock _noDrives;

    public DashboardPage()
    {
        AvaloniaXamlLoader.Load(this);
        _vm.Cpu.Sub = "PROCESSOR";
        _vm.Gpu.Sub = "GRAPHICS";
        DataContext = _vm;
        _noDrives = this.FindControl<TextBlock>("NoDrivesText")!;
        _heroGrid = this.FindControl<Grid>("HeroGrid")!;
        _gpuCard = this.FindControl<ContentControl>("GpuCard")!;
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private readonly Grid _heroGrid;
    private readonly ContentControl _gpuCard;
    private bool? _stacked;

    /// <summary>
    /// The processor and graphics cards each need about 430 px for the gauge plus two columns of
    /// readings. Narrower (small window, or a larger interface scale) they go one above the other
    /// instead of cutting the readings off.
    /// </summary>
    private void ApplyWidth(double pageWidth)
    {
        bool stacked = pageWidth - 56 < 880; // 56 = the page's side margins
        if (stacked == _stacked) return;
        _stacked = stacked;
        Grid.SetColumn(_gpuCard, stacked ? 0 : 2);
        Grid.SetRow(_gpuCard, stacked ? 1 : 0);
        Grid.SetColumnSpan(_gpuCard, stacked ? 3 : 1);
        _gpuCard.Margin = stacked ? new Avalonia.Thickness(0, 16, 0, 0) : default;
        var cpuCard = _heroGrid.Children[0];
        Grid.SetColumnSpan(cpuCard, stacked ? 3 : 1);
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        _vm.Update(snap);
        _noDrives.IsVisible = _vm.Drives.Count == 0;
    }

    private void ResetMinMax_Click(object? sender, RoutedEventArgs e) => App.Sensors.ResetMinMax();
}
