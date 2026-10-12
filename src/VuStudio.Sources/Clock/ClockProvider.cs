using System.Globalization;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Clock;

/// <summary>
/// Publishes the local time as numbers (hour, minute, second), as text (<c>HH:mm</c>, <c>yyyy-MM-dd</c>),
/// as progress of the day, week, month and year, and as user-defined countdowns.
/// </summary>
/// <remarks>
/// Sensor ids: <c>hour</c>, <c>minute</c>, <c>second</c>, <c>day/progress</c>, <c>week/progress</c>,
/// <c>month/progress</c>, <c>year/progress</c>, <c>time/text</c>, <c>date/text</c>, and for every countdown
/// <c>countdown/{id}/remaining</c> (seconds) and <c>countdown/{id}/percent</c>.
/// </remarks>
internal sealed class ClockProvider : PollingSensorProvider
{
    private const string Device = "Clock";

    private readonly TimeProvider _time;
    private readonly DayOfWeek _weekStart;
    private readonly IReadOnlyList<Countdown> _countdowns;
    private readonly List<string> _issues = [];

    public ClockProvider(string id, ProviderSettings settings, TimeProvider time, ILogger logger)
        : base(id, settings.GetSeconds("interval", 1), logger)
    {
        _time = time;
        _weekStart = string.Equals(settings.GetString("weekStart"), "sunday", StringComparison.OrdinalIgnoreCase)
            ? DayOfWeek.Sunday
            : DayOfWeek.Monday;
        _countdowns = Countdown.Parse(settings, time.LocalTimeZone, _issues);
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var stale = SensorHints.StaleAfterSeconds(Interval);
        Define(sink, "hour", "Hour", "time", Units.None, 0, 23, "clock-outline", stale);
        Define(sink, "minute", "Minute", "time", Units.None, 0, 59, "clock-outline", stale);
        Define(sink, "second", "Second", "time", Units.None, 0, 59, "clock-outline", stale);
        Define(sink, "day/progress", "Day progress", SensorKinds.Percent, Units.Percent, 0, 100, "calendar-today", stale);
        Define(sink, "week/progress", "Week progress", SensorKinds.Percent, Units.Percent, 0, 100, "calendar", stale);
        Define(sink, "month/progress", "Month progress", SensorKinds.Percent, Units.Percent, 0, 100, "calendar", stale);
        Define(sink, "year/progress", "Year progress", SensorKinds.Percent, Units.Percent, 0, 100, "calendar", stale);
        Define(sink, "time/text", "Time", SensorKinds.Text, Units.None, null, null, "clock-digital", stale, text: true);
        Define(sink, "date/text", "Date", SensorKinds.Text, Units.None, null, null, "calendar-today", stale, text: true);

        foreach (var countdown in _countdowns)
        {
            sink.Define(new SensorInfo
            {
                Id = $"countdown/{countdown.Id}/remaining",
                Name = "Remaining time",
                Device = countdown.Name,
                Category = SensorCategory.Time,
                Kind = SensorKinds.Duration,
                Unit = Units.Seconds,
                Min = 0,
                Max = countdown.SpanSeconds,
                Icon = "timer-sand",
                StaleAfterSeconds = stale,
            });
            sink.Define(new SensorInfo
            {
                Id = $"countdown/{countdown.Id}/percent",
                Name = "Remaining (% of span)",
                Device = countdown.Name,
                Category = SensorCategory.Time,
                Kind = SensorKinds.Percent,
                Unit = Units.Percent,
                Min = 0,
                Max = 100,
                Icon = "timer-sand",
                StaleAfterSeconds = stale,
            });
        }

        return Task.CompletedTask;
    }

    protected override Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var utcNow = _time.GetUtcNow();
        var wall = _time.GetLocalNow().DateTime;

        sink.Publish("hour", wall.Hour);
        sink.Publish("minute", wall.Minute);
        sink.Publish("second", wall.Second);
        sink.Publish("day/progress", ClockMath.DayProgress(wall));
        sink.Publish("week/progress", ClockMath.WeekProgress(wall, _weekStart));
        sink.Publish("month/progress", ClockMath.MonthProgress(wall));
        sink.Publish("year/progress", ClockMath.YearProgress(wall));
        sink.PublishText("time/text", wall.ToString("HH:mm", CultureInfo.InvariantCulture));
        sink.PublishText("date/text", wall.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        foreach (var countdown in _countdowns)
        {
            var (remaining, percent) = countdown.Evaluate(utcNow, _time.LocalTimeZone);
            sink.Publish($"countdown/{countdown.Id}/remaining", remaining);
            sink.Publish($"countdown/{countdown.Id}/percent", percent);
        }

        // Rows that could not be understood are skipped, but say so: the provider shows as degraded.
        if (_issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", _issues));
        }

        return Task.CompletedTask;
    }

    private static void Define(
        ISensorSink sink,
        string id,
        string name,
        string kind,
        string unit,
        double? min,
        double? max,
        string icon,
        double stale,
        bool text = false) =>
        sink.Define(new SensorInfo
        {
            Id = id,
            Name = name,
            Device = Device,
            Category = SensorCategory.Time,
            Kind = kind,
            Unit = unit,
            ValueKind = text ? SensorValueKind.Text : SensorValueKind.Number,
            Min = min,
            Max = max,
            Icon = icon,
            StaleAfterSeconds = stale,
        });
}
