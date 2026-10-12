using System.Collections.Concurrent;
using System.Text.Json;
using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Text;

/// <summary>One font file of a bundled family.</summary>
internal sealed record FontFace(
    string Path,
    bool Italic,
    bool Variable,
    int Weight,
    float WeightMin,
    float WeightMax,
    float OpticalSizeMin,
    float OpticalSizeMax)
{
    public bool HasOpticalSize => OpticalSizeMax > OpticalSizeMin;

    /// <summary>How far <paramref name="weight"/> is from what this file can render.</summary>
    public float Distance(int weight) =>
        Variable
            ? weight < WeightMin ? WeightMin - weight : weight > WeightMax ? weight - WeightMax : 0
            : Math.Abs(Weight - weight);
}

/// <summary>A bundled font family as described by <c>Fonts/fonts.json</c>.</summary>
internal sealed record FontFamily(string Id, string Name, string Category, IReadOnlyList<FontFace> Faces)
{
    /// <summary>Pixel fonts are drawn without anti-aliasing or hinting so their grid stays intact.</summary>
    public bool IsPixel => Category == "pixel";

    /// <summary>Size (px) at which one design pixel of a pixel font maps to one display pixel.</summary>
    public float PixelGrid => Id switch
    {
        "silkscreen" => 8,
        "vt323" => 25,
        _ => 0,
    };
}

/// <summary>A typeface picked for a requested font id, weight and style.</summary>
internal readonly record struct ResolvedTypeface(SKTypeface Typeface, FontFamily Family, bool SyntheticItalic);

/// <summary>
/// The bundled fonts. Files are read from embedded resources on first use; typefaces are cached per
/// (file, weight, optical size) and shared by all threads (Skia typefaces are immutable).
/// </summary>
internal sealed class FontLibrary
{
    public const string DefaultFontId = "inter";

    private const string ResourcePrefix = "VuStudio.Rendering.Assets/Fonts/";

    private readonly Dictionary<string, FontFamily> _families;
    private readonly ConcurrentDictionary<string, Lazy<SKTypeface>> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Path, int Weight, int OpticalSize), Lazy<SKTypeface>> _instances = new();

    public FontLibrary()
    {
        using var stream = FaceAssets.OpenResource(ResourcePrefix + "fonts.json");
        using var json = JsonDocument.Parse(stream);
        var families = new List<FontFamily>();
        foreach (var font in json.RootElement.GetProperty("fonts").EnumerateArray())
        {
            var faces = new List<FontFace>();
            foreach (var file in font.GetProperty("files").EnumerateArray())
            {
                var variable = file.TryGetProperty("variable", out var v) && v.GetBoolean();
                var italic = file.TryGetProperty("italic", out var i) && i.GetBoolean();
                var weight = file.TryGetProperty("weight", out var w) ? w.GetInt32() : 400;
                var (wMin, wMax) = Axis(file, "wght", weight);
                var (oMin, oMax) = Axis(file, "opsz", 0);
                faces.Add(new FontFace(file.GetProperty("path").GetString()!, italic, variable, weight, wMin, wMax, oMin, oMax));
            }

            families.Add(new FontFamily(
                font.GetProperty("id").GetString()!,
                font.GetProperty("family").GetString()!,
                font.TryGetProperty("category", out var c) ? c.GetString() ?? "sans" : "sans",
                faces));
        }

        _families = families.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        Families = families;
        Fonts = families.Select(ToInfo).ToList();

        static (float Min, float Max) Axis(JsonElement file, string tag, float fallback)
        {
            if (file.TryGetProperty("axes", out var axes) && axes.TryGetProperty(tag, out var range) && range.GetArrayLength() == 2)
            {
                return (range[0].GetSingle(), range[1].GetSingle());
            }

            return (fallback, fallback);
        }
    }

    public IReadOnlyList<FontFamily> Families { get; }

    public IReadOnlyList<FontInfo> Fonts { get; }

    public FontFamily? FindFamily(string? id) =>
        id is not null && _families.TryGetValue(id.Trim(), out var family) ? family : null;

    /// <summary>
    /// Picks the typeface for a font id (unknown ids fall back to Inter), weight and style. Variable fonts
    /// get their <c>wght</c> axis set (and <c>opsz</c> from the physical size of <paramref name="sizePx"/>);
    /// static fonts use the nearest available weight. A missing italic is synthesised by the caller.
    /// </summary>
    public ResolvedTypeface Resolve(string? fontId, int weight, bool italic, float sizePx)
    {
        var family = FindFamily(fontId) ?? _families[DefaultFontId];
        weight = Math.Clamp(weight, 1, 1000);

        var synthetic = false;
        var candidates = family.Faces.Where(f => f.Italic == italic).ToList();
        if (candidates.Count == 0)
        {
            candidates = family.Faces.Where(f => !f.Italic).ToList();
            synthetic = italic;
        }

        if (candidates.Count == 0)
        {
            candidates = [.. family.Faces];
        }

        // Nearest weight; on a tie prefer the heavier file, which survives the 1-bit threshold better.
        var face = candidates.OrderBy(f => f.Distance(weight)).ThenByDescending(f => f.Weight).First();
        return new ResolvedTypeface(GetTypeface(face, weight, sizePx), family, synthetic);
    }

    private SKTypeface GetTypeface(FontFace face, int weight, float sizePx)
    {
        var file = _files.GetOrAdd(face.Path, path => new Lazy<SKTypeface>(() => Load(path))).Value;
        if (!face.Variable)
        {
            return file;
        }

        // Round to 10 so arbitrary user weights cannot grow the cache without bound.
        var wght = (int)(Math.Round(Math.Clamp(weight, face.WeightMin, face.WeightMax) / 10.0) * 10);
        var opsz = face.HasOpticalSize ? (int)Math.Round(Math.Clamp(PhysicalPoints(sizePx), face.OpticalSizeMin, face.OpticalSizeMax)) : 0;
        return _instances.GetOrAdd((face.Path, wght, opsz), key => new Lazy<SKTypeface>(() =>
        {
            var weightAxis = new SKFontVariationPositionCoordinate { Axis = SKFourByteTag.Parse("wght"), Value = key.Weight };
            SKFontVariationPositionCoordinate[] coordinates = key.OpticalSize > 0
                ? [weightAxis, new SKFontVariationPositionCoordinate { Axis = SKFourByteTag.Parse("opsz"), Value = key.OpticalSize }]
                : [weightAxis];
            return file.Clone(coordinates) ?? file;
        })).Value;
    }

    /// <summary>Optical size follows the physical size on the 188 dpi panel (1 pt = 188/72 px).</summary>
    private static float PhysicalPoints(float sizePx) => sizePx * 72f / 188f;

    private static SKTypeface Load(string path)
    {
        using var stream = FaceAssets.OpenResource(ResourcePrefix + path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        using var data = SKData.CreateCopy(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        return SKTypeface.FromData(data, 0) ?? throw new InvalidDataException($"Font '{path}' could not be loaded.");
    }

    private static FontInfo ToInfo(FontFamily family)
    {
        var weights = new SortedSet<int>();
        foreach (var face in family.Faces)
        {
            if (face.Variable)
            {
                for (var w = (int)Math.Ceiling(face.WeightMin / 100) * 100; w <= face.WeightMax; w += 100)
                {
                    weights.Add(w);
                }
            }
            else
            {
                weights.Add(face.Weight);
            }
        }

        return new FontInfo(family.Id, family.Name, family.Category, [.. weights], family.Faces.Any(f => f.Italic));
    }
}
