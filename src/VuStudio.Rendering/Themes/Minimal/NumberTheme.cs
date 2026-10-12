using VuStudio.Core.Faces;
using VuStudio.Core.Providers;

namespace VuStudio.Rendering.Themes.Minimal;

/// <summary>
/// minimal/number — the value dominates: huge digits across the top half, a tiny title at the bottom and a
/// hairline scale for the needle.
/// </summary>
internal sealed class NumberTheme : IThemeGenerator
{
    private const double ArcRadius = 149;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "minimal/number",
        "Big number",
        "Numero grande",
        "Huge digits fill the top of the face; a hairline scale and a tiny title keep out of the way.",
        "Cifre enormi occupano la parte alta; una scala sottilissima e un titolo minuto restano in disparte.",
        ThemeKit.SelectOption(
            "font",
            "Digits",
            "Cifre",
            "inter",
            new SettingOption("inter", new LocalizedText("Inter")),
            new SettingOption("space-grotesk", new LocalizedText("Space Grotesk")),
            new SettingOption("bebas-neue", new LocalizedText("Bebas Neue")),
            new SettingOption("oswald", new LocalizedText("Oswald"))),
        ThemeKit.BoolOption("rangeLabels", "Min/max labels", "Etichette min/max", false));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var font = ThemeKit.Option(config, "font", "inter") switch
        {
            "space-grotesk" => "space-grotesk",
            "bebas-neue" => "bebas-neue",
            "oswald" => "oswald",
            _ => "inter",
        };
        var condensed = font is "bebas-neue" or "oswald";
        var sparkline = config.ShowSparkline;

        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = sparkline ? 47 : 58,
                Font = font,
                Size = sparkline ? (condensed ? 52 : 46) : (condensed ? 70 : 62),
                Weight = font switch { "bebas-neue" => 400, "oswald" => 600, _ => 700 },
                LetterSpacing = condensed ? 0 : -1.5,
                UnitFont = "inter",
                UnitSize = 14,
                UnitWeight = 600,
                Placement = UnitPlacement.Top,
                Gap = 3,
                Left = 4,
                Right = 196,
            }));
        }
        else if (ThemeKit.IconTemplate(config, context) is { } bigIcon)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = bigIcon, X = ThemeKit.CenterX, Y = 36, Size = 48 });
        }

        if (sparkline)
        {
            layers.Add(ThemeKit.Sparkline(44, 54, 112, 15, stroke: 1));
        }

        var ticks = new ScaleLayer
        {
            Name = "Scale",
            Radius = ArcRadius + 0.5,
            ShowArc = true,
            ArcRadius = ArcRadius,
            ArcWidth = 1,
            EvenSpacing = true,
            MajorCount = ThemeKit.LabelCount(config),
            MinorPerMajor = 0,
            MajorLength = 5,
            MajorWidth = 1,
            ShowLabels = false,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Band, ArcRadius - 0.5, ArcRadius + 2.5) is { } warn)
        {
            ticks.Zones.Add(warn);
        }

        layers.Add(ticks);

        if (ThemeKit.Option(config, "rangeLabels", false))
        {
            layers.Add(new ScaleLayer
            {
                Name = "Range",
                Radius = ArcRadius,
                EvenSpacing = true,
                MajorCount = 2,
                MinorPerMajor = 0,
                MajorLength = 0,
                MinorLength = 0,
                LabelRadius = ArcRadius - 13,
                LabelFont = "inter",
                LabelSize = 9,
                LabelWeight = 500,
            });
        }

        // Tiny title (and icon) under the scale.
        var subtitle = ThemeKit.SubtitleTemplate(config, context);
        var titleTemplate = subtitle is null ? ThemeKit.TitleTemplate(config) : ThemeKit.TitleTemplate(config) + " · " + subtitle;
        var title = TextTemplate.Render(titleTemplate, context).ToUpper(context.Culture);
        var icon = config.ShowValue ? ThemeKit.IconTemplate(config, context) : null;
        var titleWidth = Math.Min(150, ThemeKit.Measure(title, "inter", 9, 600, letterSpacing: 1.5, smooth: false));
        var total = icon is null ? titleWidth : 16 + 4 + titleWidth;
        var left = Math.Round(ThemeKit.CenterX - (total / 2));
        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = left + 8, Y = 131, Size = 16 });
            left += 20;
        }

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = titleTemplate,
            X = icon is null ? ThemeKit.CenterX : left,
            Y = 131,
            Align = icon is null ? TextAlign.Center : TextAlign.Left,
            Baseline = TextBaseline.Middle,
            Font = "inter",
            Size = 9,
            Weight = 600,
            Uppercase = true,
            LetterSpacing = 1.5,
            MaxWidth = 150,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        return doc;
    }
}
