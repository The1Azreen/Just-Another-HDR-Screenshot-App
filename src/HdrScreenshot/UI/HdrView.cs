using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using HdrScreenshot.Imaging;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace HdrScreenshot.UI;

public enum ViewMode { Hdr, ToneMapped, Split }

/// <summary>
/// Shows an <see cref="HdrImage"/> in real HDR. WPF can only render 8-bit SDR, so this hosts a Win32 child window
/// with an FP16 scRGB flip-model swap chain; DWM composites it in HDR when the display has HDR turned on.
/// Scroll to zoom around the cursor, drag to pan, and in split mode drag the divider to compare.
/// </summary>
public sealed class HdrView : HwndHost
{
    public event Action? ViewChanged;
    public event Action<string>? FileDropped;

    private IntPtr _hwnd;
    private ID3D11Device? _device;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _hdrBitmap, _sdrBitmap;
    private ID2D1SolidColorBrush? _dividerBrush;
    private int _imgW, _imgH, _viewW = 1, _viewH = 1;
    private float _dividerNits = 203;

    private ViewMode _mode = ViewMode.Hdr;
    private double _zoom = 1, _offsetX, _offsetY;
    private bool _fit = true;
    private double _split = 0.5;
    private (int x, int y, double ox, double oy)? _pan;
    private bool _draggingSplit;

    public double Zoom => _zoom;
    public bool IsFit => _fit;
    public bool HasImage => _hdrBitmap != null;

    public ViewMode Mode
    {
        get => _mode;
        set { _mode = value; Render(); }
    }

    // ================================================================== content

    /// <summary>Uploads the HDR image and its tone-mapped twin (both scRGB FP16 RGBA, same size).</summary>
    public void SetImages(HdrImage hdr, ushort[] sdrScRgb, double sdrWhiteNits)
    {
        if (_dc == null) return;
        bool sizeChanged = hdr.Width != _imgW || hdr.Height != _imgH;
        _hdrBitmap?.Dispose(); _hdrBitmap = null;
        _sdrBitmap?.Dispose(); _sdrBitmap = null;
        _imgW = hdr.Width; _imgH = hdr.Height;
        _hdrBitmap = Upload(hdr.Pixels);
        _sdrBitmap = Upload(sdrScRgb);
        _dividerNits = (float)sdrWhiteNits;
        _dividerBrush?.Dispose();
        float white = _dividerNits / 80f;
        _dividerBrush = _dc.CreateSolidColorBrush(new Color4(white, white, white, 1));
        if (sizeChanged) _fit = true;
        Render();
    }

    /// <summary>Replaces only the tone-mapped image (tone-mapping settings changed).</summary>
    public void SetSdr(ushort[] sdrScRgb)
    {
        if (_dc == null || _hdrBitmap == null) return;
        _sdrBitmap?.Dispose();
        _sdrBitmap = Upload(sdrScRgb);
        Render();
    }

    public void Clear()
    {
        _hdrBitmap?.Dispose(); _hdrBitmap = null;
        _sdrBitmap?.Dispose(); _sdrBitmap = null;
        _imgW = _imgH = 0;
        Render();
    }

    public void ZoomToFit() { _fit = true; Render(); }

    public void ZoomActual()
    {
        if (_imgW == 0) return;
        // Keep the current centre of the view in place
        double cx = (_viewW / 2.0 - _offsetX) / _zoom, cy = (_viewH / 2.0 - _offsetY) / _zoom;
        _fit = false;
        _zoom = 1;
        _offsetX = _viewW / 2.0 - cx;
        _offsetY = _viewH / 2.0 - cy;
        Render();
    }

    private unsafe ID2D1Bitmap1 Upload(ushort[] pixels)
    {
        fixed (ushort* p = pixels)
            return _dc!.CreateBitmap(new SizeI(_imgW, _imgH), (IntPtr)p, (uint)_imgW * 8,
                new BitmapProperties1(new D2DPixelFormat(Format.R16G16B16A16_Float, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
    }

    // ================================================================== window + device

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                               0, 0, 64, 64, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        DragAcceptFiles(_hwnd, true);
        CreateDevice();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hdrBitmap?.Dispose(); _sdrBitmap?.Dispose(); _dividerBrush?.Dispose();
        if (_dc != null) _dc.Target = null;
        _target?.Dispose(); _dc?.Dispose(); _d2dDevice?.Dispose(); _d2dFactory?.Dispose();
        _swapChain?.Dispose(); _device?.Dispose();
        DestroyWindow(hwnd.Handle);
    }

    private void CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out _device!).CheckError();
        if (_device == null) throw new InvalidOperationException("Couldn't create a Direct3D 11 device.");
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        GetClientRect(_hwnd, out var rc);
        _viewW = Math.Max(8, rc.Width); _viewH = Math.Max(8, rc.Height);
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1
        {
            Width = (uint)_viewW,
            Height = (uint)_viewH,
            Format = Format.R16G16B16A16_Float,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        });
        using (var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>())
            sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);   // scRGB: linear, 1.0 = 80 nits

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dc.UnitMode = UnitMode.Pixels;
        CreateTarget();
    }

    private void CreateTarget()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _target = _dc!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.R16G16B16A16_Float, D2DAlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _dc.Target = _target;
    }

    private void Resize()
    {
        if (_swapChain == null || _dc == null) return;
        GetClientRect(_hwnd, out var rc);
        int w = Math.Max(8, rc.Width), h = Math.Max(8, rc.Height);
        if (w == _viewW && h == _viewH) return;
        _viewW = w; _viewH = h;
        _dc.Target = null;
        _target?.Dispose();
        _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.R16G16B16A16_Float, SwapChainFlags.None).CheckError();
        CreateTarget();
        Render();
    }

    // ================================================================== drawing

    private static readonly Color4 Background = new(0.0037f, 0.0044f, 0.0070f, 1f);   // #0c0e14 in linear light

    public void Render()
    {
        if (_dc == null || _swapChain == null) return;
        if (_fit && _imgW > 0)
        {
            _zoom = Math.Min(1.0, Math.Min((_viewW - 24.0) / _imgW, (_viewH - 24.0) / _imgH));
            _offsetX = (_viewW - _imgW * _zoom) / 2;
            _offsetY = (_viewH - _imgH * _zoom) / 2;
        }
        ClampOffsets();

        _dc.BeginDraw();
        _dc.Clear(Background);
        if (_hdrBitmap != null && _sdrBitmap != null)
        {
            var dest = new RawRectF((float)_offsetX, (float)_offsetY, (float)(_offsetX + _imgW * _zoom), (float)(_offsetY + _imgH * _zoom));
            var interp = _zoom < 1 ? InterpolationMode.HighQualityCubic : _zoom >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.Linear;
            switch (_mode)
            {
                case ViewMode.Hdr: _dc.DrawBitmap(_hdrBitmap, dest, 1, interp, null, null); break;
                case ViewMode.ToneMapped: _dc.DrawBitmap(_sdrBitmap, dest, 1, interp, null, null); break;
                default:
                    float sx = (float)Math.Round(_split * _viewW);
                    _dc.PushAxisAlignedClip(new RawRectF(0, 0, sx, _viewH), AntialiasMode.Aliased);
                    _dc.DrawBitmap(_hdrBitmap, dest, 1, interp, null, null);
                    _dc.PopAxisAlignedClip();
                    _dc.PushAxisAlignedClip(new RawRectF(sx, 0, _viewW, _viewH), AntialiasMode.Aliased);
                    _dc.DrawBitmap(_sdrBitmap, dest, 1, interp, null, null);
                    _dc.PopAxisAlignedClip();
                    if (_dividerBrush != null)
                    {
                        _dc.FillRectangle(new RawRectF(sx - 1, 0, sx + 1, _viewH), _dividerBrush);
                        _dc.FillRectangle(new RawRectF(sx - 6, _viewH / 2f - 22, sx + 6, _viewH / 2f + 22), _dividerBrush);
                    }
                    break;
            }
        }
        _dc.EndDraw();
        _swapChain.Present(1, PresentFlags.None);
        ViewChanged?.Invoke();
    }

    private void ClampOffsets()
    {
        if (_imgW == 0) return;
        double w = _imgW * _zoom, h = _imgH * _zoom;
        _offsetX = w <= _viewW ? (_viewW - w) / 2 : Math.Clamp(_offsetX, _viewW - w, 0);
        _offsetY = h <= _viewH ? (_viewH - h) / 2 : Math.Clamp(_offsetY, _viewH - h, 0);
    }

    // ================================================================== input

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return (IntPtr)1;   // HTCLIENT: "static" windows are transparent to the mouse otherwise
            case WM_ERASEBKGND:
                handled = true;
                return (IntPtr)1;
            case WM_PAINT:
                BeginPaint(hwnd, out var ps);
                EndPaint(hwnd, ref ps);
                Render();
                handled = true;
                return IntPtr.Zero;
            case WM_SIZE:
                Resize();
                break;
            case WM_MOUSEWHEEL:
            {
                var pt = new POINTS { X = (short)(lParam.ToInt64() & 0xFFFF), Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF) };
                var p = new Native.POINT { X = pt.X, Y = pt.Y };
                ScreenToClient(hwnd, ref p);
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ZoomAt(p.X, p.Y, Math.Pow(1.25, delta / 120.0));
                handled = true;
                return IntPtr.Zero;
            }
            case WM_LBUTTONDOWN:
            {
                int x = LoX(lParam), y = HiY(lParam);
                SetCapture(hwnd);
                if (NearDivider(x)) _draggingSplit = true;
                else _pan = (x, y, _offsetX, _offsetY);
                handled = true;
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                int x = LoX(lParam), y = HiY(lParam);
                if (_draggingSplit)
                {
                    _split = Math.Clamp(x / (double)_viewW, 0.02, 0.98);
                    Render();
                }
                else if (_pan is { } pan)
                {
                    _fit = false;
                    _offsetX = pan.ox + (x - pan.x);
                    _offsetY = pan.oy + (y - pan.y);
                    Render();
                }
                break;
            }
            case WM_LBUTTONUP:
                _pan = null;
                _draggingSplit = false;
                ReleaseCapture();
                handled = true;
                return IntPtr.Zero;
            case WM_SETCURSOR:
            {
                GetCursorPos(out var cp);
                ScreenToClient(hwnd, ref cp);
                SetCursor(LoadCursor(IntPtr.Zero, NearDivider(cp.X) || _draggingSplit ? IDC_SIZEWE : CanPan ? IDC_SIZEALL : IDC_ARROW));
                handled = true;
                return (IntPtr)1;
            }
            case WM_DROPFILES:
            {
                var sb = new StringBuilder(1024);
                if (DragQueryFile(wParam, 0, sb, sb.Capacity) > 0) FileDropped?.Invoke(sb.ToString());
                DragFinish(wParam);
                handled = true;
                return IntPtr.Zero;
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private bool CanPan => _imgW > 0 && (_imgW * _zoom > _viewW + 0.5 || _imgH * _zoom > _viewH + 0.5);

    private bool NearDivider(int x) => _mode == ViewMode.Split && HasImage && Math.Abs(x - _split * _viewW) <= 8;

    private void ZoomAt(int x, int y, double factor)
    {
        if (_imgW == 0) return;
        double z = Math.Clamp(_zoom * factor, 0.05, 32);
        double ix = (x - _offsetX) / _zoom, iy = (y - _offsetY) / _zoom;
        _fit = false;
        _zoom = z;
        _offsetX = x - ix * z;
        _offsetY = y - iy * z;
        Render();
    }

    private static int LoX(IntPtr l) => (short)(l.ToInt64() & 0xFFFF);
    private static int HiY(IntPtr l) => (short)((l.ToInt64() >> 16) & 0xFFFF);

    // ================================================================== Win32

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A, WM_DROPFILES = 0x0233;
    private static readonly IntPtr IDC_ARROW = 32512, IDC_SIZEALL = 32646, IDC_SIZEWE = 32644;

    [StructLayout(LayoutKind.Sequential)] private struct POINTS { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public Native.RECT rcPaint; public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Native.RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref Native.POINT pt);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, int size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
}
