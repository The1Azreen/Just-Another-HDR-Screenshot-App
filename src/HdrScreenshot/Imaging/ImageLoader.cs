using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.WIC;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrScreenshot.Imaging;

/// <summary>
/// Opens images as scRGB so the viewer can show them in HDR:
/// HDR PNGs tagged with cICP (PQ / HLG), float formats via WIC (JPEG XR, etc.), and ordinary SDR images.
/// </summary>
public static class ImageLoader
{
    public const string DialogFilter =
        "Images (*.jxr;*.wdp;*.hdp;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp)|*.jxr;*.wdp;*.hdp;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp|All files|*.*";

    /// <param name="sdrWhiteNits">Brightness to give SDR white in files that don't carry absolute luminance.</param>
    public static HdrImage Load(string path, double sdrWhiteNits)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            var hdr = TryLoadCicpPng(path, sdrWhiteNits, name);
            if (hdr != null) return hdr;
        }
        return LoadWithWic(path, sdrWhiteNits, name);
    }

    // ------------------------------------------------------------------ WIC: JPEG XR and other float formats, else SDR

    private static unsafe HdrImage LoadWithWic(string path, double sdrWhiteNits, string name)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        frame.GetSize(out uint w, out uint h);

        if (!IsLinearFormat(frame.PixelFormat))
            return LoadSdr(path, sdrWhiteNits, name);

        // Float / fixed-point formats are scRGB already (1.0 = 80 nits)
        using var converter = factory.CreateFormatConverter();
        converter.Initialize(frame, WicPixelFormat.Format64bppRGBAHalf, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var pixels = new ushort[(long)w * h * 4];
        fixed (ushort* p = pixels)
            converter.CopyPixels(w * 8, (uint)(pixels.Length * 2), (IntPtr)p);
        return new HdrImage((int)w, (int)h, pixels, sdrWhiteNits, fromHdrDisplay: true, name);
    }

    private static readonly Dictionary<Guid, string> FormatNames = typeof(WicPixelFormat)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(Guid))
        .GroupBy(f => (Guid)f.GetValue(null)!)
        .ToDictionary(g => g.Key, g => g.First().Name);

    private static bool IsLinearFormat(Guid format) =>
        FormatNames.TryGetValue(format, out var n) && (n.Contains("Half") || n.Contains("Float") || n.Contains("Fixed"));

    /// <summary>Ordinary 8/16-bit image: decode as sRGB and place its white at the given SDR white level.</summary>
    private static HdrImage LoadSdr(string path, double sdrWhiteNits, string name)
    {
        BitmapSource src;
        using (var fs = File.OpenRead(path))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        }
        int w = src.PixelWidth, h = src.PixelHeight;
        var bgra = new byte[w * h * 4];
        src.CopyPixels(bgra, w * 4, 0);

        float k = (float)(sdrWhiteNits / 80.0);
        var lut = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            double lin = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            lut[i] = BitConverter.HalfToUInt16Bits((Half)(lin * k));
        }
        var px = new ushort[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w * 4, end = i + w * 4; i < end; i += 4)
            {
                px[i] = lut[bgra[i + 2]]; px[i + 1] = lut[bgra[i + 1]]; px[i + 2] = lut[bgra[i]];
                px[i + 3] = BitConverter.HalfToUInt16Bits((Half)(bgra[i + 3] / 255f));
            }
        });
        return new HdrImage(w, h, px, sdrWhiteNits, fromHdrDisplay: false, name);
    }

    // ------------------------------------------------------------------ HDR PNG (cICP)

    private static HdrImage? TryLoadCicpPng(string path, double sdrWhiteNits, string name)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8 || BinaryPrimitives.ReadUInt64BigEndian(data) != 0x89504E470D0A1A0A) return null;

        int w = 0, h = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? cicp = null;
        using var idat = new MemoryStream();
        for (int i = 8; i + 12 <= data.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i));
            string type = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            var body = data.AsSpan(i + 8, len);
            switch (type)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(body);
                    h = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8]; colorType = body[9]; interlace = body[12];
                    break;
                case "cICP": cicp = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            if (type == "IEND") break;
            i += 12 + len;
        }

        // Only the HDR case needs this decoder; everything else goes through WIC/WPF
        if (cicp == null || cicp[1] is not (16 or 18)) return null;
        if (interlace != 0 || colorType is not (2 or 6) || depth is not (8 or 16)) return null;

        int channels = colorType == 6 ? 4 : 3, bytesPerSample = depth / 8;
        int bpp = channels * bytesPerSample, stride = w * bpp;
        var raw = new byte[(long)h * stride];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var prev = new byte[stride];
            var row = new byte[stride + 1];
            for (int y = 0; y < h; y++)
            {
                z.ReadExactly(row);
                Unfilter(row[0], row.AsSpan(1), prev, bpp);
                row.AsSpan(1).CopyTo(raw.AsSpan(y * stride));
                row.AsSpan(1).CopyTo(prev);
            }
        }

        bool pq = cicp[1] == 16;
        double[,] m = cicp[0] switch
        {
            9 => new[,] { { 1.6605, -0.5876, -0.0728 }, { -0.1246, 1.1329, -0.0083 }, { -0.0182, -0.1006, 1.1187 } },   // BT.2020
            12 => new[,] { { 1.2249, -0.2247, 0.0 }, { -0.0420, 1.0419, 0.0 }, { -0.0197, -0.0786, 1.0979 } },          // Display P3
            _ => new[,] { { 1.0, 0, 0 }, { 0, 1.0, 0 }, { 0, 0, 1.0 } },
        };

        // Signal -> nits lookup per code value
        int levels = 1 << depth;
        var toNits = new double[levels];
        for (int i = 0; i < levels; i++)
        {
            double e = (double)i / (levels - 1);
            toNits[i] = pq ? ToneMapper.PqInverse(e) : HlgToNits(e);
        }

        var px = new ushort[(long)w * h * 4];
        Parallel.For(0, h, y =>
        {
            int s = y * stride;
            for (int x = 0; x < w; x++, s += bpp)
            {
                double r = toNits[Sample(raw, s, depth)], g = toNits[Sample(raw, s + bytesPerSample, depth)], b = toNits[Sample(raw, s + 2 * bytesPerSample, depth)];
                if (!pq)
                {
                    // HLG OOTF for a 1000-nit reference display (system gamma 1.2)
                    double ys = 0.2627 * r + 0.6780 * g + 0.0593 * b;
                    double k = ys > 0 ? Math.Pow(ys / 1000.0, 0.2) : 0;
                    r *= k; g *= k; b *= k;
                }
                int o = (y * w + x) * 4;
                px[o] = Half16((m[0, 0] * r + m[0, 1] * g + m[0, 2] * b) / 80.0);
                px[o + 1] = Half16((m[1, 0] * r + m[1, 1] * g + m[1, 2] * b) / 80.0);
                px[o + 2] = Half16((m[2, 0] * r + m[2, 1] * g + m[2, 2] * b) / 80.0);
                px[o + 3] = Half16(channels == 4 ? Sample(raw, s + 3 * bytesPerSample, depth) / (double)(levels - 1) : 1.0);
            }
        });
        return new HdrImage(w, h, px, sdrWhiteNits, fromHdrDisplay: true, name);
    }

    private static int Sample(byte[] raw, int i, int depth) => depth == 16 ? (raw[i] << 8) | raw[i + 1] : raw[i];

    private static ushort Half16(double v) => BitConverter.HalfToUInt16Bits((Half)v);

    /// <summary>HLG inverse OETF, scaled so signal 1.0 = 1000 nits before the OOTF is applied.</summary>
    private static double HlgToNits(double e)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        double scene = e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
        return scene * 1000;
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            row[i] = filter switch
            {
                1 => (byte)(row[i] + a),
                2 => (byte)(row[i] + b),
                3 => (byte)(row[i] + ((a + b) >> 1)),
                4 => (byte)(row[i] + Paeth(a, b, c)),
                _ => row[i],
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
