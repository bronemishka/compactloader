using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CompactLoader.Interop;

internal static class WindowAppearance
{
    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        // Windows 11 DWM attributes. Older systems simply reject the unsupported
        // attributes and retain the WPF border and WindowChrome behavior.
        var rounded = 3;
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        var color = ((SolidColorBrush)window.FindResource("FrameBorder")).Color;
        var borderColor = color.R | color.G << 8 | color.B << 16;
        DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        var dark = ThemeManager.IsLight ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
