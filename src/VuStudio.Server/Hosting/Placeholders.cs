using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;

namespace VuStudio.Server.Hosting;

// Temporary stand-ins used until the hardware and rendering modules are merged.

internal sealed class PlaceholderRenderer : IFaceRenderer
{
    public IReadOnlyList<ThemeInfo> Themes { get; } = [];

    public IReadOnlyList<FontInfo> Fonts { get; } = [];

    public IReadOnlyList<IconInfo> Icons { get; } = [];

    public FaceDocument BuildDocument(FaceConfig config, FaceRenderContext context) => config.Custom ?? new FaceDocument();

    public MonoBitmap Render(FaceDocument document, FaceRenderContext context) => new();

    public byte[] EncodePng(MonoBitmap bitmap, int scale = 1, RgbwColor? tint = null) => [];
}
