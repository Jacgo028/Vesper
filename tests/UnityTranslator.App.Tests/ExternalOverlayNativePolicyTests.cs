using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ExternalOverlayNativePolicyTests
{
    // Break caught: production initialization excludes translated overlays from remote/composited capture.
    // This owns one hidden production host only; no alignment, presentation, activation or capture occurs.
    [Fact]
    public void Production_hidden_overlay_allows_capture_and_disposes_on_its_own_sta()
    {
        ExceptionDispatchInfo? failure = null;
        var dispatcherStopped = false;
        var thread = new Thread(() =>
        {
            try
            {
                var threadId = GetCurrentThreadId();
                var previousDpi = GetThreadDpiAwarenessContext();
                Assert.Empty(OwnOverlayWindows(threadId));
                nint hwnd = 0;
                try
                {
                    using var host = new ExternalOverlayWindow();
                    hwnd = Assert.Single(OwnOverlayWindows(threadId));
                    Assert.Equal(threadId, GetWindowThreadProcessId(hwnd, out var processId));
                    Assert.Equal((uint)Environment.ProcessId, processId);
                    Assert.False(IsWindowVisible(hwnd));
                    Assert.True(GetWindowDisplayAffinity(hwnd, out var affinity));
                    Assert.Equal(0u, affinity); // WDA_NONE; the old production request returns 17.
                }
                finally
                {
                    if (hwnd != 0) Assert.False(IsWindow(hwnd));
                    Assert.Empty(OwnOverlayWindows(threadId));
                    Assert.True(AreDpiAwarenessContextsEqual(previousDpi, GetThreadDpiAwarenessContext()));
                }
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally
            {
                var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                dispatcher?.InvokeShutdown();
                dispatcherStopped = dispatcher is null || dispatcher.HasShutdownFinished;
            }
        }) { IsBackground = true, Name = "Hidden external overlay policy test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Hidden overlay owner STA did not finish.");
        Assert.True(dispatcherStopped);
        failure?.Throw();
    }

    private static List<nint> OwnOverlayWindows(uint threadId)
    {
        var windows = new List<nint>();
        EnumThreadWindows(threadId, (hwnd, _) =>
        {
            var title = new StringBuilder(128);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString() == "Vesper external OCR") windows.Add(hwnd);
            return true;
        }, 0);
        return windows;
    }

    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
