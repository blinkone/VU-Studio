using VuStudio.Core.Lighting;

namespace VuStudio.Session.Media;

/// <summary>Byte order of 32-bit pixels.</summary>
public enum PixelOrder
{
    Rgba,

    /// <summary>Windows/GDI+ <c>Format32bppArgb</c> in memory, and WIC <c>Bgra8</c>.</summary>
    Bgra,
}

/// <summary>
/// Picks a pleasing, saturated dominant color of an image (album art) for a backlight
/// (docs/integrations/windows-sources.md §7.4): a histogram over 24 hue buckets weighted by saturation × value,
/// ignoring near-greys (S &lt; 0.25 or V &lt; 0.2). Neighbouring buckets are smoothed so a hue on a bucket
/// boundary is not split. The winner is brightened to V ≥ 0.6 and S ≥ 0.45. Images with no colourful pixels
/// fall back to their average color (brightened, still neutral).
/// </summary>
public static class DominantColor
{
    public const int Buckets = 24;
    public const double MinSaturation = 0.25;
    public const double MinValue = 0.2;
    public const double OutputMinValue = 0.6;
    public const double OutputMinSaturation = 0.45;

    /// <summary>Pixels with alpha below this are ignored.</summary>
    public const byte MinAlpha = 128;

    public static string? FromRgba(ReadOnlySpan<byte> pixels, bool useAlpha = true) => Compute(pixels, PixelOrder.Rgba, useAlpha);

    public static string? FromBgra(ReadOnlySpan<byte> pixels, bool useAlpha = true) => Compute(pixels, PixelOrder.Bgra, useAlpha);

    /// <summary>Returns <c>#RRGGBB</c>, or <c>null</c> when there is no (opaque) pixel at all.</summary>
    public static string? Compute(ReadOnlySpan<byte> pixels, PixelOrder order, bool useAlpha = true)
    {
        var weights = new double[Buckets];
        var sumX = new double[Buckets];
        var sumY = new double[Buckets];
        var sumS = new double[Buckets];
        var sumV = new double[Buckets];
        double avgR = 0, avgG = 0, avgB = 0;
        var opaque = 0;

        var count = pixels.Length / 4;
        for (var i = 0; i < count; i++)
        {
            var p = pixels.Slice(i * 4, 4);
            if (useAlpha && p[3] < MinAlpha)
            {
                continue;
            }

            byte r, g, b;
            if (order == PixelOrder.Rgba)
            {
                (r, g, b) = (p[0], p[1], p[2]);
            }
            else
            {
                (r, g, b) = (p[2], p[1], p[0]);
            }

            opaque++;
            avgR += r;
            avgG += g;
            avgB += b;

            var (h, s, v) = ToHsv(r, g, b);
            if (s < MinSaturation || v < MinValue)
            {
                continue;
            }

            var weight = s * v;
            var bucket = (int)(h / (360.0 / Buckets)) % Buckets;
            var radians = h * Math.PI / 180;
            weights[bucket] += weight;
            sumX[bucket] += Math.Cos(radians) * weight;
            sumY[bucket] += Math.Sin(radians) * weight;
            sumS[bucket] += s * weight;
            sumV[bucket] += v * weight;
        }

        if (opaque == 0)
        {
            return null;
        }

        var total = weights.Sum();
        if (total < 0.01 * opaque)
        {
            return Neutral(avgR / opaque, avgG / opaque, avgB / opaque);
        }

        var best = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < Buckets; i++)
        {
            var score = weights[i] + (0.5 * (weights[(i + Buckets - 1) % Buckets] + weights[(i + 1) % Buckets]));
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        double w = 0, x = 0, y = 0, sat = 0, val = 0;
        foreach (var i in new[] { (best + Buckets - 1) % Buckets, best, (best + 1) % Buckets })
        {
            w += weights[i];
            x += sumX[i];
            y += sumY[i];
            sat += sumS[i];
            val += sumV[i];
        }

        var hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        var color = RgbwColor.FromHsv(hue, Math.Clamp(Math.Max(sat / w, OutputMinSaturation), 0, 1), Math.Clamp(Math.Max(val / w, OutputMinValue), 0, 1));
        return color.ToHex();
    }

    /// <summary>The average color, brightened so its brightest channel reaches V ≥ 0.6 (a black image gives a mid grey).</summary>
    private static string Neutral(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var floor = OutputMinValue * 255;
        if (max < 1)
        {
            (r, g, b, max) = (floor, floor, floor, floor);
        }
        else if (max < floor)
        {
            var k = floor / max;
            (r, g, b) = (r * k, g * k, b * k);
        }

        return new RgbwColor(ToByte(r), ToByte(g), ToByte(b)).ToHex();

        static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    internal static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == rf)
            {
                h = 60 * (((gf - bf) / delta) % 6);
            }
            else if (max == gf)
            {
                h = 60 * (((bf - rf) / delta) + 2);
            }
            else
            {
                h = 60 * (((rf - gf) / delta) + 4);
            }
        }

        if (h < 0)
        {
            h += 360;
        }

        return (h, max == 0 ? 0 : delta / max, max);
    }
}
