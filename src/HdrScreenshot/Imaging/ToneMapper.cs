namespace HdrScreenshot.Imaging;

public enum ToneMapMode { Natural, Filmic, Reinhard, Clip }

public sealed record ToneMapSettings(ToneMapMode Mode, double ExposureStops);

public sealed record ImageStats(double PeakNits, double MaxNits, double FractionAboveWhite, double SdrWhiteNits)
{
    /// <summary>True when there is meaningful content brighter than SDR white.</summary>
    public bool HasHdrContent => PeakNits > SdrWhiteNits * 1.1 && FractionAboveWhite > 0.00005;
}

/// <summary>Converts scRGB captures to 8-bit sRGB, compressing highlights above SDR white.</summary>
public static class ToneMapper
{
    private const int CurveSize = 4096;
    private const int SrgbSize = 16384;
    private static readonly byte[] SrgbTable = BuildSrgbTable();

    public static string Describe(ToneMapMode mode) => mode switch
    {
        ToneMapMode.Natural => "Keeps SDR content exactly as it looked and gently rolls off highlights (BT.2390). Best default.",
        ToneMapMode.Filmic => "Filmic S-curve per channel: punchy contrast, very bright areas fade to white like film.",
        ToneMapMode.Reinhard => "Smooth global compression of everything. Softer and flatter; nothing ever clips.",
        _ => "No tone mapping: anything brighter than SDR white is clipped. What 8-bit screenshot tools do.",
    };

    public static ImageStats Analyze(HdrImage img)
    {
        const int bins = 2048;
        const double minLog = -7, maxLog = 15;   // log2 nits range
        var lut = HdrImage.HalfToFloat;
        var px = img.Pixels;
        int n = img.Width * img.Height;
        var hist = new long[bins];
        float maxNits = 0;
        long above = 0;
        float whiteThreshold = (float)(img.SdrWhiteNits * 1.02 / 80.0);
        object gate = new();

        Parallel.For(0, img.Height, () => (h: new long[bins], max: 0f, above: 0L), (y, _, local) =>
        {
            int i = y * img.Width * 4;
            for (int x = 0; x < img.Width; x++, i += 4)
            {
                float m = MathF.Max(lut[px[i]], MathF.Max(lut[px[i + 1]], lut[px[i + 2]]));
                if (m > local.max) local.max = m;
                if (m > whiteThreshold) local.above++;
                double nits = m * 80.0;
                int b = nits <= 0 ? 0 : (int)((Math.Log2(nits) - minLog) / (maxLog - minLog) * (bins - 1));
                local.h[Math.Clamp(b, 0, bins - 1)]++;
            }
            return local;
        }, local =>
        {
            lock (gate)
            {
                for (int b = 0; b < bins; b++) hist[b] += local.h[b];
                if (local.max > maxNits) maxNits = local.max;
                above += local.above;
            }
        });

        // Robust peak: 99.99th percentile so a handful of hot pixels don't crush the whole image
        long target = (long)(n * 0.9999), acc = 0;
        double peak = 0;
        for (int b = 0; b < bins; b++)
        {
            acc += hist[b];
            if (acc >= target) { peak = Math.Pow(2, minLog + (b + 1) * (maxLog - minLog) / (bins - 1)); break; }
        }
        double max = maxNits * 80.0;
        return new ImageStats(Math.Min(peak, max), max, n == 0 ? 0 : (double)above / n, img.SdrWhiteNits);
    }

    /// <summary>Tone-map into a BGRA32 buffer (straight alpha). Optionally stripes pixels that exceed SDR white.</summary>
    public static byte[] ToSdrBgra(HdrImage img, ImageStats stats, ToneMapSettings s, bool highlightClipping = false)
    {
        var dst = new byte[img.Width * img.Height * 4];
        float scale = (float)(80.0 / img.SdrWhiteNits * Math.Pow(2, s.ExposureStops));
        float peak = (float)Math.Max(1.0, stats.PeakNits / img.SdrWhiteNits * Math.Pow(2, s.ExposureStops));
        float xMax = peak * 1.0001f;
        float[] curve = BuildCurve(s.Mode, peak, img.SdrWhiteNits, xMax);
        bool perChannel = s.Mode == ToneMapMode.Filmic;
        var lut = HdrImage.HalfToFloat;
        var px = img.Pixels;
        int w = img.Width;

        Parallel.For(0, img.Height, y =>
        {
            int i = y * w * 4;
            for (int x = 0; x < w; x++, i += 4)
            {
                float r = lut[px[i]] * scale, g = lut[px[i + 1]] * scale, b = lut[px[i + 2]] * scale;
                float a = lut[px[i + 3]];

                // Bring wide-gamut (negative) colours into sRGB by desaturating towards their luminance
                float min = MathF.Min(r, MathF.Min(g, b));
                if (min < 0)
                {
                    float lum = MathF.Max(0.2126f * r + 0.7152f * g + 0.0722f * b, 0);
                    float t = lum <= 0 ? 0 : lum / (lum - min);
                    r = lum + (r - lum) * t; g = lum + (g - lum) * t; b = lum + (b - lum) * t;
                }

                float m = MathF.Max(r, MathF.Max(g, b));
                bool over = m > 1.0001f;
                if (perChannel)
                {
                    r = Eval(curve, r, xMax); g = Eval(curve, g, xMax); b = Eval(curve, b, xMax);
                }
                else if (m > 0)
                {
                    float k = Eval(curve, m, xMax) / m;
                    r *= k; g *= k; b *= k;
                }

                byte br = Encode(r), bg = Encode(g), bb = Encode(b);
                if (highlightClipping && over && (((x + y) >> 3) & 1) == 0)
                {
                    // Accent-purple zebra stripes over everything brighter than SDR white
                    br = (byte)((br + 0x7c) >> 1); bg = (byte)((bg + 0x5c) >> 1); bb = (byte)((bb + 0xff) >> 1);
                }
                dst[i] = bb; dst[i + 1] = bg; dst[i + 2] = br;
                dst[i + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            }
        });
        return dst;
    }

    private static byte Encode(float v)
    {
        if (!(v > 0)) return 0;
        if (v >= 1) return 255;
        return SrgbTable[(int)(MathF.Sqrt(v) * (SrgbSize - 1) + 0.5f)];
    }

    private static float Eval(float[] curve, float x, float xMax)
    {
        if (!(x > 0)) return 0;
        if (x >= xMax) return curve[^1];
        float t = MathF.Sqrt(x / xMax) * (CurveSize - 1);
        int i = (int)t;
        if (i >= CurveSize - 1) return curve[^1];
        float f = t - i;
        return curve[i] + (curve[i + 1] - curve[i]) * f;
    }

    /// <summary>Samples the 1D curve on a square-root grid so shadows get as much precision as highlights.</summary>
    private static float[] BuildCurve(ToneMapMode mode, double peak, double whiteNits, double xMax)
    {
        var c = new float[CurveSize];
        for (int i = 0; i < CurveSize; i++)
        {
            double t = (double)i / (CurveSize - 1);
            double x = t * t * xMax;
            c[i] = (float)Math.Clamp(mode switch
            {
                ToneMapMode.Natural => Bt2390(x, peak, whiteNits),
                ToneMapMode.Filmic => Hable(x * 2.0) / Hable(peak * 2.0),
                ToneMapMode.Reinhard => x * (1 + x / (peak * peak)) / (1 + x),
                _ => x,
            }, 0, 1);
        }
        return c;
    }

    /// <summary>ITU-R BT.2390 EETF: hermite roll-off in PQ space from the source peak down to SDR white.</summary>
    private static double Bt2390(double x, double peak, double whiteNits)
    {
        if (peak <= 1.0001) return Math.Min(x, 1);
        double srcPeak = Pq(peak * whiteNits);
        double e1 = Pq(x * whiteNits) / srcPeak;
        double maxLum = Pq(whiteNits) / srcPeak;
        double ks = 1.5 * maxLum - 0.5;
        double e2 = e1;
        if (e1 > ks)
        {
            double tt = (e1 - ks) / (1 - ks), t2 = tt * tt, t3 = t2 * tt;
            e2 = (2 * t3 - 3 * t2 + 1) * ks + (t3 - 2 * t2 + tt) * (1 - ks) + (-2 * t3 + 3 * t2) * maxLum;
        }
        return PqInverse(Math.Min(e2, maxLum) * srcPeak) / whiteNits;
    }

    private static double Hable(double x)
    {
        const double A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
        return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
    }

    // SMPTE ST 2084 (PQ), nits <-> 0..1 signal
    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    public static double Pq(double nits)
    {
        double y = Math.Pow(Math.Clamp(nits / 10000.0, 0, 1), M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    public static double PqInverse(double e)
    {
        double p = Math.Pow(Math.Clamp(e, 0, 1), 1 / M2);
        return 10000.0 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1 / M1);
    }

    private static byte[] BuildSrgbTable()
    {
        var t = new byte[SrgbSize];
        for (int i = 0; i < SrgbSize; i++)
        {
            double s = (double)i / (SrgbSize - 1);
            double v = s * s;
            double e = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            t[i] = (byte)Math.Clamp((int)Math.Round(e * 255), 0, 255);
        }
        return t;
    }
}
