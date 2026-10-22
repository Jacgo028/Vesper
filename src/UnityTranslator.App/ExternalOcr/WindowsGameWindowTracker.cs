using System.Runtime.InteropServices;
using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

internal sealed class WindowsGameWindowTracker : IExternalWindowTracker
{
    public WindowObservation Observe(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || processId <= 0)
            return new(null, false, false, false, false);
        var foreground = GetForegroundWindow();
        var target = TryGetTarget(foreground, processId);
        if (target is null)
        {
            // The largest visible top-level client is preferred over launchers/tool windows.
            EnumWindows((hwnd, _) =>
            {
                var candidate = TryGetTarget(hwnd, processId);
                if (candidate is not null && (target is null ||
                    (long)candidate.ClientOnScreen.Width * candidate.ClientOnScreen.Height >
                    (long)target.ClientOnScreen.Width * target.ClientOnScreen.Height)) target = candidate;
                return true;
            }, 0);
        }
        return new(target, target?.Hwnd == foreground, target is not null && IsIconic(target.Hwnd),
            target is not null && IsWindowVisible(target.Hwnd), target is not null && IsCloaked(target.Hwnd));
    }

    internal static WindowTarget? TryGetTarget(nint hwnd, int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || hwnd == 0 || !IsWindow(hwnd) ||
            !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var actualPid);
        if (actualPid != processId) return null;
        // GetClientRect/ClientToScreen are synchronous inside a verified PMv2 scope.
        // DWM extended frame bounds are physical; never multiply them by window DPI.
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        if (previous == 0) return null;
        try
        {
            if (!GetClientRect(hwnd, out var client)) return null;
            var first = new Point { X = client.Left, Y = client.Top };
            var last = new Point { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(hwnd, ref first) || !ClientToScreen(hwnd, ref last) ||
                DwmGetWindowAttribute(hwnd, 9, out Rect frame, Marshal.SizeOf<Rect>()) < 0) return null;
            var clientRect = MakeRect(first.X, first.Y, last.X, last.Y);
            var frameRect = MakeRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
            if (clientRect is null || frameRect is null ||
                !ExternalFrameRules.TryClientCrop(frameRect, clientRect, frameRect.Width, frameRect.Height, out _)) return null;
            GetWindowThreadProcessId(hwnd, out actualPid);
            return actualPid == processId && IsWindow(hwnd)
                ? new(hwnd, processId, clientRect, frameRect) : null;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static OcrRect? MakeRect(int left, int top, int right, int bottom)
    {
        var width = (long)right - left;
        var height = (long)bottom - top;
        return width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue
            ? new(left, top, (int)width, (int)height) : null;
    }
    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) < 0 || cloaked != 0;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
