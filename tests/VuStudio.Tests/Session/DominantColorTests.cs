using VuStudio.Core.Lighting;
using VuStudio.Session.Media;

namespace VuStudio.Tests.Session;

public class DominantColorTests
{
    private static byte[] Image(params (int Count, byte R, byte G, byte B, byte A)[] runs)
    {
        var list = new List<byte>();
        foreach (var (count, r, g, b, a) in runs)
        {
            for (var i = 0; i < count; i++)
            {
                list.AddRange([r, g, b, a]);
            }
        }

        return list.ToArray();
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var copy = (byte[])rgba.Clone();
        for (var i = 0; i < copy.Length; i += 4)
        {
            (copy[i], copy[i + 2]) = (copy[i + 2], copy[i]);
        }

        return copy;
    }

    private static (double H, double S, double V) Hsv(string hex)
    {
        var c = RgbwColor.Parse(hex);
        return DominantColor.ToHsv(c.R, c.G, c.B);
    }

    [Fact]
    public void Solid_color_is_returned()
    {
        Assert.Equal("#FF0000", DominantColor.FromRgba(Image((64, 255, 0, 0, 255))));
        Assert.Equal("#0000FF", DominantColor.FromBgra(ToBgra(Image((64, 0, 0, 255, 255)))));
    }

    [Fact]
    public void Rgba_and_bgra_orders_agree()
    {
        var rgba = Image((300, 30, 30, 30, 255), (100, 20, 160, 90, 255), (50, 220, 120, 10, 255));
        Assert.Equal(DominantColor.FromRgba(rgba), DominantColor.FromBgra(ToBgra(rgba)));
    }

    [Fact]
    public void Greys_are_ignored_and_the_colourful_hue_wins()
    {
        // 70 % grey/black/white, 30 % blue: blue, not grey.
        var image = Image((300, 128, 128, 128, 255), (200, 10, 10, 10, 255), (200, 250, 250, 250, 255), (300, 30, 60, 200, 255));
        var (h, s, v) = Hsv(DominantColor.FromRgba(image)!);
        Assert.InRange(h, 220, 235);
        Assert.True(s > 0.7);
        Assert.True(v >= 0.6);
    }

    [Fact]
    public void Weight_favours_saturated_bright_pixels_over_many_dull_ones()
    {
        // Many dull, barely-saturated olive pixels vs fewer vivid orange ones.
        var image = Image((500, 110, 105, 80, 255), (150, 255, 120, 0, 255));
        var (h, _, _) = Hsv(DominantColor.FromRgba(image)!);
        Assert.InRange(h, 20, 35);
    }

    [Fact]
    public void A_hue_split_across_a_bucket_boundary_is_not_penalised()
    {
        // Two nearby reds straddling 0°/360° beat a slightly bigger single green patch.
        var image = Image((200, 255, 0, 20, 255), (200, 255, 20, 0, 255), (300, 0, 200, 0, 255));
        var (h, _, _) = Hsv(DominantColor.FromRgba(image)!);
        Assert.True(h < 10 || h > 350, $"hue {h}");
    }

    [Fact]
    public void Dark_colours_are_brightened_for_the_backlight()
    {
        var (h, s, v) = Hsv(DominantColor.FromRgba(Image((100, 0, 60, 0, 255)))!);
        Assert.InRange(h, 115, 125);
        Assert.True(s > 0.9);
        Assert.InRange(v, 0.59, 0.61);
    }

    [Fact]
    public void Pastels_get_a_minimum_saturation()
    {
        var (_, s, _) = Hsv(DominantColor.FromRgba(Image((100, 255, 190, 190, 255)))!);
        Assert.True(s >= 0.44);
    }

    [Fact]
    public void Grey_images_fall_back_to_their_average_as_a_neutral_color()
    {
        var bw = DominantColor.FromRgba(Image((100, 0, 0, 0, 255), (100, 100, 100, 100, 255)))!;
        var c = RgbwColor.Parse(bw);
        Assert.Equal(c.R, c.G);
        Assert.Equal(c.G, c.B);
        Assert.InRange(c.R, 150, 156); // average 50 brightened to V = 0.6

        Assert.Equal("#999999", DominantColor.FromRgba(Image((10, 0, 0, 0, 255))));
        Assert.Equal("#C8C8C8", DominantColor.FromRgba(Image((10, 200, 200, 200, 255))));
    }

    [Fact]
    public void Transparent_pixels_are_ignored_unless_alpha_is_disabled()
    {
        var image = Image((500, 255, 0, 0, 0), (50, 0, 255, 0, 255));
        Assert.Equal("#00FF00", DominantColor.FromRgba(image));
        Assert.Equal("#FF0000", DominantColor.FromRgba(image, useAlpha: false));
        Assert.Null(DominantColor.FromRgba(Image((10, 255, 0, 0, 0))));
        Assert.Null(DominantColor.FromRgba([]));
    }
}
