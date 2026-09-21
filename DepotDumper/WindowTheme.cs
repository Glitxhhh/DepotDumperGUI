#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DepotDumper.GUI;

/// <summary>Makes a window's title bar follow the app theme (dark/light, and the caption colour on Windows 11).</summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = App.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (window.TryFindResource("BgColor") is Color c)
            {
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int));   // DWMWA_CAPTION_COLOR (Windows 11)
            }
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Applies the title bar theme as soon as the window has a handle.</summary>
    public static void Follow(Window window) => window.SourceInitialized += (_, _) => Apply(window);
}
