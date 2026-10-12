using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VuStudio.Core.Faces;

/// <summary>Which of the two face colors a layer draws with.</summary>
public enum Ink
{
    /// <summary>Black on a normal face, white on an inverted one.</summary>
    Foreground,

    /// <summary>The paper color: erases/knocks out (e.g. white text on a black box).</summary>
    Background,
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum TextBaseline
{
    Top,
    Middle,
    Baseline,
    Bottom,
}

public enum TextOverflow
{
    Visible,
    Clip,
    Ellipsis,

    /// <summary>Reduce the font size until the text fits <see cref="TextLayer.MaxWidth"/>.</summary>
    Shrink,
}

public enum DitherMode
{
    Threshold,
    FloydSteinberg,
    Atkinson,
    Bayer,
}

public enum ImageFit
{
    Contain,
    Cover,
    Stretch,
    None,
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arc,
}

public enum TickStyle
{
    Line,
    Dot,
    Wedge,
}

public enum ZoneStyle
{
    Band,
    Hatched,
    Dotted,
    Outline,
}

public enum Orientation
{
    Horizontal,
    Vertical,
}

public enum SparklineStyle
{
    Line,
    Area,
    Bars,
}

/// <summary>A dial face: an ordered list of layers on a 200 × 144 one-bit canvas.</summary>
public sealed class FaceDocument
{
    public int Version { get; set; } = 1;

    /// <summary>Black paper with white ink.</summary>
    public bool Inverted { get; set; }

    public List<FaceLayer> Layers { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScaleLayer), "scale")]
[JsonDerivedType(typeof(TextLayer), "text")]
[JsonDerivedType(typeof(IconLayer), "icon")]
[JsonDerivedType(typeof(ImageLayer), "image")]
[JsonDerivedType(typeof(ShapeLayer), "shape")]
[JsonDerivedType(typeof(BarLayer), "bar")]
[JsonDerivedType(typeof(SparklineLayer), "sparkline")]
public abstract class FaceLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    /// <summary>Label shown in the editor's layer list.</summary>
    public string? Name { get; set; }

    public bool Visible { get; set; } = true;

    public Ink Ink { get; set; } = Ink.Foreground;
}

/// <summary>Ticks, labels, arc and zones drawn around the needle pivot; follows the page's value mapping.</summary>
public sealed class ScaleLayer : FaceLayer
{
    /// <summary>Outer radius of the ticks.</summary>
    public double Radius { get; set; } = 152;

    public double MajorLength { get; set; } = 14;

    public double MinorLength { get; set; } = 6;

    public double MajorWidth { get; set; } = 2.5;

    public double MinorWidth { get; set; } = 1.2;

    public TickStyle TickStyle { get; set; } = TickStyle.Line;

    /// <summary>Approximate number of major ticks (labels). Nice round values are chosen from the mapping.</summary>
    public int MajorCount { get; set; } = 5;

    public int MinorPerMajor { get; set; } = 4;

    /// <summary>Use exactly <see cref="MajorCount"/> evenly spaced majors instead of "nice" values.</summary>
    public bool EvenSpacing { get; set; }

    public bool ShowLabels { get; set; } = true;

    /// <summary>Radius of the label centres.</summary>
    public double LabelRadius { get; set; } = 124;

    public string LabelFont { get; set; } = "barlow-condensed";

    public double LabelSize { get; set; } = 14;

    public int LabelWeight { get; set; } = 600;

    /// <summary>.NET numeric format for labels (e.g. "0", "0.#"); null picks one automatically.</summary>
    public string? LabelFormat { get; set; }

    /// <summary>Divide label values by this factor (e.g. 1000 to print "2" for 2000 RPM with a "x1000" caption).</summary>
    public double LabelDivisor { get; set; } = 1;

    /// <summary>Explicit label texts, placed on evenly spaced majors (overrides values).</summary>
    public List<string>? CustomLabels { get; set; }

    /// <summary>Rotate labels along the arc instead of keeping them upright.</summary>
    public bool RotateLabels { get; set; }

    public bool ShowArc { get; set; }

    /// <summary>Arc radius; null uses <see cref="Radius"/>.</summary>
    public double? ArcRadius { get; set; }

    public double ArcWidth { get; set; } = 1.5;

    public List<ScaleZone> Zones { get; set; } = [];
}

/// <summary>A highlighted range of the scale, in dial fractions (0..1).</summary>
public sealed class ScaleZone
{
    public double From { get; set; } = 0.85;

    public double To { get; set; } = 1;

    public ZoneStyle Style { get; set; } = ZoneStyle.Band;

    public double InnerRadius { get; set; } = 138;

    public double OuterRadius { get; set; } = 152;
}

public sealed class TextLayer : FaceLayer
{
    public double X { get; set; } = 100;

    public double Y { get; set; } = 30;

    /// <summary>Template, e.g. "{title}", "{value} {unit}", "{time:HH:mm}", "{sensor:lhm/cpu/0/load/0}".</summary>
    public string Text { get; set; } = "{title}";

    /// <summary>Font id from the bundled set (e.g. "inter", "barlow-condensed").</summary>
    public string Font { get; set; } = "inter";

    public double Size { get; set; } = 18;

    public int Weight { get; set; } = 600;

    public bool Italic { get; set; }

    public TextAlign Align { get; set; } = TextAlign.Center;

    public TextBaseline Baseline { get; set; } = TextBaseline.Middle;

    public double? MaxWidth { get; set; }

    public TextOverflow Overflow { get; set; } = TextOverflow.Shrink;

    public double LetterSpacing { get; set; }

    public bool Uppercase { get; set; }

    /// <summary>Render with antialiasing then threshold (smoother curves); false draws aliased (crisper at small sizes).</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>Rotation in degrees around (X, Y).</summary>
    public double Rotation { get; set; }
}

public sealed class IconLayer : FaceLayer
{
    /// <summary>Center X.</summary>
    public double X { get; set; } = 100;

    /// <summary>Center Y.</summary>
    public double Y { get; set; } = 30;

    public double Size { get; set; } = 24;

    /// <summary>Icon id, or "{icon}" for the page's icon.</summary>
    public string Icon { get; set; } = "{icon}";
}

public sealed class ImageLayer : FaceLayer
{
    /// <summary>Id of an uploaded asset.</summary>
    public string AssetId { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 200;

    public double Height { get; set; } = 144;

    public ImageFit Fit { get; set; } = ImageFit.Contain;

    public DitherMode Dither { get; set; } = DitherMode.FloydSteinberg;

    /// <summary>0..255 threshold for <see cref="DitherMode.Threshold"/> and the dithering midpoint.</summary>
    public int Threshold { get; set; } = 128;

    /// <summary>-100..100.</summary>
    public int Brightness { get; set; }

    /// <summary>-100..100.</summary>
    public int Contrast { get; set; }

    public bool InvertImage { get; set; }
}

public sealed class ShapeLayer : FaceLayer
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;

    public double X { get; set; } = 10;

    public double Y { get; set; } = 10;

    /// <summary>Width (rectangle/ellipse) — for lines the end point is (X2, Y2).</summary>
    public double Width { get; set; } = 40;

    public double Height { get; set; } = 20;

    public double X2 { get; set; } = 50;

    public double Y2 { get; set; } = 10;

    public double StrokeWidth { get; set; } = 1.5;

    public bool Fill { get; set; }

    public bool Stroke { get; set; } = true;

    public double CornerRadius { get; set; }

    /// <summary>Arc start angle (degrees, 0 = right, clockwise).</summary>
    public double StartAngle { get; set; }

    public double SweepAngle { get; set; } = 90;

    public bool Dashed { get; set; }
}

/// <summary>A linear gauge for a secondary value.</summary>
public sealed class BarLayer : FaceLayer
{
    public double X { get; set; } = 20;

    public double Y { get; set; } = 120;

    public double Width { get; set; } = 160;

    public double Height { get; set; } = 8;

    /// <summary>Sensor to show; null shows the page value.</summary>
    public string? SensorId { get; set; }

    /// <summary>Range in the sensor's canonical units; null uses the page mapping or the sensor's suggested range.</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    /// <summary>0 = solid fill, otherwise number of segments.</summary>
    public int Segments { get; set; }

    public double Gap { get; set; } = 1;

    public bool Border { get; set; } = true;
}

/// <summary>Recent history of a sensor as a tiny chart.</summary>
public sealed class SparklineLayer : FaceLayer
{
    public double X { get; set; } = 20;

    public double Y { get; set; } = 96;

    public double Width { get; set; } = 160;

    public double Height { get; set; } = 24;

    /// <summary>Sensor to chart; null charts the page's sensor.</summary>
    public string? SensorId { get; set; }

    public int WindowSeconds { get; set; } = 300;

    public SparklineStyle Style { get; set; } = SparklineStyle.Line;

    public double StrokeWidth { get; set; } = 1.5;

    /// <summary>Fixed range (canonical units); null = auto-fit to the data.</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    public bool Border { get; set; }
}

/// <summary>How a page's face is produced.</summary>
public sealed class FaceConfig
{
    /// <summary>Theme id "{family}/{variant}", e.g. "vintage/classic", "minimal/arc", "instrument/gauge", "hud/frame".</summary>
    public string Theme { get; set; } = "minimal/arc";

    /// <summary>Title override; null uses the binding label or the sensor name.</summary>
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Icon override; null uses the sensor's suggested icon.</summary>
    public string? Icon { get; set; }

    public bool ShowIcon { get; set; } = true;

    public bool ShowValue { get; set; } = true;

    public bool ShowSparkline { get; set; }

    /// <summary>Start of the warning zone as a dial fraction; null = no zone.</summary>
    public double? WarnFrom { get; set; } = 0.85;

    public int LabelCount { get; set; } = 5;

    public bool Inverted { get; set; }

    /// <summary>Theme-specific options (see <c>ThemeInfo.Options</c>).</summary>
    public JsonObject? Options { get; set; }

    /// <summary>When set, this document is rendered instead of the theme.</summary>
    public FaceDocument? Custom { get; set; }

    /// <summary>Minimum seconds between e-paper refreshes caused by changing content.</summary>
    public int MinRefreshSeconds { get; set; } = 60;
}
