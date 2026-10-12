namespace VuStudio.Core.Faces;

/// <summary>
/// A 1-bit image. <c>true</c> = white (paper), <c>false</c> = black (ink).
/// The VU1 e-paper is 200 × 144; other sizes are allowed for previews and tests.
/// </summary>
public sealed class MonoBitmap : IEquatable<MonoBitmap>
{
    public const int DialWidth = 200;
    public const int DialHeight = 144;

    private readonly bool[] _pixels;

    public MonoBitmap(int width = DialWidth, int height = DialHeight, bool white = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _pixels = new bool[width * height];
        if (white)
        {
            Array.Fill(_pixels, true);
        }
    }

    private MonoBitmap(int width, int height, bool[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Gets or sets a pixel; <c>true</c> = white. Out-of-range reads return white, writes are ignored.</summary>
    public bool this[int x, int y]
    {
        get => (uint)x < (uint)Width && (uint)y < (uint)Height ? _pixels[(y * Width) + x] : true;
        set
        {
            if ((uint)x < (uint)Width && (uint)y < (uint)Height)
            {
                _pixels[(y * Width) + x] = value;
            }
        }
    }

    public ReadOnlySpan<bool> Pixels => _pixels;

    public MonoBitmap Clone() => new(Width, Height, (bool[])_pixels.Clone());

    public void Invert()
    {
        for (var i = 0; i < _pixels.Length; i++)
        {
            _pixels[i] = !_pixels[i];
        }
    }

    public int CountBlack() => _pixels.Count(p => !p);

    /// <summary>Builds a bitmap from 8-bit luminance (row-major), thresholding at 128.</summary>
    public static MonoBitmap FromLuminance(int width, int height, ReadOnlySpan<byte> luminance, byte threshold = 128)
    {
        if (luminance.Length < width * height)
        {
            throw new ArgumentException("Buffer too small.", nameof(luminance));
        }

        var pixels = new bool[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = luminance[i] >= threshold;
        }

        return new MonoBitmap(width, height, pixels);
    }

    /// <summary>64-bit FNV-1a hash of the packed pixels; used to skip identical uploads.</summary>
    public ulong ComputeHash()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset ^ (ulong)Width ^ ((ulong)Height << 16);
        byte acc = 0;
        var bits = 0;
        foreach (var p in _pixels)
        {
            acc = (byte)((acc << 1) | (p ? 1 : 0));
            if (++bits == 8)
            {
                hash = (hash ^ acc) * prime;
                acc = 0;
                bits = 0;
            }
        }

        if (bits > 0)
        {
            hash = (hash ^ acc) * prime;
        }

        return hash;
    }

    public bool Equals(MonoBitmap? other) =>
        other is not null && other.Width == Width && other.Height == Height && other._pixels.AsSpan().SequenceEqual(_pixels);

    public override bool Equals(object? obj) => Equals(obj as MonoBitmap);

    public override int GetHashCode() => (int)ComputeHash();
}
