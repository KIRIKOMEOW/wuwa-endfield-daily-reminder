using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameDailyWidget;

/// <summary>Physical screen coordinates for ordinary top-level windows, including negative monitor origins.</summary>
public static class FloatingWindowPlacement
{
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    public static (int X, int Y) GetScreenPosition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rectangle);
        return (rectangle.Left, rectangle.Top);
    }

    public static void MoveToScreen(Window window, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        // Top-level native positions are already physical screen pixels. Keeping
        // the current size, z-order and activation also preserves topmost mode.
        SetWindowPos(handle, nint.Zero, screenX, screenY, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
