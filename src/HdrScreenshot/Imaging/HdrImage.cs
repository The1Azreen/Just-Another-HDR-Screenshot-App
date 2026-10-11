namespace HdrScreenshot.Imaging;

/// <summary>
/// A captured frame in scRGB: linear light, BT.709 primaries, 1.0 = 80 nits, stored as raw FP16 RGBA.
/// Values above <see cref="SdrWhiteNits"/>/80 are HDR highlights; negative values are wide-gamut colours.
/// </summary>
public sealed class HdrImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>RGBA half-float bit patterns, row-major, no padding.</summary>
    public ushort[] Pixels { get; }
    /// <summary>Brightness that SDR white (UI, desktop) had on the source display.</summary>
    public double SdrWhiteNits { get; }
    /// <summary>True when the source display was in HDR mode.</summary>
    public bool FromHdrDisplay { get; }
    public string SourceName { get; }
    public DateTime CapturedAt { get; } = DateTime.Now;

    /// <summary>FP16 bits → float for every possible half value (256 KB, built once).</summary>
    public static readonly float[] HalfToFloat = BuildHalfTable();

    public HdrImage(int width, int height, ushort[] pixels, double sdrWhiteNits, bool fromHdrDisplay, string sourceName)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer size mismatch.");
        Width = width; Height = height; Pixels = pixels;
        SdrWhiteNits = sdrWhiteNits; FromHdrDisplay = fromHdrDisplay; SourceName = sourceName;
    }

    public HdrImage Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x); h = Math.Clamp(h, 1, Height - y);
        var dst = new ushort[w * h * 4];
        for (int row = 0; row < h; row++)
            Array.Copy(Pixels, ((y + row) * Width + x) * 4, dst, row * w * 4, w * 4);
        return new HdrImage(w, h, dst, SdrWhiteNits, FromHdrDisplay, SourceName);
    }

    private static float[] BuildHalfTable()
    {
        var t = new float[65536];
        for (int i = 0; i < t.Length; i++)
        {
            float f = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            t[i] = float.IsFinite(f) ? f : 0f;
        }
        return t;
    }
}
