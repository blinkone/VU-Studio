using VuStudio.Core.Providers;

namespace VuStudio.Sources.Common;

/// <summary>Shorthand for the English/Italian strings used by provider descriptors.</summary>
internal static class Localized
{
    public static LocalizedText L(string en, string it) => new(en, it);
}
