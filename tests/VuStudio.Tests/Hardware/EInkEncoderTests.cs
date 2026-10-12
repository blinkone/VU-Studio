using VuStudio.Core.Faces;
using VuStudio.Hardware;

namespace VuStudio.Tests.Hardware;

public class EInkEncoderTests
{
    [Fact]
    public void Frame_is_3600_bytes_with_white_as_one()
    {
        Assert.Equal(3600, EInkEncoder.FrameLength);
        Assert.All(EInkEncoder.Encode(new MonoBitmap(white: true)), b => Assert.Equal(0xFF, b));
        Assert.All(EInkEncoder.Encode(new MonoBitmap(white: false)), b => Assert.Equal(0x00, b));
    }

    [Theory]
    [InlineData(0, 0, 0, 0x80)]
    [InlineData(0, 7, 0, 0x01)]
    [InlineData(0, 8, 1, 0x80)]
    [InlineData(0, 143, 17, 0x01)]
    [InlineData(1, 0, 18, 0x80)]
    [InlineData(2, 10, 37, 0x20)]
    [InlineData(199, 143, 3599, 0x01)]
    public void A_single_white_pixel_sets_one_bit(int x, int y, int index, byte expected)
    {
        var bitmap = new MonoBitmap(white: false) { [x, y] = true };
        var frame = EInkEncoder.Encode(bitmap);

        Assert.Equal(expected, frame[index]);
        Assert.Equal(1, frame.Sum(b => System.Numerics.BitOperations.PopCount(b)));
    }

    [Fact]
    public void Decode_inverts_encode()
    {
        var random = new Random(42);
        var bitmap = new MonoBitmap();
        for (var y = 0; y < MonoBitmap.DialHeight; y++)
        {
            for (var x = 0; x < MonoBitmap.DialWidth; x++)
            {
                bitmap[x, y] = random.Next(2) == 0;
            }
        }

        var frame = EInkEncoder.Encode(bitmap);
        Assert.Equal(bitmap, EInkEncoder.Decode(frame));
        Assert.Equal(frame, EInkEncoder.Encode(EInkEncoder.Decode(frame)));
    }

    [Fact]
    public void Other_sizes_are_cropped_or_padded_with_white()
    {
        var small = new MonoBitmap(10, 10, white: false);
        var decoded = EInkEncoder.Decode(EInkEncoder.Encode(small));
        Assert.False(decoded[9, 9]);
        Assert.True(decoded[10, 9]);
        Assert.True(decoded[9, 10]);
        Assert.Equal(100, decoded.CountBlack());

        var large = new MonoBitmap(300, 200, white: false);
        Assert.Equal(MonoBitmap.DialWidth * MonoBitmap.DialHeight, EInkEncoder.Decode(EInkEncoder.Encode(large)).CountBlack());
    }

    [Fact]
    public void Decode_rejects_a_wrong_length() =>
        Assert.Throws<ArgumentException>(() => EInkEncoder.Decode(new byte[3599]));
}
