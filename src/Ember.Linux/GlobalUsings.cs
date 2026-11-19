// The view-models are shared with the Windows (WPF) app. They name WPF's Brush / Color / Colors from
// System.Windows.Media; on Linux those names map to Avalonia's equivalents.
global using Brush = Avalonia.Media.IBrush;
global using Color = Avalonia.Media.Color;
global using Colors = Avalonia.Media.Colors;

namespace System.Windows.Media
{
    /// <summary>Lets the shared files' "using System.Windows.Media;" compile on Linux.</summary>
    internal static class WpfMediaShim { }
}
