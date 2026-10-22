using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

// The boundary excludes the HWND from ordinary unit tests; dispatcher and layout remain real.
internal interface IExternalOverlayHost : IDisposable
{
    event Action? DpiChanged;
    Matrix TransformFromDevice { get; }
    void Align(OcrRect clientOnScreen, bool moveOnly);
    void Present(DrawingGroup drawing);
    void Hide();
}

/// <summary>A hidden-by-default WPF HWND owned only by the presenter's PMv2 STA.</summary>
internal sealed class ExternalOverlayWindow : IExternalOverlayHost
{
    private readonly nint _previousDpiContext;
    private readonly OverlaySurface _surface;
    private HwndSource? _source;
    private bool _disposed;
    public event Action? DpiChanged;

    internal ExternalOverlayWindow()
    {
        _previousDpiContext = OverlayWindowNative.EnterPerMonitorV2();
        try
        {
            // Even FrameworkElement construction can initialize the thread's Dispatcher.
            _surface = new OverlaySurface();
            _source = new HwndSource(new HwndSourceParameters("Vesper external OCR")
            {
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, initially not WS_VISIBLE.
                ExtendedWindowStyle = OverlayWindowNative.ExtendedStyles,
                UsesPerPixelOpacity = true,
                HwndSourceHook = WindowMessage
            }) { SizeToContent = SizeToContent.Manual };
            _source.CompositionTarget.BackgroundColor = Colors.Transparent;
            _source.RootVisual = _surface;
            // Remove this app's capture restriction; actual remote visibility and target isolation need paired evidence.
            OverlayWindowNative.ConfigureCaptureVisibility(_source.Handle);
        }
        catch
        {
            try { _source?.Dispose(); }
            finally { OverlayWindowNative.RestoreDpiContext(_previousDpiContext); }
            throw;
        }
    }

    public Matrix TransformFromDevice => _source!.CompositionTarget.TransformFromDevice;

    public void Align(OcrRect clientOnScreen, bool moveOnly)
    {
        _surface.Dispatcher.VerifyAccess();
        OverlayWindowNative.Position(_source!.Handle, clientOnScreen, moveOnly);
        var transform = TransformFromDevice;
        var size = new Size(clientOnScreen.Width * transform.M11, clientOnScreen.Height * transform.M22);
        _surface.Width = size.Width;
        _surface.Height = size.Height;
        _surface.Measure(size);
        _surface.Arrange(new Rect(size));
    }

    public void Present(DrawingGroup drawing)
    {
        _surface.Dispatcher.VerifyAccess();
        _surface.Drawing = drawing;
        _surface.InvalidateVisual();
        // No Window.Show/layout-position replay: visibility alone never changes the aligned rectangle.
        OverlayWindowNative.ShowWithoutActivation(_source!.Handle);
    }

    public void Hide()
    {
        _surface.Dispatcher.VerifyAccess();
        _surface.Drawing = null;
        _surface.InvalidateVisual();
        OverlayWindowNative.Hide(_source!.Handle);
    }

    public void Dispose()
    {
        _surface.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        try { _source?.Dispose(); }
        finally { OverlayWindowNative.RestoreDpiContext(_previousDpiContext); }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x02E0 && _source is not null) // WM_DPICHANGED: let WPF update its own transform.
            DpiChanged?.Invoke();
        if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x0084) { handled = true; return -1; } // WM_NCHITTEST / HTTRANSPARENT
        return 0;
    }

    private sealed class OverlaySurface : FrameworkElement
    {
        internal DrawingGroup? Drawing;
        internal OverlaySurface() { IsHitTestVisible = false; Focusable = false; }
        protected override void OnRender(DrawingContext context)
        {
            if (Drawing is not null) context.DrawDrawing(Drawing);
        }
    }
}
