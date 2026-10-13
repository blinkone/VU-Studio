using System.Text.RegularExpressions;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Engine;

/// <summary>Facts the profile conditions are evaluated against.</summary>
public interface IProfileFacts
{
    /// <summary>Lower-case executable names (with extension) currently running.</summary>
    IReadOnlySet<string> RunningProcesses { get; }

    /// <summary>Lower-case executable name of the foreground window's process, if known.</summary>
    string? ForegroundProcess { get; }

    DateTime LocalNow { get; }

    double? Number(string sensorId);
}

/// <summary>
/// Chooses the active profile: a valid manual choice wins; otherwise the highest-priority enabled
/// profile whose activation is <see cref="ProfileActivation.Always"/> or whose conditions hold.
/// Sensor conditions honour their hold time.
/// </summary>
public sealed class ProfileSelector
{
    private readonly Dictionary<SensorCondition, DateTime> _since = new(ReferenceEqualityComparer.Instance);

    public Profile? Select(IReadOnlyList<Profile> profiles, string? manualProfileId, IProfileFacts facts)
    {
        if (manualProfileId is not null &&
            profiles.FirstOrDefault(p => p.Id == manualProfileId && p.Enabled) is { } manual)
        {
            return manual;
        }

        Profile? best = null;
        foreach (var profile in profiles)
        {
            if (!profile.Enabled || profile.Activation == ProfileActivation.Manual)
            {
                continue;
            }

            var eligible = profile.Activation == ProfileActivation.Always || Matches(profile, facts);
            if (eligible && (best is null || profile.Priority > best.Priority))
            {
                best = profile;
            }
        }

        return best ?? profiles.FirstOrDefault(p => p.Enabled);
    }

    public bool Matches(Profile profile, IProfileFacts facts)
    {
        if (profile.Conditions.Count == 0)
        {
            return false;
        }

        var results = profile.Conditions.Select(c => Evaluate(c, facts));
        return profile.Match == ConditionMatch.All ? results.All(r => r) : results.Any(r => r);
    }

    private bool Evaluate(ProfileCondition condition, IProfileFacts facts)
    {
        switch (condition)
        {
            case ProcessRunningCondition running:
                return running.Processes.Any(p => facts.RunningProcesses.Any(r => NameMatches(p, r)));
            case ForegroundAppCondition foreground:
                return facts.ForegroundProcess is { } fg && foreground.Processes.Any(p => NameMatches(p, fg));
            case ScheduleCondition schedule:
                return InSchedule(schedule, facts.LocalNow);
            case SensorCondition sensor:
            {
                var value = facts.Number(sensor.SensorId);
                var holds = value is { } v && (sensor.Comparison == Comparison.Above ? v > sensor.Value : v < sensor.Value);
                if (!holds)
                {
                    _since.Remove(sensor);
                    return false;
                }

                if (!_since.TryGetValue(sensor, out var since))
                {
                    since = facts.LocalNow;
                    _since[sensor] = since;
                }

                return (facts.LocalNow - since).TotalSeconds >= sensor.ForSeconds;
            }

            default:
                return false;
        }
    }

    public static bool InSchedule(ScheduleCondition schedule, DateTime localNow)
    {
        var time = TimeOnly.FromDateTime(localNow);
        if (schedule.From <= schedule.To)
        {
            return schedule.Days.Contains(localNow.DayOfWeek) && time >= schedule.From && time < schedule.To;
        }

        // Wraps past midnight: the late part belongs to "today", the early part to the previous day.
        return time >= schedule.From
            ? schedule.Days.Contains(localNow.DayOfWeek)
            : time < schedule.To && schedule.Days.Contains(localNow.AddDays(-1).DayOfWeek);
    }

    /// <summary>Case-insensitive match with optional '*' wildcards; ".exe" is optional on both sides.</summary>
    public static bool NameMatches(string pattern, string name)
    {
        static string Normalize(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4] : s;
        }

        var p = Normalize(pattern);
        var n = Normalize(name);
        if (p.Length == 0)
        {
            return false;
        }

        if (!p.Contains('*'))
        {
            return p == n;
        }

        var regex = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(n, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
}
