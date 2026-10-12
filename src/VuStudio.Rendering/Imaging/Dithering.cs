using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Imaging;

/// <summary>
/// Converts 8-bit luminance (0 = black, 255 = white, row-major) to one bit. Error diffusion works on the
/// gamma-encoded values, so a threshold of 128 means "mid-grey" as users expect.
/// </summary>
public static class Dithering
{
    /// <summary>The classic 8 × 8 Bayer index matrix (0..63).</summary>
    private static readonly byte[] Bayer8 =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>Dithers <paramref name="luminance"/> into a bitmap of the same size (true = white).</summary>
    /// <param name="threshold">Decision level 0..255; with error diffusion and Bayer it shifts the midpoint.</param>
    public static MonoBitmap Dither(ReadOnlySpan<byte> luminance, int width, int height, DitherMode mode, int threshold = 128)
    {
        var white = Apply(luminance, width, height, mode, threshold);
        var levels = new byte[white.Length];
        for (var i = 0; i < white.Length; i++)
        {
            levels[i] = white[i] ? (byte)255 : (byte)0;
        }

        return MonoBitmap.FromLuminance(width, height, levels);
    }

    /// <summary>Same as <see cref="Dither"/> but returns the raw mask (true = white).</summary>
    public static bool[] Apply(ReadOnlySpan<byte> luminance, int width, int height, DitherMode mode, int threshold = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (luminance.Length < width * height)
        {
            throw new ArgumentException("Buffer too small.", nameof(luminance));
        }

        threshold = Math.Clamp(threshold, 1, 255);
        return mode switch
        {
            DitherMode.FloydSteinberg => FloydSteinberg(luminance, width, height, threshold),
            DitherMode.Atkinson => Atkinson(luminance, width, height, threshold),
            DitherMode.Bayer => Ordered(luminance, width, height, threshold),
            _ => Threshold(luminance, width, height, threshold),
        };
    }

    private static bool[] Threshold(ReadOnlySpan<byte> luminance, int width, int height, int threshold)
    {
        var result = new bool[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = luminance[i] >= threshold;
        }

        return result;
    }

    /// <summary>Floyd–Steinberg with serpentine scanning (avoids the diagonal "worms" of one-way scans).</summary>
    private static bool[] FloydSteinberg(ReadOnlySpan<byte> luminance, int width, int height, int threshold)
    {
        var result = new bool[width * height];
        var current = new float[width + 2];
        var next = new float[width + 2];
        for (var x = 0; x < width; x++)
        {
            current[x + 1] = luminance[x];
        }

        for (var y = 0; y < height; y++)
        {
            Array.Clear(next);
            if (y + 1 < height)
            {
                for (var x = 0; x < width; x++)
                {
                    next[x + 1] = luminance[((y + 1) * width) + x];
                }
            }

            var leftToRight = (y & 1) == 0;
            for (var i = 0; i < width; i++)
            {
                var x = leftToRight ? i : width - 1 - i;
                var dir = leftToRight ? 1 : -1;
                var old = current[x + 1];
                var isWhite = old >= threshold;
                result[(y * width) + x] = isWhite;
                var error = old - (isWhite ? 255f : 0f);
                current[x + 1 + dir] += error * 7 / 16;
                next[x + 1 - dir] += error * 3 / 16;
                next[x + 1] += error * 5 / 16;
                next[x + 1 + dir] += error * 1 / 16;
            }

            (current, next) = (next, current);
        }

        return result;
    }

    /// <summary>Atkinson: spreads 6/8 of the error, which keeps highlights and shadows clean.</summary>
    private static bool[] Atkinson(ReadOnlySpan<byte> luminance, int width, int height, int threshold)
    {
        var result = new bool[width * height];
        var buffer = new float[(width + 4) * (height + 2)];
        var stride = width + 4;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                buffer[(y * stride) + x + 2] = luminance[(y * width) + x];
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + x + 2;
                var old = buffer[i];
                var isWhite = old >= threshold;
                result[(y * width) + x] = isWhite;
                var error = (old - (isWhite ? 255f : 0f)) / 8;
                buffer[i + 1] += error;
                buffer[i + 2] += error;
                buffer[i + stride - 1] += error;
                buffer[i + stride] += error;
                buffer[i + stride + 1] += error;
                buffer[i + (2 * stride)] += error;
            }
        }

        return result;
    }

    /// <summary>Ordered 8 × 8 Bayer dithering; <paramref name="threshold"/> shifts the midpoint.</summary>
    private static bool[] Ordered(ReadOnlySpan<byte> luminance, int width, int height, int threshold)
    {
        var result = new bool[width * height];
        var shift = threshold - 128;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (Bayer8[((y & 7) * 8) + (x & 7)] + 0.5f) * 255f / 64f;
                result[(y * width) + x] = luminance[(y * width) + x] - shift > level;
            }
        }

        return result;
    }
}
