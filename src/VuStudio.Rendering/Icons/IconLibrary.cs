using System.Collections.Concurrent;
using System.Text.Json;
using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Icons;

/// <summary>
/// The bundled Material Design Icons subset (<c>Icons/icons.json</c>, 24 × 24 SVG path data).
/// Paths are parsed on first use and shared between threads.
/// </summary>
internal sealed class IconLibrary
{
    private const string Resource = "VuStudio.Rendering.Assets/Icons/icons.json";

    private readonly Dictionary<string, IconInfo> _byId;
    private readonly ConcurrentDictionary<string, Lazy<SKPath?>> _paths = new(StringComparer.Ordinal);

    public IconLibrary()
    {
        using var stream = FaceAssets.OpenResource(Resource);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        ViewBox = root.TryGetProperty("viewBox", out var vb) ? vb.GetSingle() : 24;
        var icons = new List<IconInfo>();
        foreach (var icon in root.GetProperty("icons").EnumerateArray())
        {
            icons.Add(new IconInfo(
                icon.GetProperty("id").GetString()!,
                icon.TryGetProperty("category", out var c) ? c.GetString() ?? "misc" : "misc",
                icon.GetProperty("path").GetString()!));
        }

        Icons = icons;
        _byId = icons.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IconInfo> Icons { get; }

    /// <summary>Size of the square view box the path data is drawn in (24 for MDI).</summary>
    public float ViewBox { get; }

    public bool Contains(string? id) => id is not null && _byId.ContainsKey(id.Trim());

    /// <summary>The parsed path in view-box units, or null for unknown ids.</summary>
    public SKPath? GetPath(string? id)
    {
        if (id is null || !_byId.TryGetValue(id.Trim(), out var info))
        {
            return null;
        }

        return _paths.GetOrAdd(info.Id, _ => new Lazy<SKPath?>(() => Parse(info.Path))).Value;
    }

    private static SKPath? Parse(string data)
    {
        var path = SKPath.ParseSvgPathData(data);
        if (path is null)
        {
            return null;
        }

        // Skia computes bounds and convexity lazily; do it now so concurrent draws only ever read.
        _ = path.Bounds;
        _ = path.TightBounds;
        _ = path.IsConvex;
        return path;
    }
}
