using VuStudio.Core.Faces;
using VuStudio.Core.Providers;

namespace VuStudio.Rendering.Themes.Vintage;

/// <summary>
/// vintage/classic — a 1970s hi-fi meter: a fine arc with many minor ticks, condensed labels, a bold "red
/// zone" band, the title in Libre Baskerville italic with a small unit caption, and a discreet value.
/// </summary>
internal sealed class ClassicTheme : IThemeGenerator
{
    private const double ArcRadius = 148;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "vintage/classic",
        "Classic meter",
        "Strumento classico",
        "A 1970s hi-fi meter: fine arc, dense ticks, condensed numerals and an italic serif title.",
        "Uno strumento hi-fi anni '70: arco sottile, tacche fitte, numeri condensati e titolo in corsivo.",
        ThemeKit.SelectOption(
            "labelFont",
            "Numeral font",
            "Font dei numeri",
            "barlow-condensed",
            new SettingOption("barlow-condensed", new LocalizedText("Barlow Condensed")),
            new SettingOption("oswald", new LocalizedText("Oswald"))));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var labelFont = ThemeKit.Option(config, "labelFont", "barlow-condensed") == "oswald" ? "oswald" : "barlow-condensed";
        var majors = ThemeKit.LabelCount(config);

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = ArcRadius,
            ShowArc = true,
            ArcRadius = ArcRadius,
            ArcWidth = 1,
            MajorCount = majors,
            MinorPerMajor = ThemeKit.MinorCount(context, majors, dense: false),
            MajorLength = 10,
            MajorWidth = 2,
            MinorLength = 5,
            MinorWidth = 1,
            LabelRadius = 129,
            LabelFont = labelFont,
            LabelSize = labelFont == "oswald" ? 12 : 13,
            LabelWeight = labelFont == "oswald" ? 500 : 600,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Band, ArcRadius - 0.5, ArcRadius + 4.5) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);

        // The dome above the arc holds the identity: (icon,) italic title, unit caption, (history).
        var icon = ThemeKit.IconTemplate(config, context);
        var subtitle = ThemeKit.SubtitleTemplate(config, context);

        // The caption under the title shows the unit, or the subtitle (the unit then joins the value).
        var caption = subtitle ?? (ThemeKit.Unit(context).Length > 0 ? "{unit}" : null);
        var hasUnit = caption is not null;
        var (titleSize, titleOverflow) = ThemeKit.Fit(ThemeKit.ResolvedTitle(config, context), "libre-baskerville", 22, 400, 150, 15, italic: true);
        var titleCap = ThemeKit.CapHeight("libre-baskerville", titleSize, 400);
        var stack = (icon is null ? 0 : 18 + 6) + titleCap + (hasUnit ? 7 + 8 : 0);
        var top = Math.Round(5 + ((66 - stack) / 2));

        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = ThemeKit.CenterX, Y = top + 9, Size = 18 });
            top += 18 + 6;
        }

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = ThemeKit.CenterX,
            Y = top + titleCap,
            Baseline = TextBaseline.Baseline,
            Font = "libre-baskerville",
            Italic = true,
            Size = titleSize,
            Weight = 400,
            MaxWidth = 150,
            Overflow = titleOverflow,
        });

        if (hasUnit)
        {
            layers.Add(new TextLayer
            {
                Name = subtitle is null ? "Unit" : "Subtitle",
                Text = caption!,
                X = ThemeKit.CenterX,
                Y = top + titleCap + 7,
                Baseline = TextBaseline.Top,
                Font = "barlow-condensed",
                Size = 11,
                Weight = 500,
                LetterSpacing = 0.6,
                MaxWidth = 90,
                Overflow = TextOverflow.Ellipsis,
                Smooth = false,
            });
        }

        if (config.ShowSparkline)
        {
            // A little chart recorder under the scale.
            layers.Add(ThemeKit.Sparkline(66, 107, 68, 14, stroke: 1));
        }

        // A discreet readout under the scale, where meters print their legend.
        if (config.ShowValue)
        {
            layers.Add(new TextLayer
            {
                Name = "Value",
                Text = subtitle is null ? "{value}" : "{value} {unit}",
                X = ThemeKit.CenterX,
                Y = 137,
                Baseline = TextBaseline.Baseline,
                Font = labelFont,
                Size = 15,
                Weight = labelFont == "oswald" ? 500 : 600,
                LetterSpacing = 0.5,
                MaxWidth = 80,
                Overflow = TextOverflow.Shrink,
                Smooth = false,
            });
        }

        return doc;
    }
}
