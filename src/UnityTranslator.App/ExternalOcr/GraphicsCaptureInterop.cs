using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace UnityTranslator.App.ExternalOcr;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class GraphicsCaptureInterop
{
    internal static bool IsSupported() =>
        ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureItem") &&
        ApiInformation.IsMethodPresent("Windows.Graphics.Capture.Direct3D11CaptureFramePool", "CreateFreeThreaded") &&
        GraphicsCaptureSession.IsSupported();

    internal static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem",
            new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"));
        var call = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Slot3(factory.ThisPtr));
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint result = 0;
        try
        {
            Marshal.ThrowExceptionForHR(call(factory.ThisPtr, hwnd, in iid, out result));
            if (result == 0) throw new InvalidOperationException("CreateForWindow returned no item.");
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); }
    }

    internal static IDirect3DDevice CreateDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        nint result = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out result));
            if (result == 0) throw new InvalidOperationException("DXGI returned no WinRT device.");
            return MarshalInterface<IDirect3DDevice>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); }
    }

    internal static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        using var surfaceRef = MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        using var access = surfaceRef.As(new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"));
        var call = Marshal.GetDelegateForFunctionPointer<GetInterface>(Slot3(access.ThisPtr));
        var iid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        nint result = 0;
        try
        {
            Marshal.ThrowExceptionForHR(call(access.ThisPtr, in iid, out result));
            if (result == 0) throw new InvalidOperationException("Capture surface has no D3D11 texture.");
            var texture = new ID3D11Texture2D(result); // Transfer the owned out-reference to SharpGen.
            result = 0;
            return texture;
        }
        finally { if (result != 0) Marshal.Release(result); }
    }

    private static nint Slot3(nint pointer) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 3 * nint.Size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint self, nint hwnd, in Guid iid, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterface(nint self, in Guid iid, out nint texture);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
