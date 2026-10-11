using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HdrScreenshot.UI;

/// <summary>Small non-activating notification shown after a hotkey capture is auto-saved.</summary>
public sealed class Toast : Window
{
    private static Toast? _current;

    public static void Show(BitmapSource thumbnail, string title, string detail, Action? onClick)
    {
        _current?.Close();
        _current = new Toast(thumbnail, title, detail, onClick);
        _current.Show();
    }

    private Toast(BitmapSource thumbnail, string title, string detail, Action? onClick)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = (Brush)Application.Current.Resources["Card"];
        BorderBrush = (Brush)Application.Current.Resources["Accent"];
        BorderThickness = new Thickness(1);
        FontFamily = new FontFamily("Segoe UI");
        Cursor = System.Windows.Input.Cursors.Hand;
        UseLayoutRounding = true;

        var thumb = new Image { Source = thumbnail, MaxWidth = 128, MaxHeight = 72, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13.5, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Text"],
        });
        text.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["Muted"],
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        row.Children.Add(new Border { Background = (Brush)Application.Current.Resources["Field"], Child = thumb });
        row.Children.Add(text);
        Content = row;

        SourceInitialized += (_, _) => Native.Win32.DisableTransitions(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        MouseLeftButtonUp += (_, _) => { onClick?.Invoke(); Close(); };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (_current == this) _current = null; };
    }
}
