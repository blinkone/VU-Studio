using System.Globalization;
using System.Text.RegularExpressions;

namespace VuStudio.Sources.Common;

/// <summary>
/// Forgiving parsing of numbers printed by scripts and web APIs. Always culture-independent:
/// <c>.</c> is the decimal point, but a single <c>,</c> between digits is accepted as a decimal comma
/// (<c>"42,5"</c>), and grouped forms such as <c>"1,234.5"</c>, <c>"1.234,5"</c> or <c>"1.234.567"</c>
/// are understood. Anything ambiguous or malformed (<c>"1.2.3"</c>, <c>"NaN"</c>) is rejected.
/// </summary>
public static partial class NumberParser
{
    [GeneratedRegex(@"^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?$")]
    private static partial Regex Standard();

    [GeneratedRegex(@"^[-+]?\d+,\d+(?:[eE][-+]?\d+)?$")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"^[-+]?\d{1,3}(?:,\d{3})+(?:\.\d+)?$")]
    private static partial Regex ThousandsComma();

    [GeneratedRegex(@"^[-+]?\d{1,3}(?:\.\d{3})+(?:,\d+)?$")]
    private static partial Regex ThousandsDot();

    [GeneratedRegex(@"[-+]?(?:\d[\d.,]*|[.,]\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex Candidate();

    /// <summary>Parses text that is, as a whole, a finite number. Surrounding white space is ignored.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        string normalized;
        if (Standard().IsMatch(s))
        {
            normalized = s;
        }
        else if (DecimalComma().IsMatch(s))
        {
            normalized = s.Replace(',', '.');
        }
        else if (ThousandsComma().IsMatch(s))
        {
            normalized = s.Replace(",", "");
        }
        else if (ThousandsDot().IsMatch(s))
        {
            normalized = s.Replace(".", "").Replace(',', '.');
        }
        else
        {
            return false;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            return false; // "1e999" overflows to infinity
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as a whole number, or else takes the first number found anywhere in it
    /// (<c>"CPU: 42.5 %"</c> gives 42.5).
    /// </summary>
    public static bool TryFindFirst(string? text, out double value)
    {
        if (TryParse(text, out value))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(text))
        {
            foreach (Match match in Candidate().Matches(text))
            {
                // A trailing '.' or ',' is sentence punctuation, not part of the number.
                if (TryParse(match.Value.TrimEnd('.', ','), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }
}
