using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Ember.Views;

public partial class OverlayContent : UserControl
{
    public OverlayContent()
    {
        InitializeComponent();
    }

    /// <summary>Hide the lock bar (used for the static preview in settings).</summary>
    public bool IsPreview { get; set; }

    public void ApplyAppearance()
    {
        var s = App.Settings;
        byte a = (byte)Math.Round(Math.Clamp(s.OverlayOpacity, 0.2, 1) * 255);
        Shell.Background = new SolidColorBrush(Color.FromArgb(a, 0x10, 0x11, 0x16));
        Shell.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(a * 0.18), 0xFF, 0xFF, 0xFF));
        Shell.CornerRadius = new CornerRadius(s.OverlayLayout == Models.OverlayLayout.Pill ? 14 : 14);
        Halo1.Opacity = Halo2.Opacity = a / 255.0;
        if (!IsPreview)
        {
            Scale.ScaleX = Scale.ScaleY = s.OverlayScale;
        }
        if (IsPreview) UnlockLayer.Visibility = Visibility.Collapsed;
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.OverlayLocked = true;
        App.NotifySettingsChanged();
    }
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
