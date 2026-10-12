using VuStudio.Rendering;
using VuStudio.Rendering.Text;

namespace VuStudio.Tests.Rendering;

public class AssetTests
{
    private readonly FaceRenderer _renderer = new();

    [Fact]
    public void Every_bundled_font_is_listed_with_weights()
    {
        Assert.Equal(14, _renderer.Fonts.Count);
        Assert.All(_renderer.Fonts, f => Assert.NotEmpty(f.Weights));
        var inter = Assert.Single(_renderer.Fonts, f => f.Id == "inter");
        Assert.Equal([100, 200, 300, 400, 500, 600, 700, 800, 900], inter.Weights);
        Assert.Equal([400, 500, 600, 700], _renderer.Fonts.Single(f => f.Id == "barlow-condensed").Weights);
        Assert.True(_renderer.Fonts.Single(f => f.Id == "libre-baskerville").HasItalic);
    }

    [Fact]
    public void Every_font_weight_and_style_resolves_to_a_typeface_with_glyphs()
    {
        var fonts = FaceAssets.Shared.Fonts;
        foreach (var info in _renderer.Fonts)
        {
            foreach (var weight in info.Weights)
            {
                foreach (var italic in new[] { false, true })
                {
                    var resolved = fonts.Resolve(info.Id, weight, italic, 14);
                    Assert.Equal(info.Id, resolved.Family.Id);
                    Assert.Equal(italic && !info.HasItalic, resolved.SyntheticItalic);
                    using var text = TextShaper.Shape(fonts, "Ag0", new TextStyle(info.Id, 14, weight, italic));
                    Assert.False(text.IsEmpty, $"{info.Id} {weight} {italic}");
                    Assert.True(text.Width > 0);
                }
            }
        }
    }

    [Fact]
    public void Variable_fonts_get_heavier_with_weight_and_static_fonts_use_the_nearest_file()
    {
        var fonts = FaceAssets.Shared.Fonts;
        var renderer = new FaceRenderer();
        int Ink(int weight)
        {
            var doc = new VuStudio.Core.Faces.FaceDocument();
            doc.Layers.Add(new VuStudio.Core.Faces.TextLayer { Text = "HOLD 88", Font = "inter", Size = 30, Weight = weight, X = 100, Y = 72 });
            return renderer.Render(doc, new VuStudio.Core.Faces.FaceRenderContext()).CountBlack();
        }

        Assert.True(Ink(800) > Ink(300) * 1.5);
        Assert.Same(fonts.Resolve("inter", 700, false, 14).Typeface, fonts.Resolve("inter", 700, false, 14).Typeface);
        Assert.Same(fonts.Resolve("barlow-condensed", 600, false, 14).Typeface, fonts.Resolve("barlow-condensed", 640, false, 14).Typeface);
        Assert.Same(fonts.Resolve("b612", 700, false, 14).Typeface, fonts.Resolve("b612", 900, false, 14).Typeface);
    }

    [Fact]
    public void Unknown_font_falls_back_to_inter()
    {
        Assert.Equal("inter", FaceAssets.Shared.Fonts.Resolve("no-such-font", 400, false, 12).Family.Id);
    }

    [Fact]
    public void Missing_glyphs_are_substituted_or_taken_from_inter()
    {
        var fonts = FaceAssets.Shared.Fonts;

        // Silkscreen has no U+2212 (minus): it is replaced by its own hyphen, so the width is the hyphen's.
        using var minus = TextShaper.Shape(fonts, "−5", new TextStyle("silkscreen", 8));
        using var hyphen = TextShaper.Shape(fonts, "-5", new TextStyle("silkscreen", 8));
        Assert.Equal(hyphen.Width, minus.Width, 3);

        // Orbitron has no µ: it comes from Inter instead of being dropped.
        using var micro = TextShaper.Shape(fonts, "µs", new TextStyle("orbitron", 12));
        using var s = TextShaper.Shape(fonts, "s", new TextStyle("orbitron", 12));
        Assert.True(micro.Width > s.Width + 3);
    }

    [Fact]
    public void Every_icon_resolves_to_a_path()
    {
        Assert.Equal(202, _renderer.Icons.Count);
        var icons = FaceAssets.Shared.Icons;
        Assert.All(_renderer.Icons, icon =>
        {
            var path = icons.GetPath(icon.Id);
            Assert.NotNull(path);
            Assert.False(path.TightBounds.IsEmpty);
            Assert.True(path.TightBounds.Right <= 24.01 && path.TightBounds.Bottom <= 24.01, icon.Id);
        });
        Assert.Null(icons.GetPath("definitely-not-an-icon"));
    }

    [Fact]
    public void Font_licenses_are_embedded_with_the_fonts()
    {
        var names = FaceAssets.ResourceNames.ToList();
        Assert.Contains("VuStudio.Rendering.Assets/Icons/LICENSE-MDI.txt", names);
        Assert.Equal(14, names.Count(n => n.EndsWith("/OFL.txt", StringComparison.Ordinal)));
    }
}
