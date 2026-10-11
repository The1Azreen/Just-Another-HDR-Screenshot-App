using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.WIC;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrScreenshot.Imaging;

public enum ExportFormat { Jxr, HdrPng, Png, Jpg, HdrAndSdr }

public static class Exporters
{
    public static string Describe(ExportFormat f) => f switch
    {
        ExportFormat.Jxr => "JPEG XR keeps the full FP16 HDR capture losslessly. Opens in HDR in Windows Photos and Game Bar's gallery.",
        ExportFormat.HdrPng => "16-bit PNG in BT.2020 PQ (cICP tagged). Shows in HDR in Chrome, Edge and other HDR-aware viewers.",
        ExportFormat.Png => "Tone-mapped 8-bit sRGB PNG. Looks right everywhere: Discord, browsers, editors.",
        ExportFormat.Jpg => "Tone-mapped sRGB JPEG at quality 95. Small files for sharing.",
        _ => "Both: a lossless HDR .jxr plus a tone-mapped SDR .png side by side.",
    };

    /// <summary>Writes the files for <paramref name="format"/> next to <paramref name="basePath"/> (no extension) and returns their paths.</summary>
    public static List<string> Save(ExportFormat format, string basePath, HdrImage img, Func<byte[]> sdrBgra)
    {
        var written = new List<string>();
        void Add(string ext, Action<string> write)
        {
            string path = UniquePath(basePath + ext);
            write(path);
            written.Add(path);
        }

        switch (format)
        {
            case ExportFormat.Jxr: Add(".jxr", p => SaveJxr(img, p)); break;
            case ExportFormat.HdrPng: Add(".png", p => SaveHdrPng(img, p)); break;
            case ExportFormat.Png: Add(".png", p => SaveSdr(sdrBgra(), img.Width, img.Height, p, jpeg: false)); break;
            case ExportFormat.Jpg: Add(".jpg", p => SaveSdr(sdrBgra(), img.Width, img.Height, p, jpeg: true)); break;
            case ExportFormat.HdrAndSdr:
                Add(".jxr", p => SaveJxr(img, p));
                Add(".png", p => SaveSdr(sdrBgra(), img.Width, img.Height, p, jpeg: false));
                break;
        }
        return written;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char ch in name.Trim()) sb.Append(invalid.Contains(ch) ? ' ' : ch);
        string s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > 60) s = s[..60].TrimEnd();
        return s.Length == 0 ? "Screenshot" : s;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static BitmapSource ToBitmap(byte[] bgra, int width, int height)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static void SaveSdr(byte[] bgra, int width, int height, string path, bool jpeg)
    {
        BitmapSource src = ToBitmap(bgra, width, height);
        BitmapEncoder enc;
        if (jpeg)
        {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            enc = new JpegBitmapEncoder { QualityLevel = 95 };
        }
        else enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ------------------------------------------------------------------ JPEG XR (scRGB FP16, lossless)

    public static unsafe void SaveJxr(HdrImage img, string path)
    {
        using var factory = new IWICImagingFactory();
        using var stream = factory.CreateStream(path, FileAccess.Write);
        using var encoder = factory.CreateEncoder(ContainerFormatGuids.Wmp);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);

        using var frame = encoder.CreateNewFrame(out var props);
        using (props)
        {
            SetBoolProperty(props.NativePointer, "Lossless", true);
            frame.Initialize(props);
        }
        frame.SetSize((uint)img.Width, (uint)img.Height);
        Guid format = WicPixelFormat.Format64bppRGBAHalf;
        frame.SetPixelFormat(ref format);
        if (format != WicPixelFormat.Format64bppRGBAHalf)
            throw new NotSupportedException("This system's JPEG XR encoder can't write FP16 HDR data.");

        fixed (ushort* p = img.Pixels)
        {
            uint stride = (uint)img.Width * 8;
            frame.WritePixels((uint)img.Height, stride, stride * (uint)img.Height, p);
        }
        frame.Commit();
        encoder.Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPBAG2 { public uint dwType; public ushort vt; public ushort cfType; public uint dwHint; public IntPtr pstrName; public Guid clsid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public short boolVal; }

    /// <summary>IPropertyBag2::Write (vtable slot 4) for a single VT_BOOL encoder option.</summary>
    private static unsafe void SetBoolProperty(IntPtr bag, string name, bool value)
    {
        IntPtr namePtr = Marshal.StringToCoTaskMemUni(name);
        try
        {
            var pb = new PROPBAG2 { vt = 11 /* VT_BOOL */, pstrName = namePtr };
            var v = new VARIANT { vt = 11, boolVal = (short)(value ? -1 : 0) };
            var write = (delegate* unmanaged[Stdcall]<IntPtr, uint, PROPBAG2*, VARIANT*, int>)(*(IntPtr**)bag)[4];
            write(bag, 1, &pb, &v);   // best effort: falls back to the encoder's default quality
        }
        finally { Marshal.FreeCoTaskMem(namePtr); }
    }

    // ------------------------------------------------------------------ HDR PNG (BT.2020 + PQ, 16-bit, cICP)

    public static void SaveHdrPng(HdrImage img, string path)
    {
        int w = img.Width, h = img.Height;
        var lut = HdrImage.HalfToFloat;
        var px = img.Pixels;
        bool hasAlpha = false;
        for (int i = 3; i < px.Length && !hasAlpha; i += 4) hasAlpha = lut[px[i]] < 0.999f;
        int channels = hasAlpha ? 4 : 3;
        int rowBytes = w * channels * 2;

        // Encode every row: scRGB (BT.709 linear, 1.0 = 80 nits) -> BT.2020 linear nits -> PQ 16-bit big endian
        var rows = new byte[h][];
        double[] rowMax = new double[h], rowSum = new double[h];
        Parallel.For(0, h, y =>
        {
            var row = new byte[1 + rowBytes];
            row[0] = 1; // filter: Sub
            var raw = new ushort[w * channels];
            double max = 0, sum = 0;
            int si = y * w * 4;
            for (int x = 0; x < w; x++, si += 4)
            {
                double r = lut[px[si]], g = lut[px[si + 1]], b = lut[px[si + 2]];
                double r2 = (0.6274040 * r + 0.3292820 * g + 0.0433136 * b) * 80;
                double g2 = (0.0690970 * r + 0.9195400 * g + 0.0113612 * b) * 80;
                double b2 = (0.0163916 * r + 0.0880132 * g + 0.8955950 * b) * 80;
                double m = Math.Max(r2, Math.Max(g2, b2));
                if (m > max) max = m;
                sum += Math.Max(m, 0);
                int o = x * channels;
                raw[o] = PqWord(r2); raw[o + 1] = PqWord(g2); raw[o + 2] = PqWord(b2);
                if (hasAlpha) raw[o + 3] = (ushort)Math.Clamp((int)(lut[px[si + 3]] * 65535 + 0.5), 0, 65535);
            }
            rowMax[y] = max; rowSum[y] = sum;

            int bpp = channels * 2;
            var bytes = new byte[rowBytes];
            for (int i = 0; i < raw.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), raw[i]);
            for (int i = 0; i < rowBytes; i++)
                row[1 + i] = (byte)(bytes[i] - (i >= bpp ? bytes[i - bpp] : 0));
            rows[y] = row;
        });

        double maxCll = Math.Min(rowMax.Max(), 10000);
        double maxFall = Math.Min(rowSum.Sum() / ((double)w * h), 10000);

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 16; ihdr[9] = (byte)(hasAlpha ? 6 : 2);
        WriteChunk(fs, "IHDR", ihdr);
        // cICP: BT.2020 primaries (9), PQ transfer (16), RGB (0), full range (1)
        WriteChunk(fs, "cICP", new byte[] { 9, 16, 0, 1 });
        var clli = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(clli, (uint)(maxCll * 10000));
        BinaryPrimitives.WriteUInt32BigEndian(clli.AsSpan(4), (uint)(maxFall * 10000));
        WriteChunk(fs, "cLLi", clli);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                foreach (var row in rows) z.Write(row);
            WriteChunk(fs, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static ushort PqWord(double nits) => (ushort)Math.Clamp((int)(ToneMapper.Pq(Math.Max(nits, 0)) * 65535 + 0.5), 0, 65535);

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        s.Write(header);
        s.Write(data);
        uint crc = Crc32(header[4..], 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
