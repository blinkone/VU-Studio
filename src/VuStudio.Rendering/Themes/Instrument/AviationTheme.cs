using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Instrument;

/// <summary>
/// instrument/aviation — an EFIS engine display: the value in a boxed B612 Mono readout at the top, a scale
/// with wedge ticks, a dotted caution band and a radial red line where the warning starts.
/// </summary>
internal sealed class AviationTheme : IThemeGenerator
{
    private const double Radius = 149;
    private const double BoxHeight = 26;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "instrument/aviation",
        "Aviation",
        "Aviazione",
        "A cockpit engine display: boxed digital readout, wedge ticks, caution band and red line.",
        "Un display motore da cabina: lettura digitale in riquadro, tacche a cuneo, banda di attenzione e linea rossa.");

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var majors = ThemeKit.LabelCount(config);

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = Radius,
            ShowArc = true,
            ArcRadius = Radius,
            ArcWidth = 2,
            TickStyle = TickStyle.Wedge,
            MajorCount = majors,
            MinorPerMajor = 1,
            MajorLength = 12,
            MajorWidth = 7,
            MinorLength = 7,
            MinorWidth = 4,
            LabelRadius = 127,
            LabelFont = "b612",
            LabelSize = 11,
            LabelWeight = 700,
        };
        if (config.WarnFrom is { } from && double.IsFinite(from) && from < 1)
        {
            scale.Zones.Add(new ScaleZone { From = from, To = 1, Style = ZoneStyle.Dotted, InnerRadius = Radius - 11, OuterRadius = Radius - 1 });
            var redline = Math.Clamp(from, 0, 0.995);
            scale.Zones.Add(new ScaleZone { From = redline, To = redline + 0.012, Style = ZoneStyle.Band, InnerRadius = Radius - 16, OuterRadius = Radius + 4 });
        }

        layers.Add(scale);

        // Boxed digital readout. The box is sized for a typical value; longer values shrink inside it.
        var top = config.ShowSparkline ? 16.0 : 22.0;
        var sample = ThemeKit.SampleValue(context);
        var digits = Math.Max(3, sample.Length);
        var charWidth = ThemeKit.Measure("0", "b612-mono", 20, 700);
        var boxWidth = Math.Clamp(Math.Round((digits * charWidth) + 14), 58, 150);
        var boxLeft = Math.Round(ThemeKit.CenterX - (boxWidth / 2));
        var titleTemplate = ThemeKit.TitleTemplate(config);

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = titleTemplate,
            X = boxLeft,
            Y = top - 4,
            Baseline = TextBaseline.Bottom,
            Align = TextAlign.Left,
            Font = "b612",
            Size = 10,
            Weight = 700,
            Uppercase = true,
            LetterSpacing = 1,
            MaxWidth = 150,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (config.ShowValue)
        {
            layers.Add(ThemeKit.Rect(boxLeft, top, boxWidth, BoxHeight, stroke: 2, name: "Readout box"));
            layers.Add(new TextLayer
            {
                Name = "Value",
                Text = "{value}",
                X = boxLeft + boxWidth - 7,
                Y = top + (BoxHeight / 2),
                Align = TextAlign.Right,
                Baseline = TextBaseline.Middle,
                Font = "b612-mono",
                Size = 20,
                Weight = 700,
                MaxWidth = boxWidth - 12,
                Overflow = TextOverflow.Shrink,
            });

            if (ThemeKit.Unit(context).Length > 0)
            {
                layers.Add(new TextLayer
                {
                    Name = "Unit",
                    Text = "{unit}",
                    X = boxLeft + boxWidth + 5,
                    Y = top + BoxHeight - 6,
                    Align = TextAlign.Left,
                    Baseline = TextBaseline.Baseline,
                    Font = "b612-mono",
                    Size = 10,
                    Weight = 400,
                    MaxWidth = Math.Max(12, 196 - (boxLeft + boxWidth + 5)),
                    Overflow = TextOverflow.Shrink,
                    Smooth = false,
                });
            }
        }

        if (ThemeKit.IconTemplate(config, context) is { } icon)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 186, Y = 14, Size = 18 });
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(boxLeft, top + BoxHeight + 5, boxWidth, 12, stroke: 1));
        }

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            var y = top + BoxHeight + (config.ShowSparkline ? 21 : 5);
            layers.Add(ThemeKit.Subtitle(subtitle, ThemeKit.CenterX, y, "b612", 9, 400, baseline: TextBaseline.Top, maxWidth: 120));
        }

        return doc;
    }
}
