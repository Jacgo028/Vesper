using System.ComponentModel;
using System.Runtime.InteropServices;
using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

internal static class OverlayWindowNative
{
    // LAYERED + TRANSPARENT is the cross-process hit-test policy; the surface flag alone is insufficient.
    internal const int ExtendedStyles = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;

    internal static nint EnterPerMonitorV2()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("External OCR overlay requires Windows 10 2004 (19041) or later.");
        var previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) throw Failure("Cannot create the external overlay in per-monitor-v2 DPI context");
        return previous;
    }

    internal static void RestoreDpiContext(nint previous) => SetThreadDpiAwarenessContext(previous);

    internal static void ConfigureCaptureVisibility(nint hwnd)
    {
        if (!SetWindowDisplayAffinity(hwnd, 0)) // WDA_NONE: allow remote/composited capture of this overlay.
            throw Failure("External overlay capture-visible configuration failed; overlay is unavailable");
    }

    internal static void Position(nint hwnd, OcrRect rect, bool moveOnly)
    {
        const uint noActivate = 0x0010;
        const uint noSize = 0x0001;
        if (!SetWindowPos(hwnd, -1, rect.X, rect.Y, rect.Width, rect.Height, noActivate | (moveOnly ? noSize : 0)))
            throw Failure("Cannot align the external overlay to the physical client rectangle");
    }

    internal static void ShowWithoutActivation(nint hwnd)
    {
        // An unchanged placement may outlive an external z-order demotion.
        // Recover TOPMOST on accepted presentation without replaying geometry or focus.
        const uint noActivate = 0x0010, noMove = 0x0002, noSize = 0x0001, showWindow = 0x0040;
        if (!SetWindowPos(hwnd, -1, 0, 0, 0, 0, noActivate | noMove | noSize | showWindow))
            throw Failure("Cannot show the external overlay above its game without activation");
        if ((GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x00000008) != 0) return;
        // A background first-show request can succeed without changing TOPMOST.
        // Reset only this owned window's layer, then retry without geometry or activation.
        if (!SetWindowPos(hwnd, -2, 0, 0, 0, 0, noActivate | noMove | noSize) ||
            !SetWindowPos(hwnd, -1, 0, 0, 0, 0, noActivate | noMove | noSize | showWindow))
            throw Failure("Cannot recover the external overlay layer without activation");
        if ((GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x00000008) == 0)
            throw new InvalidOperationException("Windows did not apply the external overlay's topmost layer.");
    }
    internal static void Hide(nint hwnd) => ShowWindow(hwnd, 0);

    private static Win32Exception Failure(string operation) => new(Marshal.GetLastWin32Error(), operation);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
}
