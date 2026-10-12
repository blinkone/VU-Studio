using System.Text.Json;
using System.Text.Json.Nodes;
using VuStudio.Core.Config;
using VuStudio.Core.Faces;
using VuStudio.Rendering;
using VuStudio.Rendering.Themes;

namespace VuStudio.Tests.Rendering;

public class ThemeTests
{
    private static readonly string[] RequiredThemes =
    [
        "vintage/classic", "vintage/vu", "minimal/arc", "minimal/number",
        "instrument/gauge", "instrument/aviation", "hud/frame", "hud/grid",
    ];

    private readonly FaceRenderer _renderer = new();

    public static TheoryData<string> ThemeIds() => [.. BuiltInThemes.All.Select(t => t.Info.Id)];

    [Fact]
    public void Built_in_themes_cover_the_required_set_with_unique_localized_ids()
    {
        var ids = _renderer.Themes.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Superset(RequiredThemes.ToHashSet(), ids.ToHashSet());
        Assert.All(_renderer.Themes, t =>
        {
            Assert.Matches("^[a-z]+/[a-z]+$", t.Id);
            Assert.Equal(t.Id.Split('/')[0], t.Family);
            Assert.False(string.IsNullOrWhiteSpace(t.Name.En));
            Assert.False(string.IsNullOrWhiteSpace(t.Name.It));
            Assert.False(string.IsNullOrWhiteSpace(t.Description?.It));
            Assert.All(t.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Label.It)));
        });
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Theme_renders_every_variety_without_errors_and_with_real_ink(string themeId)
    {
        var failures = new List<string>();
        _renderer.LayerFailed += (layer, ex) => failures.Add($"{layer.Name}: {ex.Message}");
        foreach (var context in RenderingTestData.Variety())
        {
            foreach (var config in Configs(themeId))
            {
                var doc = _renderer.BuildDocument(config, context);
                Assert.Equal(config.Inverted, doc.Inverted);
                var bitmap = _renderer.Render(doc, context);
                var ink = config.Inverted ? (200 * 144) - bitmap.CountBlack() : bitmap.CountBlack();
                Assert.InRange(ink, 250, 200 * 144 / 3);
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Generated_text_uses_templates_so_values_update_live(string themeId)
    {
        var config = new FaceConfig { Theme = themeId };
        var doc = _renderer.BuildDocument(config, RenderingTestData.Context());
        var texts = doc.Layers.OfType<TextLayer>().ToList();
        Assert.Contains(texts, t => t.Text.Contains("{title}", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Text.Contains("{value}", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Text.Contains("63.4", StringComparison.Ordinal) || t.Text == "CPU");

        // Same document, new value: the face changes.
        var a = _renderer.Render(doc, RenderingTestData.Context(value: 12.5));
        var b = _renderer.Render(doc, RenderingTestData.Context(value: 87.5));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Common_fields_are_honoured(string themeId)
    {
        var context = RenderingTestData.Context();
        var plain = _renderer.BuildDocument(new FaceConfig { Theme = themeId, WarnFrom = null, ShowValue = false, ShowSparkline = false, ShowIcon = false }, context);
        Assert.DoesNotContain(plain.Layers.OfType<TextLayer>(), t => t.Text.Contains("{value}", StringComparison.Ordinal));
        Assert.Empty(plain.Layers.OfType<SparklineLayer>());
        Assert.Empty(plain.Layers.OfType<IconLayer>());
        Assert.DoesNotContain(plain.Layers.OfType<ScaleLayer>().SelectMany(s => s.Zones), z => z.Style != ZoneStyle.Dotted || z.To - z.From < 1);

        var full = _renderer.BuildDocument(new FaceConfig { Theme = themeId, WarnFrom = 0.8, ShowSparkline = true, Title = "Custom {unit}", Subtitle = "Sub" }, context);
        Assert.Single(full.Layers.OfType<SparklineLayer>());
        Assert.Contains(full.Layers.OfType<IconLayer>(), i => i.Icon == "{icon}");
        Assert.Contains(full.Layers.OfType<ScaleLayer>().SelectMany(s => s.Zones), z => Math.Abs(z.To - 1) < 1e-9 || z.From >= 0.7);
        Assert.Contains(full.Layers.OfType<TextLayer>(), t => t.Text.Contains("Custom {unit}", StringComparison.Ordinal));
        Assert.Contains(full.Layers.OfType<TextLayer>(), t => t.Text.Contains("Sub", StringComparison.Ordinal));
    }

    [Fact]
    public void Label_count_sets_the_number_of_majors()
    {
        var doc = _renderer.BuildDocument(new FaceConfig { Theme = "instrument/gauge", LabelCount = 7 }, RenderingTestData.Context());
        Assert.Equal(7, doc.Layers.OfType<ScaleLayer>().Single().MajorCount);
    }

    [Fact]
    public void Build_document_prefers_custom_and_falls_back_to_minimal_arc()
    {
        var custom = new FaceDocument();
        Assert.Same(custom, _renderer.BuildDocument(new FaceConfig { Theme = "vintage/vu", Custom = custom }, RenderingTestData.Context()));

        var context = RenderingTestData.Context();
        var unknown = _renderer.BuildDocument(new FaceConfig { Theme = "nope/nothing" }, context);
        var arc = _renderer.BuildDocument(new FaceConfig { Theme = "minimal/arc" }, context);
        Assert.Equal(_renderer.Render(arc, context), _renderer.Render(unknown, context));
    }

    [Fact]
    public void Generated_documents_round_trip_through_the_config_json()
    {
        foreach (var theme in _renderer.Themes)
        {
            var context = RenderingTestData.Context();
            var doc = _renderer.BuildDocument(new FaceConfig { Theme = theme.Id, ShowSparkline = true }, context);
            var copy = JsonSerializer.Deserialize<FaceDocument>(JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;
            Assert.Equal(_renderer.Render(doc, context), _renderer.Render(copy, context));
        }
    }

    [Fact]
    public void Theme_options_are_read_from_the_config()
    {
        var context = RenderingTestData.Context("Fan", 1840, "RPM", 0, 3000);
        var withMultiplier = _renderer.BuildDocument(new FaceConfig { Theme = "instrument/gauge" }, context);
        Assert.Equal(1000, withMultiplier.Layers.OfType<ScaleLayer>().Single().LabelDivisor);
        Assert.Contains(withMultiplier.Layers.OfType<TextLayer>(), t => t.Text.Contains("×1000", StringComparison.Ordinal));

        var without = _renderer.BuildDocument(new FaceConfig { Theme = "instrument/gauge", Options = new JsonObject { ["multiplier"] = false } }, context);
        Assert.Equal(1, without.Layers.OfType<ScaleLayer>().Single().LabelDivisor);

        var legend = _renderer.BuildDocument(new FaceConfig { Theme = "vintage/vu", Options = new JsonObject { ["legend"] = "dB" } }, context);
        Assert.Contains(legend.Layers.OfType<TextLayer>(), t => t.Text == "dB");
    }

    [Fact]
    public void Vu_marks_sit_at_amplitude_fractions()
    {
        Assert.Equal(1, VuStudio.Rendering.Themes.Vintage.VuMeterTheme.Fraction(3), 9);
        Assert.Equal(0.7079, VuStudio.Rendering.Themes.Vintage.VuMeterTheme.Fraction(0), 4);
        Assert.Equal(0.0708, VuStudio.Rendering.Themes.Vintage.VuMeterTheme.Fraction(-20), 4);

        var context = RenderingTestData.Context("Audio", 50, "", 0, 100);
        var doc = _renderer.BuildDocument(new FaceConfig { Theme = "vintage/vu" }, context);
        var bitmap = _renderer.Render(doc, context);
        var geometry = NeedleGeometry.Default;
        foreach (var db in new[] { -20.0, -10, -7, -3, 0, 3 })
        {
            var (x, y) = geometry.PointAt(VuStudio.Rendering.Themes.Vintage.VuMeterTheme.Fraction(db), 142);
            Assert.True(RenderingTestData.BlackNear(bitmap, x, y), $"{db} dB tick missing");
        }
    }

    private static IEnumerable<FaceConfig> Configs(string themeId)
    {
        yield return new FaceConfig { Theme = themeId };
        yield return new FaceConfig { Theme = themeId, Inverted = true, ShowSparkline = true, WarnFrom = 0.6, LabelCount = 9 };
        yield return new FaceConfig { Theme = themeId, ShowValue = false, ShowIcon = false, WarnFrom = null, Subtitle = "Subtitle" };
    }
}
