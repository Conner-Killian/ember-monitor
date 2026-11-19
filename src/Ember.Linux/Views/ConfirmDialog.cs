using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ember.Views;

/// <summary>A small OK / Cancel question in Ember's style (Avalonia has no message box).</summary>
public static class ConfirmDialog
{
    public static async Task<bool> Ask(Window? owner, string title, string text, string ok)
    {
        if (owner == null) return false;
        var dlg = new Window
        {
            Title = title,
            Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("WindowBrush"));
        var okButton = new Button { Content = ok, IsDefault = true };
        okButton.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        okButton.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("h2");
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20), FontSize = 13 };
        body.Classes.Add("caption");
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children =
            {
                heading, body,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, okButton } },
            },
        };
        return await dlg.ShowDialog<bool>(owner);
    }
}
