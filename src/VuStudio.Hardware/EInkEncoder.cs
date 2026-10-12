using VuStudio.Core.Faces;

namespace VuStudio.Hardware;

/// <summary>
/// Converts between <see cref="MonoBitmap"/> and the VU1 e-paper frame (<c>docs/vu1-protocol.md</c> §7):
/// column by column (x = 0…199), 18 bytes per column top to bottom, most significant bit = topmost pixel,
/// bit 1 = white. A frame is 3600 bytes.
/// </summary>
public static class EInkEncoder
{
    public const int Width = MonoBitmap.DialWidth;

    public const int Height = MonoBitmap.DialHeight;

    public const int BytesPerColumn = Height / 8;

    public const int FrameLength = Width * BytesPerColumn;

    /// <summary>
    /// Encodes a bitmap. Bitmaps of another size are cropped or padded with white, following
    /// <see cref="MonoBitmap"/>'s rule that pixels outside its bounds read as white.
    /// </summary>
    public static byte[] Encode(MonoBitmap bitmap)
    {
        var frame = new byte[FrameLength];
        Encode(bitmap, frame);
        return frame;
    }

    /// <inheritdoc cref="Encode(MonoBitmap)"/>
    public static void Encode(MonoBitmap bitmap, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (destination.Length < FrameLength)
        {
            throw new ArgumentException($"The destination must hold {FrameLength} bytes.", nameof(destination));
        }

        var i = 0;
        for (var x = 0; x < Width; x++)
        {
            for (var top = 0; top < Height; top += 8)
            {
                var value = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    if (bitmap[x, top + bit])
                    {
                        value |= 0x80 >> bit;
                    }
                }

                destination[i++] = (byte)value;
            }
        }
    }

    /// <summary>Decodes a 3600-byte frame into a 200 × 144 bitmap.</summary>
    public static MonoBitmap Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameLength)
        {
            throw new ArgumentException($"An e-paper frame is {FrameLength} bytes, not {frame.Length}.", nameof(frame));
        }

        var bitmap = new MonoBitmap(Width, Height, white: false);
        var i = 0;
        for (var x = 0; x < Width; x++)
        {
            for (var top = 0; top < Height; top += 8)
            {
                var value = frame[i++];
                for (var bit = 0; bit < 8; bit++)
                {
                    bitmap[x, top + bit] = (value & (0x80 >> bit)) != 0;
                }
            }
        }

        return bitmap;
    }
}
