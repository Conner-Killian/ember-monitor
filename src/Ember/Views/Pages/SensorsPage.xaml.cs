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
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private bool? _narrow;

    /// <summary>Narrow page: search under the title and no type column, so sensor names keep their room.</summary>
    private void ApplyWidth(double pageWidth)
    {
        bool narrow = pageWidth - 56 < 720; // 56 = the page's side margins
        if (narrow == _narrow) return;
        _narrow = narrow;
        Grid.SetRow(Tools, narrow ? 1 : 0);
        Grid.SetColumn(Tools, narrow ? 0 : 1);
        Grid.SetColumnSpan(Tools, narrow ? 2 : 1);
        Tools.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Tools.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(18, 0, 0, 0);
        Resources["TypeColumnWidth"] = new GridLength(narrow ? 0 : 110);
    }

    public void UpdateSnapshot(SensorSnapshot snap) => _vm.Update(snap);

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SensorGroupVM g }) _vm.Toggle(g);
    }
}
