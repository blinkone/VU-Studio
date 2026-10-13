using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VuStudio.Sources.Remote;

/// <summary>One sample line of the Prometheus text exposition format.</summary>
internal sealed record PromSample(string Name, IReadOnlyList<KeyValuePair<string, string>> Labels, double Value)
{
    public string? Label(string name)
    {
        foreach (var (key, value) in Labels)
        {
            if (key == name)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Label values (without <c>__name__</c>) joined by a space; used to name the sensors of multi-series metrics.</summary>
    public string LabelText() => string.Join(' ', Labels.Where(l => l.Key != "__name__").Select(l => l.Value));
}

/// <summary>The samples of one scrape plus the declared metric types.</summary>
internal sealed class PromScrape
{
    public List<PromSample> Samples { get; } = [];

    /// <summary>Metric family name to type (<c>counter</c>, <c>gauge</c>, …) from <c># TYPE</c> lines.</summary>
    public Dictionary<string, string> Types { get; } = new(StringComparer.Ordinal);

    public IEnumerable<PromSample> Named(string name) => Samples.Where(s => s.Name == name);

    public bool Has(string name) => Samples.Any(s => s.Name == name);

    public double? First(string name) => Samples.FirstOrDefault(s => s.Name == name && !double.IsNaN(s.Value))?.Value;

    /// <summary>True when the family of <paramref name="sampleName"/> was declared a counter (handles the <c>_total</c> suffix of OpenMetrics).</summary>
    public bool IsCounter(string sampleName) =>
        Types.TryGetValue(sampleName, out var t) ? t == "counter"
        : sampleName.EndsWith("_total", StringComparison.Ordinal) && Types.TryGetValue(sampleName[..^6], out var t2) && t2 == "counter";
}

/// <summary>
/// A minimal, allocation-conscious parser of the Prometheus text format (version 0.0.4) and the sample lines of
/// OpenMetrics. Handles quoted label names (LibreHardwareMonitor), escapes in label values, <c>+Inf</c>/<c>NaN</c>,
/// timestamps (ignored) and blanks before <c>{</c>. Quoted UTF-8 metric names are skipped.
/// </summary>
internal static class PrometheusText
{
    public static PromScrape Parse(string text, Func<string, bool>? wantName = null)
    {
        var scrape = new PromScrape();
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line[0] == '#')
            {
                ParseComment(line, scrape);
                if (line.StartsWith("# EOF"))
                {
                    break;
                }

                continue;
            }

            var sample = ParseSample(line, wantName);
            if (sample is not null)
            {
                scrape.Samples.Add(sample);
            }
        }

        return scrape;
    }

    private static void ParseComment(ReadOnlySpan<char> line, PromScrape scrape)
    {
        var rest = line[1..].TrimStart();
        if (!rest.StartsWith("TYPE ", StringComparison.Ordinal))
        {
            return;
        }

        var parts = rest[5..].Trim().ToString().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            scrape.Types[parts[0]] = parts[1].ToLowerInvariant();
        }
    }

    private static PromSample? ParseSample(ReadOnlySpan<char> line, Func<string, bool>? wantName)
    {
        var i = 0;
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] is '_' or ':'))
        {
            i++;
        }

        if (i == 0)
        {
            return null;
        }

        var name = line[..i].ToString();
        if (wantName is not null && !wantName(name))
        {
            return null;
        }

        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        var labels = new List<KeyValuePair<string, string>>();
        if (i < line.Length && line[i] == '{')
        {
            i++;
            while (i < line.Length && line[i] != '}')
            {
                while (i < line.Length && line[i] is ' ' or ',' or '\t')
                {
                    i++;
                }

                if (i >= line.Length || line[i] == '}')
                {
                    break;
                }

                var quotedName = line[i] == '"';
                int keyStart;
                if (quotedName)
                {
                    i++;
                    keyStart = i;
                    while (i < line.Length && line[i] != '"')
                    {
                        i++;
                    }
                }
                else
                {
                    keyStart = i;
                    while (i < line.Length && line[i] != '=')
                    {
                        i++;
                    }
                }

                var key = line[keyStart..Math.Min(i, line.Length)].Trim().ToString();
                if (quotedName)
                {
                    i++;
                }

                while (i < line.Length && line[i] != '"')
                {
                    i++;
                }

                i++; // opening quote of the value
                var value = new StringBuilder();
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        value.Append(line[i] == 'n' ? '\n' : line[i]);
                    }
                    else
                    {
                        value.Append(line[i]);
                    }

                    i++;
                }

                i++; // closing quote
                labels.Add(new KeyValuePair<string, string>(key, value.ToString()));
            }

            i++; // '}'
        }

        if (i > line.Length)
        {
            return null;
        }

        var rest = line[i..].Trim();
        var space = rest.IndexOfAny(' ', '\t');
        var number = (space < 0 ? rest : rest[..space]).ToString();
        return TryParseValue(number, out var parsed) ? new PromSample(name, labels, parsed) : null;
    }

    public static bool TryParseValue(string text, out double value)
    {
        switch (text)
        {
            case "+Inf" or "Inf":
                value = double.PositiveInfinity;
                return true;
            case "-Inf":
                value = double.NegativeInfinity;
                return true;
            case "NaN" or "nan":
                value = double.NaN;
                return true;
            default:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}

/// <summary>A label filter such as <c>mode="idle",cpu=~"0|1",device!="lo"</c> (PromQL matcher syntax; quotes optional for <c>=</c>).</summary>
internal sealed class LabelMatcher
{
    private static readonly Regex Pattern = new(@"\s*([A-Za-z_][A-Za-z0-9_]*)\s*(=~|!~|!=|=)\s*(?:""((?:[^""\\]|\\.)*)""|([^,\s]*))\s*(?:,|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<(string Name, bool Negate, Regex? Regex, string Value)> _rules = [];

    public static LabelMatcher Parse(string? text)
    {
        var matcher = new LabelMatcher();
        if (string.IsNullOrWhiteSpace(text))
        {
            return matcher;
        }

        var body = text.Trim().Trim('{', '}');
        foreach (Match m in Pattern.Matches(body))
        {
            var op = m.Groups[2].Value;
            var value = m.Groups[3].Success ? Regex.Unescape(m.Groups[3].Value) : m.Groups[4].Value;
            Regex? regex = null;
            if (op is "=~" or "!~")
            {
                try
                {
                    regex = new Regex("^(?:" + value + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }

            matcher._rules.Add((m.Groups[1].Value, op is "!=" or "!~", regex, value));
        }

        return matcher;
    }

    public bool IsEmpty => _rules.Count == 0;

    public bool Matches(PromSample sample)
    {
        foreach (var (name, negate, regex, value) in _rules)
        {
            var actual = sample.Label(name) ?? "";
            var hit = regex is not null ? regex.IsMatch(actual) : actual == value;
            if (hit == negate)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Per-series counter state: turns counters into deltas, treating a decrease as a reset (same rule as PromQL <c>rate()</c>).</summary>
internal sealed class CounterStore
{
    private readonly Dictionary<string, (double Value, double Seconds)> _last = new(StringComparer.Ordinal);

    /// <summary>Samples closer than this are too noisy to give a rate; the previous baseline is kept.</summary>
    public const double MinWindowSeconds = 0.5;

    /// <summary>The increase since the previous sample and the elapsed seconds, or <c>null</c> for the first sample or a window that is too short.</summary>
    public (double Delta, double Seconds)? Delta(string key, double value, double nowSeconds)
    {
        if (!_last.TryGetValue(key, out var previous))
        {
            _last[key] = (value, nowSeconds);
            return null;
        }

        var seconds = nowSeconds - previous.Seconds;
        if (seconds < MinWindowSeconds)
        {
            return null;
        }

        _last[key] = (value, nowSeconds);
        var delta = value >= previous.Value ? value - previous.Value : value; // reset: counted from zero
        return (delta, seconds);
    }

    public double? Rate(string key, double value, double nowSeconds) => Delta(key, value, nowSeconds) is { } d ? d.Delta / d.Seconds : null;

    public void Clear() => _last.Clear();
}
