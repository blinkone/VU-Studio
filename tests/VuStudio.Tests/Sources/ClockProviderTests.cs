using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Clock;

namespace VuStudio.Tests.Sources;

public class ClockMathTests
{
    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(6, 0, 25)]
    [InlineData(12, 0, 50)]
    [InlineData(18, 0, 75)]
    [InlineData(23, 59, 99.93055555)]
    public void Day_progress(int hour, int minute, double expected) =>
        Assert.Equal(expected, ClockMath.DayProgress(At(2026, 10, 12, hour, minute)), 6);

    [Fact]
    public void Week_progress_starts_on_the_chosen_day()
    {
        var mondayNoon = At(2026, 10, 12, 12);
        var sundayNoon = At(2026, 10, 11, 12);

        Assert.Equal(0.5 / 7 * 100, ClockMath.WeekProgress(mondayNoon, DayOfWeek.Monday), 6);
        Assert.Equal(6.5 / 7 * 100, ClockMath.WeekProgress(sundayNoon, DayOfWeek.Monday), 6);
        Assert.Equal(1.5 / 7 * 100, ClockMath.WeekProgress(mondayNoon, DayOfWeek.Sunday), 6);
        Assert.Equal(0.5 / 7 * 100, ClockMath.WeekProgress(sundayNoon, DayOfWeek.Sunday), 6);
        Assert.Equal(0, ClockMath.WeekProgress(At(2026, 10, 12), DayOfWeek.Monday), 6);
    }

    [Fact]
    public void Month_progress_respects_month_length()
    {
        Assert.Equal(0, ClockMath.MonthProgress(At(2026, 10, 1)), 6);
        Assert.Equal(11.5 / 31 * 100, ClockMath.MonthProgress(At(2026, 10, 12, 12)), 6);
        Assert.Equal(14.0 / 28 * 100, ClockMath.MonthProgress(At(2026, 2, 15)), 6);
        Assert.Equal(14.0 / 29 * 100, ClockMath.MonthProgress(At(2028, 2, 15)), 6);
    }

    [Fact]
    public void Year_progress_respects_leap_years()
    {
        Assert.Equal(0, ClockMath.YearProgress(At(2026, 1, 1)), 6);
        Assert.Equal(284.5 / 365 * 100, ClockMath.YearProgress(At(2026, 10, 12, 12)), 6);
        Assert.Equal(364.5 / 365 * 100, ClockMath.YearProgress(At(2026, 12, 31, 12)), 6);
        Assert.Equal(365.5 / 366 * 100, ClockMath.YearProgress(At(2028, 12, 31, 12)), 6);
    }
}

public class ClockProviderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SensorHub> RunAsync(
        ManualTimeProvider time,
        string settingsJson = "{}",
        RecordingStatus? status = null,
        Func<SensorHub, bool>? until = null)
    {
        var hub = new SensorHub(time);
        var services = TestServices.With(s => s.AddSingleton<TimeProvider>(time));
        var provider = new ClockProviderFactory().Create(TestConfig.For("clock", settingsJson), services);
        until ??= h => h.GetText("clock/date/text") is not null;
        await ProviderRunner.RunUntilAsync(provider, hub, () => until(hub), status: status);
        return hub;
    }

    [Fact]
    public async Task Publishes_the_local_time_as_numbers_and_text()
    {
        var hub = await RunAsync(new ManualTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 7, 35, TimeSpan.Zero)));

        Assert.Equal(10, hub.GetNumber("clock/hour"));
        Assert.Equal(7, hub.GetNumber("clock/minute"));
        Assert.Equal(35, hub.GetNumber("clock/second"));
        Assert.Equal("10:07", hub.GetText("clock/time/text"));
        Assert.Equal("2026-10-12", hub.GetText("clock/date/text"));
        Assert.Equal(36_455.0 / 86_400 * 100, hub.GetNumber("clock/day/progress")!.Value, 6);
    }

    [Fact]
    public async Task Uses_the_local_time_zone_not_utc()
    {
        // 22:30 UTC is 00:30 the next day at UTC+2.
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 12, 22, 30, 0, TimeSpan.Zero), ManualTimeProvider.FixedZone(2));
        var hub = await RunAsync(time);

        Assert.Equal(0, hub.GetNumber("clock/hour"));
        Assert.Equal(30, hub.GetNumber("clock/minute"));
        Assert.Equal("00:30", hub.GetText("clock/time/text"));
        Assert.Equal("2026-10-13", hub.GetText("clock/date/text"));
    }

    [Fact]
    public async Task Publishes_calendar_progress()
    {
        var hub = await RunAsync(new ManualTimeProvider(Noon));

        Assert.Equal(50, hub.GetNumber("clock/day/progress")!.Value, 6);
        Assert.Equal(0.5 / 7 * 100, hub.GetNumber("clock/week/progress")!.Value, 6);
        Assert.Equal(11.5 / 31 * 100, hub.GetNumber("clock/month/progress")!.Value, 6);
        Assert.Equal(284.5 / 365 * 100, hub.GetNumber("clock/year/progress")!.Value, 6);
    }

    [Fact]
    public async Task Week_start_setting_moves_the_week()
    {
        var sundayNoon = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        var monday = await RunAsync(new ManualTimeProvider(sundayNoon), """{"weekStart":"monday"}""");
        var sunday = await RunAsync(new ManualTimeProvider(sundayNoon), """{"weekStart":"sunday"}""");

        Assert.Equal(6.5 / 7 * 100, monday.GetNumber("clock/week/progress")!.Value, 6);
        Assert.Equal(0.5 / 7 * 100, sunday.GetNumber("clock/week/progress")!.Value, 6);
    }

    [Fact]
    public async Task Defines_rich_metadata()
    {
        var hub = await RunAsync(new ManualTimeProvider(Noon));

        var hour = hub.Get("clock/hour")!.Info;
        Assert.Equal(SensorCategory.Time, hour.Category);
        Assert.Equal(0, hour.Min);
        Assert.Equal(23, hour.Max);
        Assert.Equal("Clock", hour.Device);
        Assert.False(string.IsNullOrWhiteSpace(hour.Icon));

        var progress = hub.Get("clock/week/progress")!.Info;
        Assert.Equal(Units.Percent, progress.Unit);
        Assert.Equal(SensorKinds.Percent, progress.Kind);
        Assert.Equal(0, progress.Min);
        Assert.Equal(100, progress.Max);

        Assert.Equal(SensorValueKind.Text, hub.Get("clock/time/text")!.Info.ValueKind);
        Assert.Equal(SensorValueKind.Text, hub.Get("clock/date/text")!.Info.ValueKind);

        // hour, minute, second, four progress sensors and two text sensors.
        Assert.Equal(9, hub.GetCatalog().Count);
    }

    [Fact]
    public async Task Countdowns_to_a_time_of_day_use_the_next_occurrence()
    {
        var json = """
            {"countdowns":[
              {"id":"evening","name":"Evening","target":"18:30"},
              {"id":"passed","name":"Passed","target":"11:00","span":12}
            ]}
            """;
        var hub = await RunAsync(new ManualTimeProvider(Noon), json, until: h => h.GetNumber("clock/countdown/passed/percent") is not null);

        Assert.Equal(6.5 * 3600, hub.GetNumber("clock/countdown/evening/remaining"));
        Assert.Equal(6.5 / 24 * 100, hub.GetNumber("clock/countdown/evening/percent")!.Value, 6);

        // 11:00 already passed today, so the next one is tomorrow: 23 h away, clamped to the 12 h span.
        Assert.Equal(23 * 3600, hub.GetNumber("clock/countdown/passed/remaining"));
        Assert.Equal(100, hub.GetNumber("clock/countdown/passed/percent"));

        var info = hub.Get("clock/countdown/evening/remaining")!.Info;
        Assert.Equal("Evening", info.Device);
        Assert.Equal(Units.Seconds, info.Unit);
        Assert.Equal(24 * 3600, info.Max);
    }

    [Fact]
    public async Task Countdowns_to_a_date_and_time_count_down_to_zero()
    {
        var json = """
            {"countdowns":[
              {"id":"soon","target":"2026-10-12T12:30:00","span":1},
              {"id":"offset","target":"2026-10-12T16:00:00+02:00","span":4},
              {"id":"utc","target":"2026-10-12T13:00:00Z","span":4},
              {"id":"past","target":"2020-01-01T00:00:00Z"},
              {"id":"day","target":"2026-10-13","span":48}
            ]}
            """;
        var hub = await RunAsync(new ManualTimeProvider(Noon), json, until: h => h.GetNumber("clock/countdown/day/percent") is not null);

        Assert.Equal(1800, hub.GetNumber("clock/countdown/soon/remaining"));
        Assert.Equal(50, hub.GetNumber("clock/countdown/soon/percent")!.Value, 6);
        Assert.Equal(2 * 3600, hub.GetNumber("clock/countdown/offset/remaining"));
        Assert.Equal(50, hub.GetNumber("clock/countdown/offset/percent")!.Value, 6);
        Assert.Equal(3600, hub.GetNumber("clock/countdown/utc/remaining"));
        Assert.Equal(0, hub.GetNumber("clock/countdown/past/remaining"));
        Assert.Equal(0, hub.GetNumber("clock/countdown/past/percent"));
        Assert.Equal(12 * 3600, hub.GetNumber("clock/countdown/day/remaining"));
        Assert.Equal(25, hub.GetNumber("clock/countdown/day/percent")!.Value, 6);
    }

    [Fact]
    public async Task Countdown_targets_without_an_offset_are_local_time()
    {
        // 10:00 UTC is 12:00 at UTC+2, so 18:30 local is 6.5 h away, and so is 2026-10-12T18:30 local.
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero), ManualTimeProvider.FixedZone(2));
        var json = """{"countdowns":[{"id":"a","target":"18:30"},{"id":"b","target":"2026-10-12T18:30:00"}]}""";
        var hub = await RunAsync(time, json, until: h => h.GetNumber("clock/countdown/b/percent") is not null);

        Assert.Equal(6.5 * 3600, hub.GetNumber("clock/countdown/a/remaining"));
        Assert.Equal(6.5 * 3600, hub.GetNumber("clock/countdown/b/remaining"));
    }

    [Fact]
    public async Task Countdown_ids_are_normalised_and_made_unique()
    {
        var json = """
            {"countdowns":[
              {"id":"Big Day!","target":"18:30"},
              {"id":"big day","target":"19:30"},
              {"name":"Only a name","target":"20:30"}
            ]}
            """;
        var hub = await RunAsync(new ManualTimeProvider(Noon), json, until: h => h.GetNumber("clock/countdown/only-a-name/percent") is not null);

        Assert.NotNull(hub.Get("clock/countdown/big-day/remaining"));
        Assert.NotNull(hub.Get("clock/countdown/big-day-2/remaining"));
        Assert.Equal("Only a name", hub.Get("clock/countdown/only-a-name/remaining")!.Info.Device);
    }

    [Fact]
    public async Task Unusable_countdown_rows_are_skipped_and_reported()
    {
        var json = """
            {"countdowns":[
              {"id":"ok","target":"18:30"},
              {"id":"bad","target":"someday"},
              {"id":"empty"}
            ]}
            """;
        var status = new RecordingStatus();
        var hub = await RunAsync(
            new ManualTimeProvider(Noon),
            json,
            status,
            until: h => h.GetNumber("clock/countdown/ok/remaining") is not null && status.Last?.Health == ProviderHealth.Degraded);

        Assert.Null(hub.Get("clock/countdown/bad/remaining"));
        Assert.NotNull(hub.GetNumber("clock/hour")); // the rest keeps working
        var (health, message) = status.Last!.Value;
        Assert.Equal(ProviderHealth.Degraded, health);
        Assert.Contains("Countdown 2", message);
        Assert.Contains("someday", message);
        Assert.Contains("Countdown 3", message);
    }

    [Fact]
    public async Task Reports_ok_when_everything_is_fine()
    {
        var status = new RecordingStatus();
        await RunAsync(new ManualTimeProvider(Noon), status: status, until: h => status.Last?.Health == ProviderHealth.Ok);
        Assert.Equal(ProviderHealth.Ok, status.Last!.Value.Health);
    }

    [Fact]
    public async Task Keeps_publishing_on_every_interval()
    {
        var time = new ManualTimeProvider(Noon);
        var hub = new SensorHub(time);
        var provider = new ClockProviderFactory().Create(
            TestConfig.For("clock", """{"interval":0.1}"""),
            TestServices.With(s => s.AddSingleton<TimeProvider>(time)));

        var versions = new HashSet<long>();
        await ProviderRunner.RunUntilAsync(provider, hub, () =>
        {
            var snapshot = hub.Get("clock/second");
            if (snapshot is not null)
            {
                versions.Add(snapshot.Version);
            }

            return versions.Count >= 3;
        });

        Assert.True(versions.Count >= 3);
    }
}
