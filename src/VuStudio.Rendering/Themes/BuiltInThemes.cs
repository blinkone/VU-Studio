using VuStudio.Rendering.Themes.Hud;
using VuStudio.Rendering.Themes.Instrument;
using VuStudio.Rendering.Themes.Minimal;
using VuStudio.Rendering.Themes.Retro;
using VuStudio.Rendering.Themes.Vintage;

namespace VuStudio.Rendering.Themes;

/// <summary>The themes shipped with VU Studio, in the order the UI lists them.</summary>
public static class BuiltInThemes
{
    private static readonly Lazy<IReadOnlyList<IThemeGenerator>> Lazy = new(() =>
    [
        new ClassicTheme(),
        new VuMeterTheme(),
        new ArcTheme(),
        new NumberTheme(),
        new GlanceTheme(),
        new GaugeTheme(),
        new AviationTheme(),
        new FrameTheme(),
        new GridTheme(),
        new PixelTheme(),
    ]);

    public static IReadOnlyList<IThemeGenerator> All => Lazy.Value;
}
