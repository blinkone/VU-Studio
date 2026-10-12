using System.Collections.Concurrent;
using System.Diagnostics;
using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;
using VuStudio.Core.Sensors;
using VuStudio.Rendering;
using VuStudio.Rendering.Drawing;

namespace VuStudio.Tests.Rendering;

public class FaceRendererTests
{
    private readonly FaceRenderer _renderer = new();

    private static FaceDocument Doc(params FaceLayer[] layers)
    {
        var doc = new FaceDocument();
        doc.Layers.AddRange(layers);
        return doc;
    }

    [Fact]
    public void Empty_document_renders_white_200_by_144()
    {
        var bitmap = _renderer.Render(new FaceDocument(), new FaceRenderContext());
        Assert.Equal(200, bitmap.Width);
        Assert.Equal(144, bitmap.Height);
        Assert.Equal(0, bitmap.CountBlack());
    }

    [Fact]
    public void Scale_ticks_follow_the_needle_geometry()
    {
        var context = RenderingTestData.Context(min: 0, max: 100);
        var bitmap = _renderer.Render(Doc(new ScaleLayer { ShowLabels = false }), context);
        var g = context.Geometry;

        // Majors (0/25/50/75/100) span radius 138–152; minors only 146–152.
        foreach (var f in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            var (x, y) = g.PointAt(f, 145);
            Assert.True(RenderingTestData.BlackNear(bitmap, x, y), $"major at {f}");
            (x, y) = g.PointAt(f, 140);
            Assert.True(RenderingTestData.BlackNear(bitmap, x, y), $"major root at {f}");
        }

        var (cx, cy) = g.PointAt(0.5, 145);
        Assert.False(bitmap[(int)cx, (int)cy]);
        Assert.InRange(cy, 79, 82);

        // Between minors (0.125 lies between the 0.10 and 0.15 minors) and below the minors (r 141) is paper.
        var (mx, my) = g.PointAt(0.125, 149);
        Assert.False(RenderingTestData.BlackNear(bitmap, mx, my, 0));
        (mx, my) = g.PointAt(0.10, 141);
        Assert.False(RenderingTestData.BlackNear(bitmap, mx, my, 0));
        (mx, my) = g.PointAt(0.10, 149);
        Assert.True(RenderingTestData.BlackNear(bitmap, mx, my, 1));
    }

    [Fact]
    public void Scale_majors_use_nice_values_even_spacing_or_custom_labels()
    {
        var context = RenderingTestData.Context(min: 0, max: 6000);
        var nice = ScaleDrawer.Majors(new ScaleLayer { MajorCount = 4 }, context);
        Assert.Equal(["0", "2000", "4000", "6000"], nice.Select(m => m.Label));
        Assert.Equal([0, 1 / 3.0, 2 / 3.0, 1], nice.Select(m => Math.Round(m.Fraction, 6)), new ToleranceComparer());

        var divided = ScaleDrawer.Majors(new ScaleLayer { MajorCount = 4, LabelDivisor = 1000 }, context);
        Assert.Equal(["0", "2", "4", "6"], divided.Select(m => m.Label));

        var even = ScaleDrawer.Majors(new ScaleLayer { MajorCount = 3, EvenSpacing = true }, RenderingTestData.Context(min: 0, max: 5));
        Assert.Equal(["0", "2.5", "5"], even.Select(m => m.Label));

        var custom = ScaleDrawer.Majors(new ScaleLayer { CustomLabels = ["LOW", "MID", "HIGH"] }, context);
        Assert.Equal([0, 0.5, 1], custom.Select(m => m.Fraction));
        Assert.Equal("MID", custom[1].Label);

        var formatted = ScaleDrawer.Majors(new ScaleLayer { MajorCount = 3, EvenSpacing = true, LabelFormat = "0.0" }, RenderingTestData.Context(min: 0, max: 1));
        Assert.Equal(["0.0", "0.5", "1.0"], formatted.Select(m => m.Label));
    }

    [Fact]
    public void Minor_ticks_are_evenly_spaced_in_fraction_space()
    {
        var majors = new List<ScaleMark> { new(0.2, "a"), new(0.6, "b"), new(1, "c") };
        var minors = ScaleDrawer.Minors(majors, 3);
        Assert.Equal([0.3, 0.4, 0.5, 0.7, 0.8, 0.9, 0.1, 0.0], minors.Select(m => Math.Round(m, 6)));
    }

    [Fact]
    public void Colliding_labels_are_thinned_evenly_keeping_both_ends()
    {
        // Each label collides with its direct neighbours only.
        var picks = ScaleDrawer.SelectLabels(9, (a, b) => Math.Abs(a - b) < 2);
        Assert.Equal([0, 2, 4, 6, 8], picks);

        var odd = ScaleDrawer.SelectLabels(8, (a, b) => Math.Abs(a - b) < 2);
        Assert.Equal(0, odd[0]);
        Assert.Equal(7, odd[^1]);
        Assert.True(odd.Zip(odd.Skip(1)).All(p => p.Second - p.First >= 2));

        Assert.Equal([0, 1, 2], ScaleDrawer.SelectLabels(3, (_, _) => false));
    }

    [Fact]
    public void Scale_labels_stay_inside_the_canvas()
    {
        var context = RenderingTestData.Context(min: 0, max: 20000);
        var bitmap = _renderer.Render(Doc(new ScaleLayer { MajorLength = 0, MinorLength = 0, LabelRadius = 150, LabelSize = 16 }), context);
        Assert.True(bitmap.CountBlack() > 0);
        Assert.Equal(0, RenderingTestData.CountBlack(bitmap, 0, 0, 1, 143));
        Assert.Equal(0, RenderingTestData.CountBlack(bitmap, 198, 0, 199, 143));
    }

    [Fact]
    public void Zones_of_every_style_draw_in_their_sector()
    {
        var context = RenderingTestData.Context();
        foreach (var style in Enum.GetValues<ZoneStyle>())
        {
            var bitmap = _renderer.Render(Doc(new ScaleLayer { MajorLength = 0, MinorLength = 0, ShowLabels = false, Zones = [new ScaleZone { From = 0.6, To = 0.9, Style = style, InnerRadius = 130, OuterRadius = 150 }] }), context);
            var (x, y) = context.Geometry.PointAt(0.75, 140);
            var near = RenderingTestData.CountBlack(bitmap, (int)x - 4, (int)y - 4, (int)x + 4, (int)y + 4);
            var (ox, oy) = context.Geometry.PointAt(0.3, 140);
            Assert.Equal(0, RenderingTestData.CountBlack(bitmap, (int)ox - 4, (int)oy - 4, (int)ox + 4, (int)oy + 4));
            if (style == ZoneStyle.Band)
            {
                Assert.Equal(81, near);
            }
            else if (style != ZoneStyle.Outline)
            {
                Assert.InRange(near, 3, 70);
            }
        }
    }

    [Fact]
    public void Template_text_follows_the_value()
    {
        var doc = Doc(new TextLayer { Text = "{value} {unit}", X = 100, Y = 72, Size = 24 });
        var a = _renderer.Render(doc, RenderingTestData.Context(value: 12));
        var b = _renderer.Render(doc, RenderingTestData.Context(value: 98));
        var a2 = _renderer.Render(doc, RenderingTestData.Context(value: 12));
        Assert.NotEqual(a, b);
        Assert.Equal(a, a2);
        Assert.True(a.CountBlack() > 50);
    }

    [Fact]
    public void Text_alignment_and_baselines_use_the_anchor()
    {
        var context = new FaceRenderContext();
        var left = _renderer.Render(Doc(new TextLayer { Text = "TEST", X = 40, Y = 50, Align = TextAlign.Left }), context);
        var right = _renderer.Render(Doc(new TextLayer { Text = "TEST", X = 160, Y = 50, Align = TextAlign.Right }), context);
        var center = _renderer.Render(Doc(new TextLayer { Text = "TEST", X = 100, Y = 50, Align = TextAlign.Center }), context);
        Assert.InRange(RenderingTestData.InkExtent(left)!.Value.Left, 40, 43);
        Assert.InRange(RenderingTestData.InkExtent(right)!.Value.Right, 156, 160);
        var (cl, cr) = RenderingTestData.InkExtent(center)!.Value;
        Assert.InRange((cl + cr) / 2.0, 98, 102);

        // Top: caps start at Y; Bottom: nothing below Y.
        var top = _renderer.Render(Doc(new TextLayer { Text = "HHH", X = 100, Y = 40, Baseline = TextBaseline.Top, Size = 20 }), context);
        Assert.Equal(0, RenderingTestData.CountBlack(top, 0, 0, 199, 38));
        Assert.True(RenderingTestData.CountBlack(top, 0, 40, 199, 44) > 0);
        var bottom = _renderer.Render(Doc(new TextLayer { Text = "HHH", X = 100, Y = 40, Baseline = TextBaseline.Bottom, Size = 20 }), context);
        Assert.Equal(0, RenderingTestData.CountBlack(bottom, 0, 41, 199, 143));
    }

    [Theory]
    [InlineData(TextOverflow.Shrink)]
    [InlineData(TextOverflow.Ellipsis)]
    [InlineData(TextOverflow.Clip)]
    public void Overflowing_text_is_kept_within_max_width(TextOverflow overflow)
    {
        var doc = Doc(new TextLayer { Text = "A WAY TOO LONG TITLE FOR THIS", X = 100, Y = 72, Size = 18, MaxWidth = 80, Overflow = overflow });
        var (l, r) = RenderingTestData.InkExtent(_renderer.Render(doc, new FaceRenderContext()))!.Value;
        Assert.InRange(l, 58, 100);
        Assert.InRange(r, 100, 141);

        var visible = Doc(new TextLayer { Text = "A WAY TOO LONG TITLE FOR THIS", X = 100, Y = 72, Size = 18, MaxWidth = 80, Overflow = TextOverflow.Visible });
        var (vl, vr) = RenderingTestData.InkExtent(_renderer.Render(visible, new FaceRenderContext()))!.Value;
        Assert.True(vr - vl > 120);
    }

    [Fact]
    public void Uppercase_letter_spacing_and_rotation_change_the_text()
    {
        var context = RenderingTestData.Context(title: "cpu");
        var plain = _renderer.Render(Doc(new TextLayer { Text = "{title}" }), context);
        var upper = _renderer.Render(Doc(new TextLayer { Text = "{title}", Uppercase = true }), context);
        var spaced = _renderer.Render(Doc(new TextLayer { Text = "{title}", LetterSpacing = 4 }), context);
        var rotated = _renderer.Render(Doc(new TextLayer { Text = "{title}", Rotation = 90 }), context);
        Assert.NotEqual(plain, upper);
        var (pl, pr) = RenderingTestData.InkExtent(plain)!.Value;
        var (sl, sr) = RenderingTestData.InkExtent(spaced)!.Value;
        Assert.True(sr - sl >= pr - pl + 7);
        var (rl, rr) = RenderingTestData.InkExtent(rotated)!.Value;
        Assert.True(rr - rl < (pr - pl) / 2);
    }

    [Fact]
    public void Inverted_face_is_the_exact_negative()
    {
        var context = RenderingTestData.Context();
        foreach (var theme in _renderer.Themes)
        {
            var doc = _renderer.BuildDocument(new FaceConfig { Theme = theme.Id, ShowSparkline = true }, context);
            var normal = _renderer.Render(doc, context);
            doc.Inverted = true;
            var inverted = _renderer.Render(doc, context);
            normal.Invert();
            Assert.Equal(normal, inverted);
        }
    }

    [Fact]
    public void Background_ink_knocks_out()
    {
        var doc = Doc(
            new ShapeLayer { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 100, Height = 40, Fill = true, Stroke = false },
            new ShapeLayer { Kind = ShapeKind.Rectangle, X = 40, Y = 30, Width = 20, Height = 20, Fill = true, Stroke = false, Ink = Ink.Background });
        var bitmap = _renderer.Render(doc, new FaceRenderContext());
        Assert.Equal((100 * 40) - (20 * 20), bitmap.CountBlack());
        Assert.True(bitmap[50, 40]);
        Assert.False(bitmap[30, 40]);
    }

    [Fact]
    public void Shapes_are_pixel_crisp()
    {
        var context = new FaceRenderContext();
        var outline = _renderer.Render(Doc(new ShapeLayer { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 50, Height = 30, StrokeWidth = 1 }), context);
        Assert.Equal((2 * 51) + (2 * 29), outline.CountBlack());

        var line = _renderer.Render(Doc(new ShapeLayer { Kind = ShapeKind.Line, X = 10, Y = 70.3, X2 = 110, Y2 = 70.3, StrokeWidth = 2 }), context);
        Assert.Equal(2, RenderingTestData.CountBlack(line, 50, 0, 50, 143));

        var dashed = _renderer.Render(Doc(new ShapeLayer { Kind = ShapeKind.Line, X = 10, Y = 70, X2 = 110, Y2 = 70, StrokeWidth = 1, Dashed = true }), context);
        Assert.InRange(dashed.CountBlack(), 40, 60);

        var ellipse = _renderer.Render(Doc(new ShapeLayer { Kind = ShapeKind.Ellipse, X = 60, Y = 40, Width = 80, Height = 60, Fill = true, Stroke = false }), context);
        Assert.InRange(ellipse.CountBlack(), (int)(Math.PI * 40 * 30 * 0.95), (int)(Math.PI * 40 * 30 * 1.05));

        var arc = _renderer.Render(Doc(new ShapeLayer { Kind = ShapeKind.Arc, X = 50, Y = 20, Width = 100, Height = 100, StartAngle = 180, SweepAngle = 90, StrokeWidth = 2 }), context);
        Assert.True(arc[51, 70] || arc[50, 70] || arc[52, 70]);
        Assert.Equal(0, RenderingTestData.CountBlack(arc, 101, 0, 199, 143));
    }

    [Fact]
    public void Icons_resolve_templates_and_skip_unknown_ids()
    {
        var context = RenderingTestData.Context(icon: "fan");
        var icon = _renderer.Render(Doc(new IconLayer { Icon = "{icon}", X = 100, Y = 72, Size = 48 }), context);
        var direct = _renderer.Render(Doc(new IconLayer { Icon = "fan", X = 100, Y = 72, Size = 48 }), context);
        Assert.Equal(direct, icon);
        Assert.True(icon.CountBlack() > 300);
        var (l, r) = RenderingTestData.InkExtent(icon)!.Value;
        Assert.InRange(l, 76, 84);
        Assert.InRange(r, 116, 124);

        Assert.Equal(0, _renderer.Render(Doc(new IconLayer { Icon = "no-such-icon" }), context).CountBlack());
        Assert.Equal(0, _renderer.Render(Doc(new IconLayer { Icon = "{icon}" }), RenderingTestData.Context(icon: null)).CountBlack());
    }

    [Fact]
    public void Bar_uses_sensor_or_page_value_and_range()
    {
        var snapshot = new SensorSnapshot(new SensorInfo { Id = "x/load", Name = "Load", Min = 0, Max = 200 }, 50, null, RenderingTestData.Now, 1, false);
        var context = new FaceRenderContext { Value = 75, Mapping = new ValueMapping(0, 100), Sensors = id => id == "x/load" ? snapshot : null };
        Assert.Equal(0.75, BarDrawer.Fraction(context, new BarLayer()));
        Assert.Equal(0.25, BarDrawer.Fraction(context, new BarLayer { SensorId = "x/load" }));
        Assert.Equal(0.5, BarDrawer.Fraction(context, new BarLayer { SensorId = "x/load", Max = 100 }));
        Assert.Equal(0.5, BarDrawer.Fraction(context, new BarLayer { Min = 50, Max = 100 }));
        Assert.Null(BarDrawer.Fraction(context, new BarLayer { SensorId = "x/missing" }));

        var solid = _renderer.Render(Doc(new BarLayer { X = 0, Y = 100, Width = 100, Height = 10, Border = false }), context);
        Assert.Equal(75 * 10, solid.CountBlack());

        var vertical = _renderer.Render(Doc(new BarLayer { X = 10, Y = 10, Width = 10, Height = 100, Border = false, Orientation = Orientation.Vertical }), context);
        Assert.False(vertical[15, 105]);
        Assert.True(vertical[15, 15]);

        var segmented = _renderer.Render(Doc(new BarLayer { X = 0, Y = 100, Width = 100, Height = 10, Border = true, Segments = 8, Gap = 2 }), context);
        Assert.True(segmented.CountBlack() > 200);
    }

    [Theory]
    [InlineData(SparklineStyle.Line)]
    [InlineData(SparklineStyle.Area)]
    [InlineData(SparklineStyle.Bars)]
    public void Sparkline_draws_history_and_nothing_without_it(SparklineStyle style)
    {
        var layer = new SparklineLayer { X = 20, Y = 20, Width = 160, Height = 40, Style = style, Border = true };
        var drawn = _renderer.Render(Doc(layer), RenderingTestData.Context());
        Assert.True(drawn.CountBlack() > 200);
        Assert.Equal(0, RenderingTestData.CountBlack(drawn, 0, 61, 199, 143));

        var single = RenderingTestData.Context() is var c ? new FaceRenderContext { SensorId = c.SensorId, History = (_, _) => [new HistoryPoint(RenderingTestData.Now, 1)] } : null;
        Assert.Equal(0, _renderer.Render(Doc(layer), single!).CountBlack());
        Assert.Equal(0, _renderer.Render(Doc(layer), new FaceRenderContext { History = (_, _) => RenderingTestData.History() }).CountBlack());
    }

    [Fact]
    public void Broken_layers_are_reported_and_skipped()
    {
        var failed = new List<FaceLayer>();
        _renderer.LayerFailed += (layer, _) => failed.Add(layer);
        var context = new FaceRenderContext { History = (_, _) => throw new InvalidOperationException("history unavailable"), SensorId = "x" };
        var bitmap = _renderer.Render(Doc(new SparklineLayer(), new ShapeLayer { Fill = true }), context);
        Assert.Single(failed);
        Assert.True(bitmap.CountBlack() > 0);
    }

    [Fact]
    public void Encode_png_upscales_and_tints()
    {
        var bitmap = new MonoBitmap();
        bitmap[0, 0] = false;
        var png = _renderer.EncodePng(bitmap, 3, new RgbwColor(255, 120, 0));
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png[..8]);

        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(600, decoded.Width);
        Assert.Equal(432, decoded.Height);
        Assert.Equal(new SKColor(0x14, 0x14, 0x14), decoded.GetPixel(2, 2));
        Assert.Equal(new SKColor(255, 120, 0), decoded.GetPixel(3, 0));
        Assert.Equal(new SKColor(255, 120, 0), decoded.GetPixel(599, 431));

        using var plain = SKBitmap.Decode(_renderer.EncodePng(bitmap));
        Assert.Equal(200, plain.Width);
        Assert.Equal(SKColors.White, plain.GetPixel(1, 0));
    }

    [Fact]
    public void Rendering_is_thread_safe()
    {
        var jobs = _renderer.Themes
            .SelectMany(t => RenderingTestData.Variety().Select(c => (Config: new FaceConfig { Theme = t.Id, ShowSparkline = true }, Context: c)))
            .Select(j => (j.Context, Doc: _renderer.BuildDocument(j.Config, j.Context)))
            .ToList();
        var expected = jobs.Select(j => new FaceRenderer().Render(j.Doc, j.Context)).ToList();

        var renderer = new FaceRenderer();
        var mismatches = new ConcurrentBag<int>();
        Parallel.For(0, jobs.Count * 6, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var job = jobs[i % jobs.Count];
            if (!renderer.Render(job.Doc, job.Context).Equals(expected[i % jobs.Count]))
            {
                mismatches.Add(i);
            }
        });
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Rendering_is_fast()
    {
        var context = RenderingTestData.Context();
        var docs = _renderer.Themes.Select(t => _renderer.BuildDocument(new FaceConfig { Theme = t.Id, ShowSparkline = true }, context)).ToList();
        foreach (var doc in docs)
        {
            _renderer.Render(doc, context);
        }

        var sw = Stopwatch.StartNew();
        const int rounds = 10;
        for (var i = 0; i < rounds; i++)
        {
            foreach (var doc in docs)
            {
                _renderer.Render(doc, context);
            }
        }

        // Typically ~0.5 ms per face; the bound is generous for slow CI machines.
        Assert.True(sw.Elapsed.TotalMilliseconds / (rounds * docs.Count) < 15, $"{sw.Elapsed.TotalMilliseconds / (rounds * docs.Count):F2} ms per face");
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;

        public int GetHashCode(double obj) => 0;
    }
}
