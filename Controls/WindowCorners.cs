using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CraftDeck.Controls;

public static class WindowCorners
{
    private const int CornerPreference = 33;
    private const int DoNotRound = 1;
    private const int Round = 2;

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            Set(window, window.WindowState == WindowState.Maximized ? DoNotRound : Round);
        };
        window.StateChanged += (_, _) =>
        {
            Set(window, window.WindowState == WindowState.Maximized ? DoNotRound : Round);
        };
    }

    private static void Set(Window window, int pref)
    {
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,
            CornerPreference, ref pref, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
