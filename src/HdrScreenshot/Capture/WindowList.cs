using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HdrScreenshot.Native;
using static HdrScreenshot.Native.Win32;

namespace HdrScreenshot.Capture;

public sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string AppName { get; init; }
    public ImageSource? Icon { get; init; }
    public string Initial => string.IsNullOrEmpty(AppName) ? "?" : AppName[..1].ToUpperInvariant();
}

/// <summary>Alt-Tab style list of top-level windows that can be captured.</summary>
public static class WindowList
{
    private static readonly Dictionary<string, ImageSource?> ExeIconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<WindowInfo> Enumerate(IntPtr exclude)
    {
        var list = new List<WindowInfo>();
        foreach (var hwnd in CapturableWindows(exclude))
        {
            string path = GetProcessPath(hwnd) ?? "";
            string app = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : GetClass(hwnd);
            list.Add(new WindowInfo
            {
                Handle = hwnd,
                Title = GetWindowTitle(hwnd),
                AppName = app,
                Icon = GetIcon(hwnd, path),
            });
        }
        return list;
    }

    /// <summary>Top-level windows in z-order (front first) that are real, visible app windows.</summary>
    public static List<IntPtr> CapturableWindows(IntPtr exclude)
    {
        var result = new List<IntPtr>();
        GetWindowThreadProcessId(exclude, out uint ownPid);
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd, ownPid)) result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Visible window rectangles in z-order, used for click-to-snap in region mode.</summary>
    public static List<RECT> SnapRects(IntPtr exclude) =>
        CapturableWindows(exclude).Where(h => !IsIconic(h)).Select(GetFrameBounds).Where(r => r.Width > 0 && r.Height > 0).ToList();

    private static bool IsCandidate(IntPtr hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        string cls = GetClass(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        var r = GetFrameBounds(hwnd);
        return IsIconic(hwnd) || (r.Width > 1 && r.Height > 1);
    }

    private static ImageSource? GetIcon(IntPtr hwnd, string exePath)
    {
        IntPtr hIcon = IntPtr.Zero;
        // ICON_BIG, ICON_SMALL2, ICON_SMALL — with a short timeout so hung apps don't stall the UI
        foreach (int kind in new[] { 1, 2, 0 })
        {
            SendMessageTimeout(hwnd, WM_GETICON, (IntPtr)kind, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 60, out hIcon);
            if (hIcon != IntPtr.Zero) break;
        }
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, -34 /* GCLP_HICONSM */);
        if (hIcon != IntPtr.Zero)
        {
            var src = FromHIcon(hIcon, own: false);
            if (src != null) return src;
        }
        return ExeIcon(exePath);
    }

    private static ImageSource? ExeIcon(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (ExeIconCache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? icon = null;
        var large = new IntPtr[1];
        if (ExtractIconEx(exePath, 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            icon = FromHIcon(large[0], own: true);
        ExeIconCache[exePath] = icon;
        return icon;
    }

    private static ImageSource? FromHIcon(IntPtr hIcon, bool own)
    {
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (own) DestroyIcon(hIcon); }
    }
}
