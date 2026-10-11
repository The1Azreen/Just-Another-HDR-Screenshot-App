using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HdrScreenshot.Capture;
using HdrScreenshot.Native;
using static HdrScreenshot.Native.Win32;

namespace HdrScreenshot.UI;

public sealed record RegionResult(int DisplayIndex, Int32Rect Rect);

/// <summary>
/// Full-screen "frozen frame" selector: one borderless window per monitor showing the tone-mapped capture.
/// Drag to select, click to snap to the window under the cursor, Enter for the whole screen, Esc to cancel.
/// </summary>
public static class RegionSelector
{
    public static Task<RegionResult?> SelectAsync(IReadOnlyList<(DisplayInfo Display, BitmapSource Preview)> frames, List<RECT> snapRects)
    {
        var tcs = new TaskCompletionSource<RegionResult?>();
        var windows = new List<OverlayWindow>();
        void Finish(RegionResult? result)
        {
            if (tcs.Task.IsCompleted) return;
            tcs.TrySetResult(result);
            foreach (var w in windows) w.Close();
        }

        GetCursorPos(out var cursor);
        for (int i = 0; i < frames.Count; i++)
        {
            var w = new OverlayWindow(i, frames[i].Display, frames[i].Preview, snapRects, Finish);
            windows.Add(w);
            w.Show();
        }
        // Keyboard focus goes to the monitor the mouse is on
        (windows.FirstOrDefault(w => w.Display.Bounds.Contains(cursor.X, cursor.Y)) ?? windows.FirstOrDefault())?.Activate();
        return tcs.Task;
    }
}

internal sealed class OverlayWindow : Window
{
    public DisplayInfo Display { get; }
    private readonly int _index;
    private readonly List<RECT> _snapRects;
    private readonly Action<RegionResult?> _finish;

    private readonly Canvas _canvas = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0x05, 0x06, 0x0A)) };
    private readonly Rectangle _selection = new() { Stroke = (Brush)Application.Current.Resources["Accent"], StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Line _hLine = Guide(), _vLine = Guide();
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold };

    private Point? _dragStart;
    private Rect? _current;   // in DIPs

    public OverlayWindow(int index, DisplayInfo display, BitmapSource preview, List<RECT> snapRects, Action<RegionResult?> finish)
    {
        _index = index; Display = display; _snapRects = snapRects; _finish = finish;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 100; Height = 100;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        _sizeLabel = new Border
        {
            Background = (Brush)Application.Current.Resources["Accent"],
            Padding = new Thickness(8, 3, 8, 4),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var image = new Image { Source = preview, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x1A, 0x23)),
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Drag to select  ·  Click a window to snap  ·  Enter = whole screen  ·  Esc to cancel",
                Foreground = (Brush)Application.Current.Resources["Text"],
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
            },
        };

        _dim.IsHitTestVisible = _selection.IsHitTestVisible = false;
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_hLine);
        _canvas.Children.Add(_vLine);
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(_sizeLabel);
        _canvas.Background = Brushes.Transparent;

        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(_canvas);
        root.Children.Add(help);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => UpdateVisuals(null);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) =>
        {
            if (_dragStart != null) { _dragStart = null; ReleaseMouseCapture(); UpdateVisuals(null); }
            else _finish(null);
        };
        KeyDown += OnKey;
        Deactivated += (_, _) => { /* stay up; another overlay on a different monitor may have focus */ };
    }

    private static Line Guide() => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
        StrokeThickness = 1,
        IsHitTestVisible = false,
    };

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DisableTransitions(hwnd);
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        var b = Display.Bounds;
        SetWindowPos(hwnd, HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // When moved onto a monitor with another DPI, force the "suggested" rect to stay exactly on our monitor
        if (msg == WM_DPICHANGED) Marshal.StructureToPtr(Display.Bounds, lParam, false);
        return IntPtr.Zero;
    }

    private double PxPerDip => Display.Bounds.Width / Math.Max(1, ActualWidth);

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _finish(null);
        else if (e.Key is Key.Enter or Key.Space)
            _finish(new RegionResult(_index, new Int32Rect(0, 0, Display.Bounds.Width, Display.Bounds.Height)));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _hLine.X1 = 0; _hLine.X2 = ActualWidth; _hLine.Y1 = _hLine.Y2 = Math.Round(p.Y) + 0.5;
        _vLine.Y1 = 0; _vLine.Y2 = ActualHeight; _vLine.X1 = _vLine.X2 = Math.Round(p.X) + 0.5;

        if (_dragStart is Point s && (Math.Abs(p.X - s.X) > 3 || Math.Abs(p.Y - s.Y) > 3))
            UpdateVisuals(new Rect(s, p), dragging: true);
        else if (_dragStart == null)
            UpdateVisuals(SnapRectAt(p));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not Point s) return;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        _dragStart = null;
        Rect sel = (Math.Abs(p.X - s.X) > 3 || Math.Abs(p.Y - s.Y) > 3)
            ? new Rect(s, p)
            : SnapRectAt(p) ?? new Rect(0, 0, ActualWidth, ActualHeight);
        var px = ToPixels(sel);
        if (px.Width < 2 || px.Height < 2) { UpdateVisuals(null); return; }
        _finish(new RegionResult(_index, px));
    }

    /// <summary>The front-most window under the point, clipped to this monitor, in DIPs.</summary>
    private Rect? SnapRectAt(Point p)
    {
        double k = PxPerDip;
        int sx = Display.Bounds.Left + (int)(p.X * k), sy = Display.Bounds.Top + (int)(p.Y * k);
        foreach (var r in _snapRects)
        {
            if (!r.Contains(sx, sy)) continue;
            var dip = new Rect((r.Left - Display.Bounds.Left) / k, (r.Top - Display.Bounds.Top) / k, r.Width / k, r.Height / k);
            dip.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            return dip.IsEmpty ? null : dip;
        }
        return null;
    }

    private Int32Rect ToPixels(Rect dip)
    {
        double k = PxPerDip;
        int x0 = Math.Clamp((int)Math.Round(dip.Left * k), 0, Display.Bounds.Width);
        int y0 = Math.Clamp((int)Math.Round(dip.Top * k), 0, Display.Bounds.Height);
        int x1 = Math.Clamp((int)Math.Round(dip.Right * k), 0, Display.Bounds.Width);
        int y1 = Math.Clamp((int)Math.Round(dip.Bottom * k), 0, Display.Bounds.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private void UpdateVisuals(Rect? sel, bool dragging = false)
    {
        _current = sel;
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        _hLine.Visibility = _vLine.Visibility = dragging ? Visibility.Collapsed : Visibility.Visible;

        if (sel is not Rect r)
        {
            _dim.Data = full;
            _selection.Visibility = _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
        _selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selection, r.Left - 1);
        Canvas.SetTop(_selection, r.Top - 1);
        _selection.Width = r.Width + 2;
        _selection.Height = r.Height + 2;

        var px = ToPixels(r);
        _sizeText.Text = $"{px.Width} × {px.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelY = r.Top - _sizeLabel.DesiredSize.Height - 6;
        if (labelY < 4) labelY = r.Top + 6;
        Canvas.SetLeft(_sizeLabel, Math.Clamp(r.Left, 4, Math.Max(4, ActualWidth - _sizeLabel.DesiredSize.Width - 4)));
        Canvas.SetTop(_sizeLabel, labelY);
    }
}
