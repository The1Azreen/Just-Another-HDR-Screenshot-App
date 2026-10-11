using System.Runtime.InteropServices;
using HdrScreenshot.Native;
using Vortice.DXGI;
using static HdrScreenshot.Native.Win32;

namespace HdrScreenshot.Capture;

public sealed record DisplayInfo(
    IntPtr Handle,
    string DeviceName,
    string FriendlyName,
    RECT Bounds,
    bool IsPrimary,
    bool HdrEnabled,
    double SdrWhiteNits,
    double PeakNits)
{
    public string Summary =>
        $"{Bounds.Width} × {Bounds.Height}  ·  " +
        (HdrEnabled ? $"HDR on  ·  {PeakNits:0} nits peak" : "SDR");

    public override string ToString() => FriendlyName;
}

/// <summary>Monitor discovery: GDI bounds + DisplayConfig names/SDR white level + DXGI HDR luminance.</summary>
public static class Displays
{
    /// <summary>Reference white used when an SDR display is captured and then exported to HDR (ITU-R BT.2408).</summary>
    public const double ReferenceWhiteNits = 203;

    public static List<DisplayInfo> Enumerate()
    {
        var monitors = new List<(IntPtr h, MONITORINFOEX info)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi)) monitors.Add((h, mi));
            return true;
        }, IntPtr.Zero);

        var config = QueryConfig();
        var dxgi = QueryDxgi();

        var list = new List<DisplayInfo>();
        foreach (var (h, mi) in monitors)
        {
            config.TryGetValue(mi.szDevice, out var cfg);
            dxgi.TryGetValue(h, out var dx);
            bool hdr = cfg.hdr || dx.hdr;
            double white = hdr ? (cfg.white > 0 ? cfg.white : 80) : 80;
            string name = string.IsNullOrWhiteSpace(cfg.name) ? mi.szDevice.TrimStart('\\', '.') : cfg.name;
            list.Add(new DisplayInfo(h, mi.szDevice, name, mi.rcMonitor, (mi.dwFlags & 1) != 0, hdr,
                                     white, dx.peak > 0 ? dx.peak : (hdr ? 1000 : 80)));
        }
        // Primary first, then left to right
        return list.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top).ToList();
    }

    public static DisplayInfo ForMonitor(IntPtr hMonitor) =>
        Enumerate().FirstOrDefault(d => d.Handle == hMonitor) ?? Enumerate().First();

    public static DisplayInfo ForWindow(IntPtr hwnd) => ForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static DisplayInfo UnderCursor()
    {
        GetCursorPos(out var pt);
        return ForMonitor(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
    }

    private static Dictionary<string, (string name, double white, bool hdr)> QueryConfig()
    {
        var result = new Dictionary<string, (string, double, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;

            foreach (var p in paths.Take((int)np))
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf(src), p.sourceInfo.adapterId, p.sourceInfo.id);
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, Marshal.SizeOf(tgt), p.targetInfo.adapterId, p.targetInfo.id);
                string name = DisplayConfigGetDeviceInfo(ref tgt) == 0 ? tgt.monitorFriendlyDeviceName : "";

                var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                white.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, Marshal.SizeOf(white), p.targetInfo.adapterId, p.targetInfo.id);
                // SDRWhiteLevel is in units of 1/1000 of 80 nits
                double whiteNits = DisplayConfigGetDeviceInfo(ref white) == 0 ? white.SDRWhiteLevel / 1000.0 * 80.0 : 0;

                var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf(color), p.targetInfo.adapterId, p.targetInfo.id);
                // bit 0 = supported, bit 1 = enabled. Bit 3 (wide colour enforced) means ACM on an SDR panel, not HDR.
                bool hdr = DisplayConfigGetDeviceInfo(ref color) == 0 && (color.value & 0x3) == 0x3 && (color.value & 0x8) == 0;

                result[src.viewGdiDeviceName] = (name, whiteNits, hdr);
            }
        }
        catch { /* DisplayConfig is best-effort; DXGI still tells us about HDR */ }
        return result;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    private static Dictionary<IntPtr, (bool hdr, double peak)> QueryDxgi()
    {
        var result = new Dictionary<IntPtr, (bool, double)>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    {
                        using (output)
                        {
                            using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                            if (output6 == null) continue;
                            var d = output6.Description1;
                            bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            result[d.Monitor] = (hdr, d.MaxLuminance);
                        }
                    }
                }
            }
        }
        catch { }
        return result;
    }
}
