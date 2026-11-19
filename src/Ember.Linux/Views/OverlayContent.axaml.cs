using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Ember.Views;

public partial class OverlayContent : UserControl
{
    public OverlayContent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Static preview on the Overlay page: no unlock layer, no scaling.</summary>
    public bool IsPreview { get; set; }

    public void ApplyAppearance()
    {
        var s = App.Settings;
        byte a = (byte)Math.Round(Math.Clamp(s.OverlayOpacity, 0.2, 1) * 255);
        var shell = this.FindControl<Border>("Shell")!;
        shell.Background = new SolidColorBrush(Color.FromArgb(a, 0x10, 0x11, 0x16));
        shell.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(a * 0.18), 0xFF, 0xFF, 0xFF));
        this.FindControl<Border>("Halo1")!.Opacity = this.FindControl<Border>("Halo2")!.Opacity = a / 255.0;
        double scale = IsPreview ? 1 : s.OverlayScale;
        this.FindControl<LayoutTransformControl>("ScaleHost")!.LayoutTransform = new ScaleTransform(scale, scale);
        if (IsPreview) this.FindControl<Grid>("UnlockLayer")!.IsVisible = false;
    }

    private void LockButton_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.OverlayLocked = true;
        App.NotifySettingsChanged();
    }
}
