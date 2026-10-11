using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HdrScreenshot.Capture;
using HdrScreenshot.Imaging;
using static HdrScreenshot.Native.Win32;
using Path = System.IO.Path;

namespace HdrScreenshot.UI;

public partial class MainWindow : Window
{
    private const int HotkeyRegion = 1, HotkeyWindow = 2, HotkeyScreen = 3;
    private const string HotkeyText = "Ctrl+Alt+S region  ·  Ctrl+Alt+W window  ·  Ctrl+Alt+F screen";

    private readonly Settings _settings = Settings.Load();
    private CaptureEngine? _engine;
    private IntPtr _hwnd;
    private bool _loading = true, _busy, _hotkeysOk;

    private List<DisplayInfo> _displays = new();
    private List<WindowInfo> _windows = new();

    private HdrImage? _image;
    private ImageStats? _stats;
    private WriteableBitmap? _bitmap;
    private bool _rendering, _renderPending;
    private string? _lastSaved;

    private HdrView? _hdrView;
    private bool _viewerDirty;
    private double _viewerWhite;

    private Point? _panStart;
    private Point _panOrigin;

    public MainWindow()
    {
        InitializeComponent();
        LogoImage.Source = EmptyLogo.Source = Logo.Create();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => { if (!_loading && !_busy) { RefreshDisplays(); UpdateDisplayNotice(); } };
        LocationChanged += (_, _) => UpdateDisplayNotice();
        PreviewKeyDown += OnKeyDown;
    }

    // ================================================================== lifecycle

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        UseDarkTitleBar(_hwnd, 0x0017110F);   // caption = #0f1117 (COLORREF is BGR)
        DisableTransitions(_hwnd);             // hide instantly before screen captures
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        if (_settings.Hotkeys) RegisterHotkeys();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        (_settings.Mode switch { SourceMode.Screen => ModeScreen, SourceMode.App => ModeApp, _ => ModeRegion }).IsChecked = true;
        (_settings.DelaySeconds switch { 3 => Delay3, 5 => Delay5, 10 => Delay10, _ => Delay0 }).IsChecked = true;
        (_settings.ToneMap switch
        {
            ToneMapMode.Filmic => TmFilmic, ToneMapMode.Reinhard => TmReinhard, ToneMapMode.Clip => TmClip, _ => TmNatural,
        }).IsChecked = true;
        (_settings.Format switch
        {
            ExportFormat.Jxr => FmtJxr, ExportFormat.HdrPng => FmtHdrPng, ExportFormat.Png => FmtPng,
            ExportFormat.Jpg => FmtJpg, _ => FmtBoth,
        }).IsChecked = true;
        ExposureSlider.Value = _settings.Exposure;
        CursorCheck.IsChecked = _settings.IncludeCursor;
        HideCheck.IsChecked = _settings.HideWhileCapturing;
        AutoSaveCheck.IsChecked = _settings.AutoSave;
        ClipboardCheck.IsChecked = _settings.CopyToClipboard;
        FolderBox.Text = _settings.SaveFolder;
        UpdateExposureLabel();
        ToneHint.Text = ToneMapper.Describe(_settings.ToneMap);
        FormatHint.Text = Exporters.Describe(_settings.Format);
        ApplyMode();

        HotkeyHint.Text = _hotkeysOk ? HotkeyText : "Some hotkeys are in use by another app:\n" + HotkeyText;
        EmptyHint.Text = "Pick a source and press Capture,\nor press Ctrl+Alt+S anywhere to grab a region.";
        UpdateOutputButtons();

        try
        {
            if (!CaptureEngine.IsSupported) throw new PlatformNotSupportedException("Windows.Graphics.Capture isn't available. Windows 10 2004 or newer is required.");
            _engine = new CaptureEngine();
        }
        catch (Exception ex)
        {
            CaptureButton.IsEnabled = false;
            EmptyHint.Text = "Screen capture is unavailable on this PC:\n" + ex.Message;
        }

        RefreshDisplays();
        CreateHdrView();
        _loading = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Save();
        UnregisterHotKey(_hwnd, HotkeyRegion);
        UnregisterHotKey(_hwnd, HotkeyWindow);
        UnregisterHotKey(_hwnd, HotkeyScreen);
        _engine?.Dispose();
    }

    private void RegisterHotkeys()
    {
        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        _hotkeysOk = RegisterHotKey(_hwnd, HotkeyRegion, mods, 'S')
                   & RegisterHotKey(_hwnd, HotkeyWindow, mods, 'W')
                   & RegisterHotKey(_hwnd, HotkeyScreen, mods, 'F');
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            _ = OnHotkey(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { Copy_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.F5) { RefreshDisplays(); RefreshWindows(); e.Handled = true; }
    }

    // ================================================================== sources

    private void RefreshDisplays()
    {
        var selected = MonitorList.SelectedItem as DisplayInfo;
        _displays = Displays.Enumerate();
        MonitorList.ItemsSource = _displays;
        MonitorList.SelectedItem = _displays.FirstOrDefault(d => d.DeviceName == selected?.DeviceName) ?? _displays.FirstOrDefault();

        DisplayStatus.Children.Clear();
        foreach (var d in _displays)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
            item.Children.Add(new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource(d.HdrEnabled ? "Good" : "Faint"),
            });
            item.Children.Add(new TextBlock
            {
                FontSize = 12, Foreground = (Brush)FindResource("Muted"),
                Text = d.HdrEnabled ? $"{d.FriendlyName}  ·  HDR  ·  SDR white {d.SdrWhiteNits:0} nits" : $"{d.FriendlyName}  ·  SDR",
            });
            DisplayStatus.Children.Add(item);
        }
    }

    private void RefreshWindows()
    {
        var selected = (WindowListBox.SelectedItem as WindowInfo)?.Handle;
        _windows = WindowList.Enumerate(_hwnd);
        ApplyWindowFilter();
        if (selected != null)
            WindowListBox.SelectedItem = WindowListBox.Items.Cast<WindowInfo>().FirstOrDefault(w => w.Handle == selected);
    }

    private void ApplyWindowFilter()
    {
        string q = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowListBox.ItemsSource = q.Length == 0 ? _windows : _windows.Where(w =>
            w.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || w.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ApplyMode()
    {
        ScreenPanel.Visibility = _settings.Mode == SourceMode.Screen ? Visibility.Visible : Visibility.Collapsed;
        AppPanel.Visibility = _settings.Mode == SourceMode.App ? Visibility.Visible : Visibility.Collapsed;
        RegionPanel.Visibility = _settings.Mode == SourceMode.Region ? Visibility.Visible : Visibility.Collapsed;
        HideCheck.IsEnabled = _settings.Mode != SourceMode.App;   // window capture isn't affected by overlap
        if (!_busy)
            CaptureButton.Content = _settings.Mode switch
            {
                SourceMode.Screen => "Capture screen", SourceMode.App => "Capture app", _ => "Select region",
            };
        if (_settings.Mode == SourceMode.App) RefreshWindows();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        _settings.Mode = sender == ModeScreen ? SourceMode.Screen : sender == ModeApp ? SourceMode.App : SourceMode.Region;
        if (IsLoaded) ApplyMode();
    }

    private void Delay_Checked(object sender, RoutedEventArgs e) =>
        _settings.DelaySeconds = int.Parse((string)((RadioButton)sender).Tag);

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeCursor = CursorCheck.IsChecked == true;
        _settings.HideWhileCapturing = HideCheck.IsChecked == true;
        _settings.AutoSave = AutoSaveCheck.IsChecked == true;
        _settings.CopyToClipboard = ClipboardCheck.IsChecked == true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyWindowFilter();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((ListBox)sender).SelectedItem != null) Capture_Click(sender, e);
    }

    // ================================================================== capture

    private CaptureOptions Options => new(_settings.IncludeCursor);

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _engine == null) return;
        var mode = _settings.Mode;
        var window = WindowListBox.SelectedItem as WindowInfo;
        var display = MonitorList.SelectedItem as DisplayInfo ?? _displays.FirstOrDefault();
        if (mode == SourceMode.App && window == null)
        {
            SetStatus("Pick an app or window from the list first.", "Warn");
            return;
        }

        SetBusy(true);
        try
        {
            for (int s = _settings.DelaySeconds; s > 0; s--)
            {
                CaptureButton.Content = $"Capturing in {s}…";
                await Task.Delay(1000);
            }
            CaptureButton.Content = "Capturing…";
            bool hide = _settings.HideWhileCapturing;
            HdrImage? img = mode switch
            {
                SourceMode.Screen => await WithMainHidden(hide, true, () => CaptureDisplay(display!)),
                SourceMode.App => await CaptureWindow(window!.Handle),
                _ => await WithMainHidden(hide, true, CaptureRegion),
            };
            if (img != null) await Deliver(img, fromHotkey: false);
        }
        catch (Exception ex) { SetStatus(ex.Message, "Bad"); }
        finally { SetBusy(false); }
    }

    private async Task OnHotkey(int id)
    {
        if (_busy || _engine == null) return;
        SetBusy(true);
        try
        {
            HdrImage? img;
            if (id == HotkeyWindow)
            {
                var fg = GetAncestor(GetForegroundWindow(), 2 /* GA_ROOT */);
                if (fg == IntPtr.Zero || fg == _hwnd) return;
                img = await CaptureWindow(fg);
            }
            else if (id == HotkeyScreen)
                img = await WithMainHidden(true, false, () => CaptureDisplay(Displays.UnderCursor()));
            else
                img = await WithMainHidden(true, false, CaptureRegion);

            if (img != null) await Deliver(img, fromHotkey: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, "Bad");
            BringToFront();
        }
        finally { SetBusy(false); }
    }

    /// <summary>Hides this window (instantly, no fade) while <paramref name="work"/> runs so it isn't in the shot.</summary>
    private async Task<T> WithMainHidden<T>(bool hide, bool activateAfter, Func<Task<T>> work)
    {
        bool hidden = hide && IsVisible && WindowState != WindowState.Minimized;
        if (hidden)
        {
            Hide();
            await Task.Delay(120);   // let DWM present a frame without us
        }
        try { return await work(); }
        finally
        {
            if (hidden)
            {
                ShowActivated = activateAfter;
                Show();
                ShowActivated = true;
            }
        }
    }

    private async Task<HdrImage?> CaptureDisplay(DisplayInfo display)
    {
        // Re-query so a changed SDR white slider or HDR toggle is picked up
        var fresh = Displays.Enumerate().FirstOrDefault(d => d.DeviceName == display.DeviceName) ?? display;
        return await _engine!.CaptureDisplayAsync(fresh, Options);
    }

    private async Task<HdrImage?> CaptureWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) throw new InvalidOperationException("That window has closed. Press Refresh.");
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
            await Task.Delay(400);
        }
        return await _engine!.CaptureWindowAsync(hwnd, Options);
    }

    private async Task<HdrImage?> CaptureRegion()
    {
        var displays = Displays.Enumerate();
        var snapRects = WindowList.SnapRects(_hwnd);
        var captures = new List<HdrImage>();
        foreach (var d in displays) captures.Add(await _engine!.CaptureDisplayAsync(d, Options));

        var tm = CurrentToneMap();
        var frames = await Task.Run(() => captures.Select((c, i) =>
        {
            var bgra = ToneMapper.ToSdrBgra(c, ToneMapper.Analyze(c), tm);
            return (displays[i], (BitmapSource)Exporters.ToBitmap(bgra, c.Width, c.Height));
        }).ToList());

        var result = await RegionSelector.SelectAsync(frames, snapRects);
        if (result == null) return null;

        var img = captures[result.DisplayIndex];
        var b = displays[result.DisplayIndex].Bounds;
        double kx = (double)img.Width / b.Width, ky = (double)img.Height / b.Height;
        var r = result.Rect;
        var cropped = img.Crop((int)Math.Round(r.X * kx), (int)Math.Round(r.Y * ky), (int)Math.Round(r.Width * kx), (int)Math.Round(r.Height * ky));
        return cropped;
    }

    /// <summary>Shows a new capture and runs the auto-save / clipboard actions.</summary>
    private async Task Deliver(HdrImage img, bool fromHotkey)
    {
        await SetCurrentImage(img);

        var notes = new List<string>();
        if (_settings.CopyToClipboard && await CopySdr()) notes.Add("copied to clipboard");
        List<string>? files = null;
        if (_settings.AutoSave)
        {
            files = await SaveCurrent();
            if (files != null) notes.Add("saved " + string.Join(" + ", files.Select(Path.GetExtension)));
        }

        if (fromHotkey && notes.Count > 0 && _bitmap != null)
        {
            string title = _stats?.HasHdrContent == true ? "HDR screenshot captured" : "Screenshot captured";
            string detail = char.ToUpper(notes[0][0]) + string.Join(", ", notes)[1..];
            string? reveal = files?.FirstOrDefault();
            Toast.Show(_bitmap, title, detail, () => { if (reveal != null) RevealFile(reveal); else BringToFront(); });
        }
        else if (fromHotkey) BringToFront();
    }

    /// <summary>Makes <paramref name="img"/> the image both tabs work on (preview, export, HDR viewer).</summary>
    private async Task SetCurrentImage(HdrImage img)
    {
        _image = img;
        _stats = await Task.Run(() => ToneMapper.Analyze(img));
        _lastSaved = null;
        UpdateImageInfo();
        UpdateOutputButtons();
        SetStatus("", "Faint");
        await RenderPreview();
        _viewerDirty = true;
        if (TabViewer.IsChecked == true) await RefreshViewer();
    }

    private void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CaptureButton.IsEnabled = !busy && _engine != null;
        if (!busy) ApplyMode();
    }

    // ================================================================== preview

    private ToneMapSettings CurrentToneMap() => new(_settings.ToneMap, _settings.Exposure);

    private async Task RenderPreview()
    {
        if (_image == null || _stats == null) return;
        if (_rendering) { _renderPending = true; return; }
        _rendering = true;
        try
        {
            do
            {
                _renderPending = false;
                var img = _image; var stats = _stats; var tm = CurrentToneMap();
                bool zebra = ZebraToggle.IsChecked == true;
                var bgra = await Task.Run(() => ToneMapper.ToSdrBgra(img, stats, tm, zebra));
                if (img != _image) { _renderPending = true; continue; }

                if (_bitmap == null || _bitmap.PixelWidth != img.Width || _bitmap.PixelHeight != img.Height)
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    _bitmap = new WriteableBitmap(img.Width, img.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, null);
                    PreviewImage.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), bgra, img.Width * 4, 0);
                EmptyState.Visibility = Visibility.Collapsed;
                PreviewBackdrop.Visibility = Visibility.Visible;
            } while (_renderPending);
        }
        finally { _rendering = false; }
    }

    private void UpdateImageInfo()
    {
        if (_image == null || _stats == null) return;
        var s = _stats;
        string light = s.HasHdrContent
            ? $"Peak {s.PeakNits:N0} nits  ·  {Percent(s.FractionAboveWhite)} brighter than SDR white"
            : _image.FromHdrDisplay ? "No HDR highlights: looks the same in SDR" : "Captured from an SDR display";
        StatsText.Text = $"{_image.Width} × {_image.Height}  ·  {light}";
        StatsText.ToolTip = $"SDR white on the source display: {_image.SdrWhiteNits:0} nits\nBrightest pixel: {s.MaxNits:N0} nits";
        SourceText.Text = $"{_image.SourceName}  ·  {_image.CapturedAt:HH:mm:ss}";
    }

    private static string Percent(double f) => f >= 0.1 ? $"{f * 100:0}%" : f >= 0.001 ? $"{f * 100:0.0}%" : "<0.1%";

    private void Zebra_Click(object sender, RoutedEventArgs e) => _ = RenderPreview();

    private void Zoom_Checked(object sender, RoutedEventArgs e)
    {
        if (PreviewScroll == null) return;
        bool actual = Zoom100.IsChecked == true;
        PreviewScroll.HorizontalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PreviewScroll.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PreviewImage.Stretch = actual ? Stretch.None : Stretch.Uniform;
        PreviewScroll.Cursor = actual ? Cursors.SizeAll : null;
        RenderOptions.SetBitmapScalingMode(PreviewImage, actual ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Zoom100.IsChecked != true) return;
        _panStart = e.GetPosition(PreviewScroll);
        _panOrigin = new Point(PreviewScroll.HorizontalOffset, PreviewScroll.VerticalOffset);
        PreviewScroll.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not Point start) return;
        var p = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_panOrigin.X - (p.X - start.X));
        PreviewScroll.ScrollToVerticalOffset(_panOrigin.Y - (p.Y - start.Y));
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null;
        PreviewScroll.ReleaseMouseCapture();
    }

    // ================================================================== tone mapping + output settings

    private void ToneMap_Checked(object sender, RoutedEventArgs e)
    {
        _settings.ToneMap = sender == TmFilmic ? ToneMapMode.Filmic : sender == TmReinhard ? ToneMapMode.Reinhard
                          : sender == TmClip ? ToneMapMode.Clip : ToneMapMode.Natural;
        if (ToneHint != null) ToneHint.Text = ToneMapper.Describe(_settings.ToneMap);
        _ = RenderPreview();
        ToneMapChangedForViewer();
    }

    private void Exposure_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.Exposure = Math.Round(e.NewValue, 1);
        UpdateExposureLabel();
        _ = RenderPreview();
        ToneMapChangedForViewer();
    }

    private void Exposure_Reset(object sender, MouseButtonEventArgs e) => ExposureSlider.Value = 0;

    private void UpdateExposureLabel()
    {
        if (ExposureValue != null) ExposureValue.Text = _settings.Exposure == 0 ? "0.0 EV" : $"{_settings.Exposure:+0.0;-0.0} EV";
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        _settings.Format = sender == FmtJxr ? ExportFormat.Jxr : sender == FmtHdrPng ? ExportFormat.HdrPng
                         : sender == FmtPng ? ExportFormat.Png : sender == FmtJpg ? ExportFormat.Jpg : ExportFormat.HdrAndSdr;
        if (FormatHint != null) FormatHint.Text = Exporters.Describe(_settings.Format);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where screenshots are saved",
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : "",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.SaveFolder = dlg.FolderName;
            FolderBox.Text = dlg.FolderName;
        }
    }

    // ================================================================== save / copy

    private void UpdateOutputButtons()
    {
        bool has = _image != null;
        SaveButton.IsEnabled = CopyButton.IsEnabled = has;
        RevealButton.IsEnabled = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null || !SaveButton.IsEnabled) return;
        await SaveCurrent();
    }

    private async Task<List<string>?> SaveCurrent()
    {
        if (_image == null || _stats == null) return null;
        var img = _image; var stats = _stats; var tm = CurrentToneMap(); var format = _settings.Format;
        string folder = string.IsNullOrWhiteSpace(_settings.SaveFolder) ? Settings.DefaultFolder : _settings.SaveFolder;
        string basePath = Path.Combine(folder, $"{Exporters.SafeFileName(img.SourceName)} {img.CapturedAt:yyyy-MM-dd HH-mm-ss}");

        SaveButton.IsEnabled = false;
        SaveButton.Content = "Saving…";
        try
        {
            var files = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                return Exporters.Save(format, basePath, img, () => ToneMapper.ToSdrBgra(img, stats, tm));
            });
            _lastSaved = files.FirstOrDefault();
            SetStatus("Saved " + string.Join("  +  ", files.Select(Path.GetFileName)), "Good");
            return files;
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't save: " + ex.Message, "Bad");
            return null;
        }
        finally
        {
            SaveButton.Content = "Save";
            UpdateOutputButtons();
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        if (await CopySdr()) SetStatus("Copied the tone-mapped image to the clipboard", "Good");
    }

    /// <summary>Clipboard gets the SDR version: apps can't paste HDR, and raw scRGB would just look clipped.</summary>
    private async Task<bool> CopySdr()
    {
        if (_image == null || _stats == null) return false;
        var img = _image; var stats = _stats; var tm = CurrentToneMap();
        var (bitmap, png) = await Task.Run(() =>
        {
            var bmp = Exporters.ToBitmap(ToneMapper.ToSdrBgra(img, stats, tm), img.Width, img.Height);
            var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(ms);
            return (bmp, ms);
        });

        var data = new DataObject();
        data.SetImage(bitmap);
        data.SetData("PNG", png);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(80); }
        }
        SetStatus("The clipboard is busy. Try Copy again.", "Warn");
        return false;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && File.Exists(_lastSaved)) RevealFile(_lastSaved);
        else
        {
            string folder = string.IsNullOrWhiteSpace(_settings.SaveFolder) ? Settings.DefaultFolder : _settings.SaveFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    private static void RevealFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private void SetStatus(string text, string brushKey)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = (Brush)FindResource(brushKey);
    }

    // ================================================================== HDR viewer

    private void CreateHdrView()
    {
        try
        {
            _hdrView = new HdrView();
            _hdrView.ViewChanged += () => ViewerZoom.Text = _hdrView.HasImage ? $"{_hdrView.Zoom * 100:0}%" : "";
            _hdrView.FileDropped += path => _ = OpenFile(path);
            HdrHost.Child = _hdrView;
        }
        catch (Exception ex)
        {
            _hdrView = null;
            ViewerInfo.Text = "The HDR viewer couldn't start: " + ex.Message;
        }
        UpdateViewModeText();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (CapturePage == null || ViewerPage == null) return;
        bool viewer = TabViewer.IsChecked == true;
        CapturePage.Visibility = viewer ? Visibility.Collapsed : Visibility.Visible;
        ViewerPage.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;
        if (viewer)
        {
            UpdateDisplayNotice();
            if (_viewerDirty || _viewerWhite != DisplayWhiteForViewer()) _ = RefreshViewer();
        }
    }

    private void ViewHdr_Click(object sender, RoutedEventArgs e) => TabViewer.IsChecked = true;

    /// <summary>SDR white of the monitor this window is on; on an SDR monitor scRGB 1.0 is display white.</summary>
    private double DisplayWhiteForViewer()
    {
        if (_hwnd == IntPtr.Zero) return 80;
        var d = Displays.ForWindow(_hwnd);
        return d.HdrEnabled ? d.SdrWhiteNits : 80;
    }

    private async Task RefreshViewer()
    {
        if (_hdrView == null) return;
        if (_image == null || _stats == null)
        {
            ViewerEmpty.Visibility = Visibility.Visible;
            HdrHost.Visibility = Visibility.Hidden;
            return;
        }
        var img = _image; var stats = _stats; var tm = CurrentToneMap();
        double white = DisplayWhiteForViewer();
        var sdr = await Task.Run(() => ToneMapper.SdrToScRgb(ToneMapper.ToSdrBgra(img, stats, tm), white));
        if (img != _image) return;
        _viewerDirty = false;
        _viewerWhite = white;
        ViewerEmpty.Visibility = Visibility.Collapsed;
        HdrHost.Visibility = Visibility.Visible;
        _hdrView.SetImages(img, sdr, white);

        ViewerName.Text = img.SourceName;
        string light = stats.HasHdrContent
            ? $"Peak {stats.PeakNits:N0} nits  ·  {Percent(stats.FractionAboveWhite)} brighter than SDR white"
            : "No highlights above SDR white";
        ViewerInfo.Text = $"{img.Width} × {img.Height}\n{light}";
        UpdateViewModeText();
    }

    private async void ToneMapChangedForViewer()
    {
        UpdateViewModeText();
        if (_hdrView == null || _image == null || _stats == null) return;
        if (TabViewer.IsChecked != true) { _viewerDirty = true; return; }
        var img = _image; var stats = _stats; var tm = CurrentToneMap(); double white = _viewerWhite;
        var sdr = await Task.Run(() => ToneMapper.SdrToScRgb(ToneMapper.ToSdrBgra(img, stats, tm), white));
        if (img == _image) _hdrView.SetSdr(sdr);
    }

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_hdrView == null) return;
        _hdrView.Mode = ViewSplit.IsChecked == true ? ViewMode.Split : ViewSdr.IsChecked == true ? ViewMode.ToneMapped : ViewMode.Hdr;
        UpdateViewModeText();
    }

    private void UpdateViewModeText()
    {
        if (ViewModeHint == null) return;
        string tm = $"{_settings.ToneMap}, {(_settings.Exposure == 0 ? "0.0" : _settings.Exposure.ToString("+0.0;-0.0"))} EV";
        var mode = _hdrView?.Mode ?? ViewMode.Hdr;
        ViewModeHint.Text = mode switch
        {
            ViewMode.Hdr => "The captured light levels, exactly as they were on screen.",
            ViewMode.ToneMapped => $"What the SDR export looks like ({tm}). Change it on the Capture tab.",
            _ => "Left: HDR. Right: tone-mapped. Drag the white divider to compare.",
        };
        ViewerFooter.Text = _image == null || _stats == null ? "" : mode switch
        {
            ViewMode.Hdr => _stats.HasHdrContent ? $"Showing HDR: highlights up to {_stats.PeakNits:N0} nits" : "Showing HDR (this image has no highlights above SDR white)",
            ViewMode.ToneMapped => $"Showing the tone-mapped SDR version ({tm})",
            _ => $"HDR  |  Tone-mapped ({tm})",
        };
    }

    private void UpdateDisplayNotice()
    {
        if (_hwnd == IntPtr.Zero || DisplayNotice == null) return;
        var d = Displays.ForWindow(_hwnd);
        DisplayNotice.BorderBrush = (Brush)FindResource(d.HdrEnabled ? "Good" : "Warn");
        DisplayNoticeText.Text = d.HdrEnabled
            ? $"This window is on {d.FriendlyName} with HDR on ({d.PeakNits:0} nits peak), so you're seeing real HDR."
            : $"This window is on {d.FriendlyName}, which is in SDR mode, so highlights will clip. Move the window to an HDR display or turn on HDR in Settings › Display.";
        if (TabViewer.IsChecked == true && _image != null && _viewerWhite != DisplayWhiteForViewer()) _ = RefreshViewer();
    }

    private void ViewerFit_Click(object sender, RoutedEventArgs e) => _hdrView?.ZoomToFit();
    private void ViewerActual_Click(object sender, RoutedEventArgs e) => _hdrView?.ZoomActual();

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open an image", Filter = ImageLoader.DialogFilter };
        if (dlg.ShowDialog(this) == true) _ = OpenFile(dlg.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = OpenFile(files[0]);
    }

    private async Task OpenFile(string path)
    {
        if (_busy) return;
        var d = Displays.ForWindow(_hwnd);
        double white = d.HdrEnabled ? d.SdrWhiteNits : Displays.ReferenceWhiteNits;
        try
        {
            var img = await Task.Run(() => ImageLoader.Load(path, white));
            TabViewer.IsChecked = true;
            await SetCurrentImage(img);
        }
        catch (Exception ex)
        {
            TabViewer.IsChecked = true;
            ViewerInfo.Text = $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
        }
    }
}
