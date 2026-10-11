using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HdrScreenshot.UI;

/// <summary>8×8 pixel logo in the same spirit as Pixelizer's gem: a glowing orb with a white-hot HDR core.</summary>
public static class Logo
{
    // Keep in sync with tools/make_icon.py
    private static readonly string[] Pixels =
    {
        "..aaaa..",
        ".abbbba.",
        "abcccbba",
        "abcwwcba",
        "abcwwcbd",
        "abbccbda",
        ".abbbda.",
        "..adda..",
    };

    private static readonly Dictionary<char, uint> Colors = new()
    {
        ['a'] = 0xFF3B2F78, ['b'] = 0xFF7C5CFF, ['c'] = 0xFFC9BCFF, ['d'] = 0xFF5A43C9, ['w'] = 0xFFFFFFFF,
    };

    /// <summary>Pre-scaled with nearest neighbour so WPF never gets a chance to blur the pixels.</summary>
    public static BitmapSource Create(int scale = 16)
    {
        int size = 8 * scale;
        var data = new uint[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                data[y * size + x] = Colors.TryGetValue(Pixels[y / scale][x / scale], out var c) ? c : 0;
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, data, size * 4);
        bmp.Freeze();
        return bmp;
    }
}
