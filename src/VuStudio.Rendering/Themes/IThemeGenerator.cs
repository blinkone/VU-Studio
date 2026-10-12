using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes;

/// <summary>
/// Produces a face document from a page's <see cref="FaceConfig"/> and render context. Generated text layers
/// use templates (<c>{title}</c>, <c>{value}</c>, <c>{unit}</c>…) so one document stays valid while values change.
/// </summary>
public interface IThemeGenerator
{
    /// <summary>Id ("family/variant"), localized name, description and theme-specific options.</summary>
    ThemeInfo Info { get; }

    FaceDocument Generate(FaceConfig config, FaceRenderContext context);
}
