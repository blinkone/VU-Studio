using System.Globalization;
using System.Text.RegularExpressions;

namespace VuStudio.Agent.Unraid;

/// <summary>
/// Parser for the quoted INI files emhttpd writes to /var/local/emhttp (<c>["disk1"]</c> sections and
/// <c>key="value"</c> lines). Keys before the first section are kept under the empty section name.
/// </summary>
internal static partial class EmhttpIni
{
    [GeneratedRegex(@"^\[""?(?<s>[^""\]]+)""?\]$")]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"^(?<k>[A-Za-z0-9_]+)=""(?<v>.*)""$")]
    private static partial Regex KeyPattern();

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string? text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var current = "";
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (SectionPattern().Match(line) is { Success: true } section)
            {
                current = section.Groups["s"].Value;
                continue;
            }

            if (KeyPattern().Match(line) is { Success: true } key)
            {
                if (!sections.TryGetValue(current, out var values))
                {
                    sections[current] = values = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                values[key.Groups["k"].Value] = key.Groups["v"].Value;
            }
        }

        return sections.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.Ordinal);
    }

    public static string? Get(this IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value : null;

    public static double? Number(this IReadOnlyDictionary<string, string> section, string key) =>
        double.TryParse(section.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
