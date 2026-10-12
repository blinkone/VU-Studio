using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Hud;

/// <summary>
/// hud/frame — a sci-fi readout: a chamfered frame around the top area with bracket accents, an Orbitron
/// title, a Share Tech Mono value, dot ticks and a segmented warning zone.
/// </summary>
internal sealed class FrameTheme : IThemeGenerator
{
    private const double Radius = 151;

    // Frame edges in pixel-centre coordinates so the 1 px lines and the 45° chamfers meet cleanly.
    private const double Left = 3.5;
    private const double Right = 196.5;
    private const double Top = 3.5;
    private const double Bottom = 64.5;
    private const double Chamfer = 9;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "hud/frame",
        "HUD frame",
        "HUD con cornice",
        "A sci-fi readout: chamfered frame, bracket accents, monospaced digits and a dotted scale.",
        "Una lettura fantascientifica: cornice smussata, parentesi decorative, cifre monospaziate e scala a punti.");

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var majors = ThemeKit.LabelCount(config);

        // Chamfered frame (top-left and bottom-right corners cut at 45°).
        layers.Add(ThemeKit.Line(Left + Chamfer, Top, Right, Top, name: "Frame top"));
        layers.Add(ThemeKit.Line(Left, Top + Chamfer, Left + Chamfer, Top, name: "Frame chamfer"));
        layers.Add(ThemeKit.Line(Left, Top + Chamfer, Left, Bottom, name: "Frame left"));
        layers.Add(ThemeKit.Line(Left, Bottom, Right - Chamfer, Bottom, name: "Frame bottom"));
        layers.Add(ThemeKit.Line(Right - Chamfer, Bottom, Right, Bottom - Chamfer, name: "Frame chamfer"));
        layers.Add(ThemeKit.Line(Right, Bottom - Chamfer, Right, Top, name: "Frame right"));

        // Bracket accents: 3 px L-shapes hugging the two square corners (the 1 px frame runs through their middle).
        layers.Add(ThemeKit.Rect(Right - 24, Top - 1.5, 26, 3, stroke: 0, fill: true, name: "Accent"));
        layers.Add(ThemeKit.Rect(Right - 1.5, Top - 1.5, 3, 18, stroke: 0, fill: true, name: "Accent"));
        layers.Add(ThemeKit.Rect(Left - 1.5, Bottom - 16.5, 3, 18, stroke: 0, fill: true, name: "Accent"));
        layers.Add(ThemeKit.Rect(Left - 1.5, Bottom - 1.5, 26, 3, stroke: 0, fill: true, name: "Accent"));

        var icon = ThemeKit.IconTemplate(config, context);
        var titleX = icon is null ? 17.0 : 34.0;
        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 23, Y = 15, Size = 16 });
        }

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = titleX,
            Y = 11,
            Align = TextAlign.Left,
            Baseline = TextBaseline.Top,
            Font = "orbitron",
            Size = 10,
            Weight = 700,
            Uppercase = true,
            LetterSpacing = 1.5,
            MaxWidth = Right - 30 - titleX,
            Overflow = TextOverflow.Ellipsis,
            Smooth = true,
        });

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            ((TextLayer)layers[^1]).MaxWidth = 92;
            layers.Add(ThemeKit.Subtitle(subtitle, Right - 28, 11, "share-tech-mono", 10, 400, TextAlign.Right, TextBaseline.Top, maxWidth: 56));
        }

        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = config.ShowSparkline ? 46 : 55,
                Font = "share-tech-mono",
                Size = config.ShowSparkline ? 28 : 34,
                Weight = 400,
                UnitFont = "orbitron",
                UnitSize = 9,
                UnitWeight = 700,
                UnitUppercase = false,
                Placement = UnitPlacement.Top,
                Gap = 4,
                Left = 12,
                Right = 188,
            }));
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(30, 50, 140, 11, stroke: 1));
        }

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = Radius,
            ShowArc = true,
            ArcRadius = Radius - 9,
            ArcWidth = 1,
            TickStyle = TickStyle.Dot,
            MajorCount = majors,
            MinorPerMajor = ThemeKit.MinorCount(context, majors, dense: false),
            MajorWidth = 4,
            MinorWidth = 2,
            LabelRadius = 129,
            LabelFont = "share-tech-mono",
            LabelSize = 11,
            LabelWeight = 400,
        };

        // Segmented warning zone: short blocks with gaps along the arc.
        if (config.WarnFrom is { } from && double.IsFinite(from) && from < 1)
        {
            from = Math.Clamp(from, 0, 1);
            const double segment = 0.028;
            const double gap = 0.01;
            var count = Math.Max(1, (int)Math.Round((1 - from + gap) / (segment + gap)));
            var pitch = (1 - from + gap) / count;
            for (var i = 0; i < count; i++)
            {
                var start = from + (i * pitch);
                scale.Zones.Add(new ScaleZone { From = start, To = Math.Min(1, start + pitch - gap), Style = ZoneStyle.Band, InnerRadius = Radius - 12, OuterRadius = Radius - 7 });
            }
        }

        layers.Add(scale);
        return doc;
    }
}
