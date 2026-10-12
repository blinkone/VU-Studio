using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VuStudio.Sources.Common;

/// <summary>
/// Helpers for <b>local</b> sensor ids (the part after the provider id). Local ids are lowercase,
/// slash-separated and match <c>[a-z0-9][a-z0-9/_.-]*</c>, e.g. <c>cpu/load</c> or <c>net/eth0/rx</c>.
/// </summary>
public static partial class LocalIds
{
    /// <summary>Longest accepted local id.</summary>
    public const int MaxLength = 128;

    private const int MaxSegmentLength = 64;

    // \z, not $: "$" would also accept a trailing newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9/_.-]*\z")]
    private static partial Regex ValidPattern();

    /// <summary>True when <paramref name="id"/> is a well-formed local id.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength } && ValidPattern().IsMatch(id);

    /// <summary>
    /// Turns arbitrary text into one id segment: lowercase ASCII letters and digits separated by single
    /// dashes (<c>"1.1.1.1"</c> becomes <c>"1-1-1-1"</c>, <c>"Wi-Fi 2"</c> becomes <c>"wi-fi-2"</c>).
    /// Diacritics are dropped. Returns <paramref name="fallback"/> when nothing usable remains.
    /// </summary>
    public static string Slug(string? text, string fallback = "item")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var decomposed = Decompose(text);
        var builder = new StringBuilder(decomposed.Length);
        var pendingDash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(lower);
                if (builder.Length >= MaxSegmentLength)
                {
                    break;
                }
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    /// <summary>
    /// Like <see cref="Slug"/> but keeps <c>/</c> as a separator, so <c>"Room/Living Temp"</c> becomes
    /// <c>"room/living-temp"</c>. Empty segments are dropped.
    /// </summary>
    public static string SlugPath(string? text, string fallback = "item")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Slug(s, ""))
            .Where(s => s.Length > 0)
            .ToList();
        if (segments.Count == 0)
        {
            return fallback;
        }

        var path = string.Join('/', segments);
        return path.Length <= MaxLength ? path : path[..MaxLength].TrimEnd('/', '-');
    }

    /// <summary>
    /// Makes <paramref name="slug"/> unique within <paramref name="used"/> by appending <c>-2</c>, <c>-3</c>…
    /// The returned value is added to the set.
    /// </summary>
    public static string Unique(string slug, ISet<string> used)
    {
        if (used.Add(slug))
        {
            return slug;
        }

        for (var n = 2; ; n++)
        {
            var candidate = slug + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Decompose(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormD);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return text;
        }
    }
}
