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
    }

    public void UpdateSnapshot(SensorSnapshot snap)
    {
        _vm.Update(snap);
        _noDrives.IsVisible = _vm.Drives.Count == 0;
    }

    private void ResetMinMax_Click(object? sender, RoutedEventArgs e) => App.Sensors.ResetMinMax();
}
