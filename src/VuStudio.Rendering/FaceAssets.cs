using VuStudio.Rendering.Icons;
using VuStudio.Rendering.Text;

namespace VuStudio.Rendering;

/// <summary>
/// Fonts and icons embedded in this assembly (<c>VuStudio.Rendering.Assets/…</c>). Loaded once per process
/// on first use and shared by every renderer; everything inside is safe to use from several threads.
/// </summary>
internal sealed class FaceAssets
{
    private const string ResourcePrefix = "VuStudio.Rendering.Assets/";

    private static readonly Lazy<FaceAssets> SharedInstance = new(() => new FaceAssets(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<FontLibrary> _fonts = new(() => new FontLibrary(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<IconLibrary> _icons = new(() => new IconLibrary(), LazyThreadSafetyMode.ExecutionAndPublication);

    private FaceAssets()
    {
    }

    public static FaceAssets Shared => SharedInstance.Value;

    public FontLibrary Fonts => _fonts.Value;

    public IconLibrary Icons => _icons.Value;

    /// <summary>Names of every embedded asset (fonts, icons, licenses).</summary>
    public static IEnumerable<string> ResourceNames =>
        typeof(FaceAssets).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal));

    public static Stream OpenResource(string name) =>
        typeof(FaceAssets).Assembly.GetManifestResourceStream(name)
        ?? throw new FileNotFoundException($"Embedded asset '{name}' is missing.", name);
}
