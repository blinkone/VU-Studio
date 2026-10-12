using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Rendering.Drawing;
using VuStudio.Rendering.Imaging;
using VuStudio.Rendering.Themes;

namespace VuStudio.Rendering;

/// <summary>
/// SkiaSharp implementation of <see cref="IFaceRenderer"/>. Layers are drawn onto an 8-bit canvas with the
/// two ink colors only (anti-aliased edges pass a coverage threshold), then thresholded at 128 into the
/// one-bit <see cref="MonoBitmap"/> that goes to the e-paper. Thread-safe: each call renders on its own
/// canvas, and fonts, icon paths and processed images are shared through thread-safe caches.
/// </summary>
public sealed class FaceRenderer : IFaceRenderer
{
    /// <summary>Theme used when <see cref="FaceConfig.Theme"/> is unknown.</summary>
    public const string DefaultThemeId = "minimal/arc";

    private static readonly (byte R, byte G, byte B) InkPreview = (0x14, 0x14, 0x14);

    private readonly FaceAssets _assets = FaceAssets.Shared;
    private readonly ImageLayerCache _images;
    private readonly Dictionary<string, IThemeGenerator> _themes;

    /// <param name="assetStore">Source of uploaded images for image layers; null skips them.</param>
    /// <param name="themes">Theme generators; null uses <see cref="BuiltInThemes.All"/>. Later ids replace earlier ones.</param>
    public FaceRenderer(IAssetStore? assetStore = null, IEnumerable<IThemeGenerator>? themes = null)
    {
        _images = new ImageLayerCache(assetStore);
        _themes = new Dictionary<string, IThemeGenerator>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes ?? BuiltInThemes.All)
        {
            _themes[theme.Info.Id] = theme;
        }

        Generators = [.. _themes.Values];
        Themes = [.. Generators.Select(t => t.Info)];
    }

    /// <summary>Raised (on the rendering thread) when a layer throws; the rest of the face is still drawn.</summary>
    public event Action<FaceLayer, Exception>? LayerFailed;

    public IReadOnlyList<ThemeInfo> Themes { get; }

    public IReadOnlyList<IThemeGenerator> Generators { get; }

    public IReadOnlyList<FontInfo> Fonts => _assets.Fonts.Fonts;

    public IReadOnlyList<IconInfo> Icons => _assets.Icons.Icons;

    public IThemeGenerator? FindTheme(string? id) =>
        id is not null && _themes.TryGetValue(id.Trim(), out var theme) ? theme : null;

    public FaceDocument BuildDocument(FaceConfig config, FaceRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);
        if (config.Custom is { } custom)
        {
            return custom;
        }

        var theme = FindTheme(config.Theme) ?? FindTheme(DefaultThemeId) ?? Generators[0];
        return theme.Generate(config, context);
    }

    public MonoBitmap Render(FaceDocument document, FaceRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);
        using var face = new FaceCanvas(document.Inverted, context, _assets);
        foreach (var layer in document.Layers ?? [])
        {
            if (layer is null || !layer.Visible)
            {
                continue;
            }

            try
            {
                DrawLayer(face, layer);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A broken layer must not take the whole face (or the engine loop) down.
                face.Canvas.RestoreToCount(1);
                LayerFailed?.Invoke(layer, ex);
            }
        }

        return face.ToMonoBitmap();
    }

    public byte[] EncodePng(MonoBitmap bitmap, int scale = 1, RgbwColor? tint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return PngEncoder.Encode(bitmap, scale, InkPreview, PaperPreview(tint));
    }

    /// <summary>Drops processed images (e.g. after assets were replaced).</summary>
    public void ClearImageCache() => _images.Clear();

    /// <summary>
    /// Preview color of lit paper: the tint's RGB plus its white LED (as in <see cref="LedLevels.ToPreviewColor"/>).
    /// No tint, or a backlight that is off, shows plain white.
    /// </summary>
    internal static (byte R, byte G, byte B) PaperPreview(RgbwColor? tint)
    {
        if (tint is not { IsOff: false } c)
        {
            return (255, 255, 255);
        }

        var white = c.W * 0.8;
        return (Mix(c.R), Mix(c.G), Mix(c.B));

        byte Mix(byte channel) => (byte)Math.Clamp(Math.Round(channel + white), 0, 255);
    }

    private void DrawLayer(FaceCanvas face, FaceLayer layer)
    {
        switch (layer)
        {
            case ScaleLayer scale:
                ScaleDrawer.Draw(face, scale);
                break;
            case TextLayer text:
                TextDrawer.Draw(face, text);
                break;
            case IconLayer icon:
                IconDrawer.Draw(face, icon);
                break;
            case ImageLayer image:
                ImageDrawer.Draw(face, image, _images);
                break;
            case ShapeLayer shape:
                ShapeDrawer.Draw(face, shape);
                break;
            case BarLayer bar:
                BarDrawer.Draw(face, bar);
                break;
            case SparklineLayer sparkline:
                SparklineDrawer.Draw(face, sparkline);
                break;
        }
    }
}
