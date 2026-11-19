using System.Windows;
using System.Windows.Controls;
using Ember.Services;
using Ember.ViewModels;

namespace Ember.Views.Pages;

public partial class SensorsPage : UserControl
{
    private readonly SensorsViewModel _vm = new();

    public SensorsPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Search.TextChanged += (_, _) => SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateSnapshot(SensorSnapshot snap) => _vm.Update(snap);

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SensorGroupVM g }) _vm.Toggle(g);
    }
}
