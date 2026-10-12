using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Hud;

/// <summary>
/// hud/grid — a tactical display: a dotted grid texture over the free area above the scale, a knocked-out
/// panel with the title on its border, a segmented bar (or sparkline) and corner brackets.
/// </summary>
internal sealed class GridTheme : IThemeGenerator
{
    private const double Radius = 150;
    private const double PanelLeft = 22;
    private const double PanelRight = 178;
    private const double PanelTop = 12;
    private const double PanelBottom = 61;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "hud/grid",
        "HUD grid",
        "HUD a griglia",
        "A tactical display: dotted grid texture, framed readout, segmented level bar and corner brackets.",
        "Un display tattico: trama a griglia di punti, lettura incorniciata, barra a segmenti e angoli a staffa.",
        ThemeKit.BoolOption("levelBar", "Segmented level bar", "Barra a segmenti", true));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var majors = ThemeKit.LabelCount(config);

        // Grid texture over everything the needle never reaches (a large dotted zone becomes a 4 px grid).
        layers.Add(new ScaleLayer
        {
            Name = "Grid",
            Radius = Radius,
            MajorLength = 0,
            MinorLength = 0,
            MinorPerMajor = 0,
            ShowLabels = false,
            Zones = [new ScaleZone { From = 0, To = 1, Style = ZoneStyle.Dotted, InnerRadius = Radius + 6, OuterRadius = 260 }],
        });

        // Knocked-out panel with a 1 px frame.
        layers.Add(ThemeKit.Rect(PanelLeft, PanelTop, PanelRight - PanelLeft, PanelBottom - PanelTop, stroke: 1, fill: true, ink: Ink.Background, name: "Panel"));
        layers.Add(ThemeKit.Rect(PanelLeft, PanelTop, PanelRight - PanelLeft, PanelBottom - PanelTop, stroke: 1, name: "Panel frame"));

        // Title as a tab on the panel's top edge: "▌ TITLE" knocked out of the border.
        var title = ThemeKit.ResolvedTitle(config, context).ToUpper(context.Culture);
        var titleWidth = Math.Min(118, ThemeKit.Measure(title, "share-tech-mono", 11, 400, letterSpacing: 1, smooth: false));
        var icon = ThemeKit.IconTemplate(config, context);
        var tabLeft = PanelLeft + 8;
        var tabWidth = titleWidth + 10 + (icon is null ? 0 : 16);
        layers.Add(ThemeKit.Rect(tabLeft, PanelTop - 5, tabWidth, 11, stroke: 0, fill: true, ink: Ink.Background, name: "Title tab"));
        layers.Add(ThemeKit.Rect(tabLeft, PanelTop - 4, 2, 9, stroke: 0, fill: true, name: "Tab marker"));
        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = tabLeft + 12, Y = PanelTop, Size = 14 });
        }

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = tabLeft + 6 + (icon is null ? 0 : 16),
            Y = PanelTop,
            Align = TextAlign.Left,
            Baseline = TextBaseline.Middle,
            Font = "share-tech-mono",
            Size = 11,
            Weight = 400,
            Uppercase = true,
            LetterSpacing = 1,
            MaxWidth = 118,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            layers.Add(ThemeKit.Subtitle(subtitle, PanelRight - 6, PanelTop + 3, "share-tech-mono", 9, 400, TextAlign.Right, TextBaseline.Top, maxWidth: Math.Max(20, PanelRight - 12 - (tabLeft + tabWidth))));
        }

        var showBar = ThemeKit.Option(config, "levelBar", true) || config.ShowSparkline;
        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = showBar ? 43 : 50,
                Font = "share-tech-mono",
                Size = showBar ? 26 : 30,
                Weight = 400,
                UnitFont = "share-tech-mono",
                UnitSize = 11,
                UnitWeight = 400,
                Placement = UnitPlacement.Baseline,
                Gap = 4,
                Left = PanelLeft + 6,
                Right = PanelRight - 6,
            }));
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(PanelLeft + 8, 47, PanelRight - PanelLeft - 16, 11, SparklineStyle.Area, stroke: 1));
        }
        else if (showBar)
        {
            layers.Add(new BarLayer
            {
                Name = "Level",
                X = PanelLeft + 8,
                Y = 49,
                Width = PanelRight - PanelLeft - 16,
                Height = 6,
                Segments = 24,
                Gap = 2,
                Border = false,
            });
        }

        // Corner brackets.
        foreach (var (x, y, dx, dy) in new[] { (2.0, 2.0, 1, 1), (197.0, 2.0, -1, 1), (2.0, 141.0, 1, -1), (197.0, 141.0, -1, -1) })
        {
            layers.Add(ThemeKit.Rect(dx > 0 ? x : x - 9, y + (dy > 0 ? 0 : -1), 10, 2, stroke: 0, fill: true, name: "Bracket"));
            layers.Add(ThemeKit.Rect(x + (dx > 0 ? 0 : -1), dy > 0 ? y : y - 9, 2, 10, stroke: 0, fill: true, name: "Bracket"));
        }

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = Radius,
            ShowArc = true,
            ArcRadius = Radius - 0.5,
            ArcWidth = 1,
            MajorCount = majors,
            MinorPerMajor = ThemeKit.MinorCount(context, majors, dense: false),
            MajorLength = 10,
            MajorWidth = 2,
            MinorLength = 5,
            MinorWidth = 1,
            LabelRadius = 130,
            LabelFont = "share-tech-mono",
            LabelSize = 11,
            LabelWeight = 400,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Hatched, Radius - 10, Radius) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);
        return doc;
    }
}
