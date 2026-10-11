using System.Runtime.InteropServices;
using HdrScreenshot.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using WgcItem = global::Windows.Graphics.Capture.GraphicsCaptureItem;
using WgcFramePool = global::Windows.Graphics.Capture.Direct3D11CaptureFramePool;
using WgcSession = global::Windows.Graphics.Capture.GraphicsCaptureSession;
using WinRtDevice = global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using WinRtSurface = global::Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using PixelFormat = global::Windows.Graphics.DirectX.DirectXPixelFormat;

namespace HdrScreenshot.Capture;

public sealed record CaptureOptions(bool IncludeCursor);

/// <summary>
/// Grabs single frames through Windows.Graphics.Capture in R16G16B16A16_FLOAT (scRGB).
/// That is the only Windows capture path that hands back real HDR values for both monitors
/// and individual windows — 8-bit BGRA capture clips everything above SDR white.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly WinRtDevice _winrtDevice;
    private readonly object _gate = new();

    public static bool IsSupported
    {
        get { try { return WgcSession.IsSupported(); } catch { return false; } }
    }

    public CaptureEngine()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out _device!, out _context!).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable));
        try { _winrtDevice = MarshalInterface<WinRtDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }

        TryRequestBorderless();
    }

    public Task<HdrImage> CaptureWindowAsync(IntPtr hwnd, CaptureOptions options)
    {
        var display = Displays.ForWindow(hwnd);
        string name = Native.Win32.GetWindowTitle(hwnd);
        return CaptureAsync(CreateItem(hwnd, forMonitor: false), display, options, string.IsNullOrWhiteSpace(name) ? "Window" : name);
    }

    public Task<HdrImage> CaptureDisplayAsync(DisplayInfo display, CaptureOptions options) =>
        CaptureAsync(CreateItem(display.Handle, forMonitor: true), display, options, display.FriendlyName);

    private async Task<HdrImage> CaptureAsync(WgcItem item, DisplayInfo display, CaptureOptions options, string sourceName)
    {
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("That source has no visible area to capture.");

        using var pool = WgcFramePool.CreateFreeThreaded(_winrtDevice, PixelFormat.R16G16B16A16Float, 1, size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = options.IncludeCursor; } catch { }
        try { session.IsBorderRequired = false; } catch { }   // Windows 11: no yellow capture border

        var tcs = new TaskCompletionSource<HdrImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            if (frame == null || tcs.Task.IsCompleted) return;
            try
            {
                var content = frame.ContentSize;
                tcs.TrySetResult(ReadSurface(frame.Surface, content.Width, content.Height, display, sourceName));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };

        session.StartCapture();
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(4000));
        if (finished != tcs.Task)
            throw new TimeoutException("No frame arrived from the source. If it's a minimised window, restore it first.");
        return await tcs.Task;
    }

    private unsafe HdrImage ReadSurface(WinRtSurface surface, int contentW, int contentH, DisplayInfo display, string sourceName)
    {
        lock (_gate)
        {
            using var texture = GetTexture(surface);
            var desc = texture.Description;
            int w = Math.Min(contentW, (int)desc.Width), h = Math.Min(contentH, (int)desc.Height);

            var stagingDesc = desc with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
            };
            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopyResource(staging, texture);

            var pixels = new ushort[w * h * 4];
            var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                fixed (ushort* dst = pixels)
                {
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * w * 4, (long)w * 8, (long)w * 8);
                }
            }
            finally { _context.Unmap(staging, 0); }

            double sdrWhite = display.SdrWhiteNits;
            if (!display.HdrEnabled)
            {
                // SDR desktop: DWM hands back 0..1 where 1.0 = display white. Re-anchor it at the BT.2408 reference
                // white so HDR exports from an SDR monitor sit at a sensible brightness instead of a dim 80 nits.
                ScaleInPlace(pixels, (float)(Displays.ReferenceWhiteNits / 80.0));
                sdrWhite = Displays.ReferenceWhiteNits;
            }
            return new HdrImage(w, h, pixels, sdrWhite, display.HdrEnabled, sourceName);
        }
    }

    private static void ScaleInPlace(ushort[] px, float k)
    {
        var lut = HdrImage.HalfToFloat;
        Parallel.For(0, px.Length / 4 / 4096 + 1, chunk =>
        {
            int start = chunk * 4096 * 4, end = Math.Min(px.Length, start + 4096 * 4);
            for (int i = start; i < end; i += 4)
                for (int c = 0; c < 3; c++)
                    px[i + c] = BitConverter.HalfToUInt16Bits((Half)(lut[px[i + c]] * k));
        });
    }

    private static unsafe ID3D11Texture2D GetTexture(WinRtSurface surface)
    {
        IntPtr unknown = MarshalInterface<WinRtSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IDirect3DDxgiInterfaceAccess, out IntPtr access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface(REFIID, void**) — vtable slot 3
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid iid = IID_ID3D11Texture2D;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unknown); }
    }

    private static unsafe WgcItem CreateItem(IntPtr handle, bool forMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr hstring));
        try
        {
            Guid interopIid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopIid, out IntPtr interop));
            try
            {
                // IGraphicsCaptureItemInterop: CreateForWindow = slot 3, CreateForMonitor = slot 4
                var vtbl = *(IntPtr**)interop;
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[forMonitor ? 4 : 3];
                Guid itemIid = IID_IGraphicsCaptureItem;
                IntPtr itemPtr;
                int hr = create(interop, handle, &itemIid, &itemPtr);
                if (hr < 0)
                    throw new InvalidOperationException(forMonitor ? "Windows refused to capture that display." :
                        "Windows refused to capture that window (it may be protected, elevated or closed).", Marshal.GetExceptionForHR(hr));
                try { return WgcItem.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(interop); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    private static async void TryRequestBorderless()
    {
        try
        {
            await global::Windows.Graphics.Capture.GraphicsCaptureAccess.RequestAccessAsync(
                global::Windows.Graphics.Capture.GraphicsCaptureAccessKind.Borderless);
        }
        catch { }
    }

    public void Dispose()
    {
        (_winrtDevice as IDisposable)?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
