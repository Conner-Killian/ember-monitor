using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

public partial class SensorsPage : UserControl
{
    private readonly SensorsViewModel _vm = new();

    public SensorsPage()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;
        // Narrow page: search under the title and no type column, so sensor names keep their room.
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width - 56 < 720);
    }

    public void UpdateSnapshot(SensorSnapshot snap) => _vm.Update(snap);

    private void Header_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SensorGroupVM g }) _vm.Toggle(g);
    }
}
