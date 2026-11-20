using System.Windows;
using System.Windows.Controls;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

public partial class DashboardPage : UserControl
{
    private readonly DashboardViewModel _vm = new();

    public DashboardPage()
    {
        InitializeComponent();
        _vm.Cpu.Sub = "PROCESSOR";
        _vm.Gpu.Sub = "GRAPHICS";
        DataContext = _vm;
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private bool? _stacked;

    /// <summary>
    /// The processor and graphics cards each need about 430 px for the gauge plus two columns of
    /// readings. Narrower (small window, or a larger interface size) they go one above the other
    /// instead of squeezing the readings.
    /// </summary>
    private void ApplyWidth(double pageWidth)
    {
        bool stacked = pageWidth - 56 < 880; // 56 = the page's side padding
        if (stacked == _stacked) return;
        _stacked = stacked;
        HeroGrid.Columns = stacked ? 1 : 2;
        GpuCard.Margin = stacked ? new Thickness(8, 16, 8, 0) : new Thickness(8, 0, 8, 0);
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        _vm.Update(snap);
        NoRamText.Visibility = _vm.HasRamTemps ? Visibility.Collapsed : Visibility.Visible;
        NoFansText.Visibility = _vm.HasFans ? Visibility.Collapsed : Visibility.Visible;
        NoBoardText.Visibility = _vm.HasBoardTemps ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetMinMax_Click(object sender, RoutedEventArgs e) => App.Sensors.ResetMinMax();

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) b.IsEnabled = false;
        await DriverService.InstallInteractive(Window.GetWindow(this));
        if (sender is Button b2) b2.IsEnabled = true;
    }
}
