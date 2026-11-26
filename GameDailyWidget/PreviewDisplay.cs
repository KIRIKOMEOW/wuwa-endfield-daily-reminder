using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GameDailyWidget;

internal static class PreviewDisplay
{
    internal static Forms.Screen? Screen { get; private set; }
    internal static Forms.Screen[] TestScreens => Screen is {} s ? [s] : Forms.Screen.AllScreens;
    internal static bool SelectSecondary()
    {
        Screen = Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary);
        return Screen != null;
    }

    internal static void Place(Window window)
    {
        if (Screen is not {} screen) return;
        var area = screen.WorkingArea;
        var x = area.Left + 80;
        var y = area.Top + 50;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.Left = x;
        window.Top = y;
        window.SourceInitialized += (_,_) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            const int extendedStyle = -20, noActivate = 0x08000000;
            SetWindowLong(handle,extendedStyle,GetWindowLong(handle,extendedStyle) | noActivate);
            FloatingWindowPlacement.MoveToScreen(window,x,y);
        };
    }

    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(nint h, int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(nint h, int index, int value);
}
