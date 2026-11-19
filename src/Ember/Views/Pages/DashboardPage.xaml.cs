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
