using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ember.Views;

/// <summary>
/// Settings-style rows (a <c>SettingRow</c> border holding a title + description on the left and a
/// control aligned right) are written as one-cell grids. In one cell the description runs underneath
/// the control once the window is narrow. This splits each such row into a text column and a control
/// column, so the description wraps beside the control instead.
/// </summary>
public static class SettingRows
{
    private const double Gap = 20;

    public static void Split(FrameworkElement page)
    {
        if (page.TryFindResource("SettingRow") is not Style rowStyle) return;
        Visit(page, rowStyle);
    }

    private static void Visit(DependencyObject node, Style rowStyle)
    {
        if (node is Border { Child: Grid g } b && b.Style == rowStyle) SplitRow(g);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList())
            Visit(child, rowStyle);
    }

    private static void SplitRow(Grid g)
    {
        // Only the plain shape: text panel first, then one control on the right, no columns yet.
        if (g.ColumnDefinitions.Count > 0 || g.Children.Count != 2) return;
        if (g.Children[0] is not StackPanel text || g.Children[1] is not FrameworkElement control) return;
        if (control.HorizontalAlignment != HorizontalAlignment.Right) return;

        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(control, 1);
        // The column now keeps the text clear of the control; a fixed right margin reserved for it is no longer needed.
        text.Margin = new Thickness(text.Margin.Left, text.Margin.Top, Gap, text.Margin.Bottom);
    }
}
