using System.Globalization;
using System.Text.RegularExpressions;
using VuStudio.Core.Providers;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Clock;

/// <summary>
/// One row of the clock's "countdowns" table. The target is either an absolute date and time
/// (<c>2026-12-25T18:00</c>, optionally with an offset or <c>Z</c>) or a time of day (<c>18:30</c>) that
/// repeats daily and always refers to the next occurrence.
/// </summary>
internal sealed partial class Countdown
{
    /// <summary>Span used for the percent sensor when the row does not give a usable one.</summary>
    public const double DefaultSpanHours = 24;

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"];

    private readonly DateTimeOffset? _absolute;
    private readonly TimeOnly? _daily;

    private Countdown(string id, string name, DateTimeOffset? absolute, TimeOnly? daily, double spanSeconds)
    {
        Id = id;
        Name = name;
        _absolute = absolute;
        _daily = daily;
        SpanSeconds = spanSeconds;
    }

    /// <summary>Local sensor id segment.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>Length of the window, in seconds, over which the percent goes from 100 to 0.</summary>
    public double SpanSeconds { get; }

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:?\d{2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();

    /// <summary>Reads the "countdowns" table; unusable rows are skipped and described in <paramref name="issues"/>.</summary>
    public static IReadOnlyList<Countdown> Parse(ProviderSettings settings, TimeZoneInfo zone, ICollection<string> issues)
    {
        var result = new List<Countdown>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rowNumber = 0;
        foreach (var row in settings.GetTable("countdowns"))
        {
            rowNumber++;
            var targetText = row.GetString("target");
            if (targetText is null)
            {
                issues.Add($"Countdown {rowNumber}: the target is empty.");
                continue;
            }

            if (!TryParseTarget(targetText, zone, out var absolute, out var daily))
            {
                issues.Add($"Countdown {rowNumber}: '{targetText}' is neither a date and time nor an HH:mm time.");
                continue;
            }

            var name = row.GetString("name");
            var id = LocalIds.Unique(LocalIds.Slug(row.GetString("id") ?? name ?? targetText, "countdown"), used);
            var hours = row.GetDouble("span", DefaultSpanHours);
            if (!double.IsFinite(hours) || hours <= 0)
            {
                hours = DefaultSpanHours;
            }

            result.Add(new Countdown(id, name ?? id, absolute, daily, hours * 3600));
        }

        return result;
    }

    /// <summary>Parses a target. Exposed for tests and for providers that offer the same syntax.</summary>
    public static bool TryParseTarget(string text, TimeZoneInfo zone, out DateTimeOffset? absolute, out TimeOnly? daily)
    {
        absolute = null;
        daily = null;
        var s = text.Trim();
        if (TimeOnly.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            daily = time;
            return true;
        }

        if (HasOffset().IsMatch(s))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
            {
                absolute = withOffset;
                return true;
            }

            return false;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var wallClock))
        {
            var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            absolute = new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
            return true;
        }

        return false;
    }

    /// <summary>Seconds left until the target and that amount as a percentage of <see cref="SpanSeconds"/> (both clamped at 0).</summary>
    public (double RemainingSeconds, double Percent) Evaluate(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        double remaining;
        if (_absolute is { } target)
        {
            remaining = Math.Max(0, (target - utcNow).TotalSeconds);
        }
        else
        {
            var wall = TimeZoneInfo.ConvertTime(utcNow, zone).DateTime;
            var next = wall.Date + _daily!.Value.ToTimeSpan();
            if (next <= wall)
            {
                next = next.AddDays(1);
            }

            remaining = (next - wall).TotalSeconds;
        }

        return (remaining, Math.Clamp(remaining / SpanSeconds * 100, 0, 100));
    }
}
