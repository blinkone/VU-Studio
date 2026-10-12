using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Retro;

/// <summary>
/// retro/pixel — an 8-bit handheld look: VT323 digits at their native 25 px grid (×2), Silkscreen captions
/// at 8 px, a pixel border, square dot ticks and a checkered warning zone.
/// </summary>
internal sealed class PixelTheme : IThemeGenerator
{
    private const double Radius = 149;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "retro/pixel",
        "Pixel",
        "Pixel",
        "An 8-bit handheld look with pixel fonts at their native sizes and a pixel border.",
        "Un look da console portatile a 8 bit, con font pixel alle dimensioni native e bordo pixel.",
        ThemeKit.BoolOption("border", "Pixel border", "Bordo pixel", true));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var majors = ThemeKit.LabelCount(config);

        if (ThemeKit.Option(config, "border", true))
        {
            // A 1 px border with notched corners, like old handheld screens.
            layers.Add(ThemeKit.Line(3, 0, 196, 0, name: "Border"));
            layers.Add(ThemeKit.Line(3, 143, 196, 143, name: "Border"));
            layers.Add(ThemeKit.Line(0, 3, 0, 140, name: "Border"));
            layers.Add(ThemeKit.Line(199, 3, 199, 140, name: "Border"));
            foreach (var (x, y) in new[] { (1.0, 1.0), (197.0, 1.0), (1.0, 141.0), (197.0, 141.0) })
            {
                layers.Add(ThemeKit.Rect(x, y, 2, 2, stroke: 0, fill: true, name: "Corner"));
            }
        }

        var icon = ThemeKit.IconTemplate(config, context);
        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = icon is null ? ThemeKit.CenterX : 26,
            Y = 7,
            Align = icon is null ? TextAlign.Center : TextAlign.Left,
            Baseline = TextBaseline.Top,
            Font = "silkscreen",
            Size = 8,
            Weight = 400,
            Uppercase = true,
            LetterSpacing = 1,
            MaxWidth = icon is null ? 180 : 160,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });
        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 14, Y = 11, Size = 16 });
        }

        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = config.ShowSparkline ? 46 : 56,
                Font = "vt323",
                Size = 50,
                Weight = 400,
                Smooth = false,
                UnitFont = "silkscreen",
                UnitSize = 8,
                UnitWeight = 400,
                UnitUppercase = true,
                Placement = UnitPlacement.Top,
                Gap = 3,
                Left = 6,
                Right = 194,
            }));
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(52, 52, 96, 14, SparklineStyle.Bars));
        }

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            var sub = ThemeKit.Subtitle(subtitle, ThemeKit.CenterX, 138, "silkscreen", 8, 400, maxWidth: 100);
            sub.Uppercase = true;
            layers.Add(sub);
        }

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = Radius,
            ShowArc = true,
            ArcRadius = Radius - 7,
            ArcWidth = 1,
            TickStyle = TickStyle.Dot,
            MajorCount = majors,
            MinorPerMajor = ThemeKit.MinorCount(context, majors, dense: false),
            MajorWidth = 4,
            MinorWidth = 2,
            LabelRadius = 132,
            LabelFont = "silkscreen",
            LabelSize = 8,
            LabelWeight = 400,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Dotted, Radius - 6, Radius) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);
        return doc;
    }
}
