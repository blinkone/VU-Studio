using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Processes;

namespace VuStudio.Tests.Sources;

internal sealed class FakeProcessSource : IProcessSource
{
    public List<ProcessSample> Current { get; set; } = [];

    public IReadOnlyList<ProcessSample> Snapshot(IReadOnlySet<string> names) =>
        Current.Where(s => names.Contains(s.Name)).ToList();
}

public class ProcessNamesTests
{
    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome.EXE", "chrome")]
    [InlineData("  Spotify.exe ", "spotify")]
    [InlineData("\"C:\\Games\\Elden Ring\\eldenring.exe\"", "eldenring")]
    [InlineData("/usr/bin/firefox", "firefox")]
    [InlineData("'steam'", "steam")]
    [InlineData("My App", "my app")]
    [InlineData("a.exe.exe", "a.exe")]
    [InlineData(".exe", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize(string? input, string? expected) => Assert.Equal(expected, ProcessNames.Normalize(input));
}

public class ProcessProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static ProcessSample Sample(int pid, string name, double? cpuSeconds, long memory = 1000) =>
        new(pid, name, cpuSeconds is { } s ? TimeSpan.FromSeconds(s) : null, memory);

    private static ProcessProvider Provider(string[] names, FakeProcessSource source, ManualTimeProvider time, int cores = 4) =>
        new("process", names, TimeSpan.FromSeconds(1), source, time, cores, NullLogger.Instance);

    private static async Task<(SensorHub Hub, ISensorSink Sink)> StartedAsync(ProcessProvider provider)
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("process");
        await ProviderDriver.InitializeAsync(provider, sink);
        return (hub, sink);
    }

    [Fact]
    public async Task Reports_running_state_instances_and_memory()
    {
        var source = new FakeProcessSource { Current = [Sample(1, "chrome", 1, 100_000_000), Sample(2, "chrome", 2, 50_000_000)] };
        var provider = Provider(["chrome", "spotify"], source, new ManualTimeProvider(Start));
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(1, hub.GetNumber("process/chrome/running"));
        Assert.Equal(2, hub.GetNumber("process/chrome/count"));
        Assert.Equal(150_000_000, hub.GetNumber("process/chrome/memory"));
        Assert.Equal(0, hub.GetNumber("process/spotify/running"));
        Assert.Equal(0, hub.GetNumber("process/spotify/count"));
        Assert.Equal(0, hub.GetNumber("process/spotify/memory"));
        Assert.Null(hub.GetNumber("process/chrome/cpu")); // needs a second sample
    }

    [Fact]
    public async Task Cpu_is_the_change_in_processor_time_over_all_cores()
    {
        var time = new ManualTimeProvider(Start);
        var source = new FakeProcessSource { Current = [Sample(1, "game", 10), Sample(2, "game", 5)] };
        var provider = Provider(["game"], source, time, cores: 4);
        var (hub, sink) = await StartedAsync(provider);
        await ProviderDriver.PollAsync(provider, sink);

        time.Advance(TimeSpan.FromSeconds(2));
        source.Current = [Sample(1, "game", 10.8), Sample(2, "game", 6.0)];
        await ProviderDriver.PollAsync(provider, sink);

        // (0.8 + 1.0) s of CPU in 2 s on 4 cores = 22.5 %
        Assert.Equal(22.5, hub.GetNumber("process/game/cpu")!.Value, 9);
    }

    [Fact]
    public async Task New_instances_and_reused_pids_do_not_cause_spikes()
    {
        var time = new ManualTimeProvider(Start);
        var source = new FakeProcessSource { Current = [Sample(1, "game", 100)] };
        var provider = Provider(["game"], source, time, cores: 1);
        var (hub, sink) = await StartedAsync(provider);
        await ProviderDriver.PollAsync(provider, sink);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Current = [Sample(1, "game", 100.5), Sample(2, "game", 5000)]; // pid 2 has no baseline yet
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(50, hub.GetNumber("process/game/cpu")!.Value, 9);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Current = [Sample(1, "game", 2), Sample(2, "game", 5000.25)]; // pid 1 was reused: total went down
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(25, hub.GetNumber("process/game/cpu")!.Value, 9);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Current = [];
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(0, hub.GetNumber("process/game/cpu"));
        Assert.Equal(0, hub.GetNumber("process/game/running"));
    }

    [Fact]
    public async Task Cpu_is_clamped_to_100_percent()
    {
        var time = new ManualTimeProvider(Start);
        var source = new FakeProcessSource { Current = [Sample(1, "x", 0)] };
        var provider = Provider(["x"], source, time, cores: 2);
        var (hub, sink) = await StartedAsync(provider);
        await ProviderDriver.PollAsync(provider, sink);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Current = [Sample(1, "x", 50)];
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(100, hub.GetNumber("process/x/cpu"));
    }

    [Fact]
    public async Task Instances_whose_cpu_time_cannot_be_read_still_count()
    {
        var time = new ManualTimeProvider(Start);
        var source = new FakeProcessSource { Current = [Sample(1, "protected", null, 777)] };
        var provider = Provider(["protected"], source, time);
        var (hub, sink) = await StartedAsync(provider);
        await ProviderDriver.PollAsync(provider, sink);
        time.Advance(TimeSpan.FromSeconds(1));
        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(1, hub.GetNumber("process/protected/running"));
        Assert.Equal(777, hub.GetNumber("process/protected/memory"));
        Assert.Equal(0, hub.GetNumber("process/protected/cpu"));
    }

    [Fact]
    public async Task Names_are_normalised_deduplicated_and_given_unique_ids()
    {
        var source = new FakeProcessSource { Current = [Sample(1, "chrome", 1), Sample(2, "node.js", 1), Sample(3, "node-js", 1)] };
        var provider = Provider(["Chrome.EXE", "chrome", " C:\\Program Files\\Google\\chrome.exe ", "'Spotify'", "node.js", "node-js", "  "], source, new ManualTimeProvider(Start));
        var (hub, sink) = await StartedAsync(provider);
        await ProviderDriver.PollAsync(provider, sink);

        var ids = hub.GetCatalog().Select(i => i.Id.Split('/')[1]).Distinct().Order().ToList();
        Assert.Equal(["chrome", "node-js", "node-js-2", "spotify"], ids);
        Assert.Equal(1, hub.GetNumber("process/chrome/count"));
        Assert.Equal(1, hub.GetNumber("process/node-js/running"));
        Assert.Equal(1, hub.GetNumber("process/node-js-2/running"));
    }

    [Fact]
    public async Task Defines_rich_metadata()
    {
        var provider = Provider(["Chrome.exe"], new FakeProcessSource(), new ManualTimeProvider(Start));
        var (hub, _) = await StartedAsync(provider);

        var cpu = hub.Get("process/chrome/cpu")!.Info;
        Assert.Equal("chrome", cpu.Device);
        Assert.Equal(SensorCategory.Process, cpu.Category);
        Assert.Equal(Units.Percent, cpu.Unit);
        Assert.Equal(100, cpu.Max);
        Assert.Equal(Units.Bytes, hub.Get("process/chrome/memory")!.Info.Unit);
        Assert.Equal(SensorKinds.State, hub.Get("process/chrome/running")!.Info.Kind);
        Assert.All(hub.GetCatalog(), i => Assert.False(string.IsNullOrWhiteSpace(i.Icon)));
        Assert.Equal(4, hub.Count);
    }

    [Fact]
    public async Task No_names_is_reported()
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            new ProcessProvider("process", [" ", ""], TimeSpan.FromMilliseconds(50), new FakeProcessSource(), TimeProvider.System, 4, NullLogger.Instance),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);
        Assert.Contains("No process names", status.Last!.Value.Message);
    }

    [Fact]
    public void The_real_source_finds_this_very_process()
    {
        using var self = Process.GetCurrentProcess();
        var name = ProcessNames.Normalize(self.ProcessName)!;
        var found = new SystemProcessSource().Snapshot(new HashSet<string> { name });

        var me = Assert.Single(found, s => s.Pid == Environment.ProcessId);
        Assert.Equal(name, me.Name);
        Assert.True(me.WorkingSetBytes > 0);
        Assert.NotNull(me.CpuTime);
    }

    [Fact]
    public async Task Works_end_to_end_through_the_factory()
    {
        using var self = Process.GetCurrentProcess();
        var name = ProcessNames.Normalize(self.ProcessName)!;
        var config = TestConfig.For("process", $$"""{"processes":["{{name}}"],"interval":0.1}""");
        var provider = new ProcessProviderFactory().Create(config, TestServices.Empty());
        var hub = new SensorHub();
        var id = "process/" + VuStudio.Sources.Common.LocalIds.Slug(name);

        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber(id + "/cpu") is not null);

        Assert.Equal(1, hub.GetNumber(id + "/running"));
        Assert.True(hub.GetNumber(id + "/count") >= 1);
        Assert.True(hub.GetNumber(id + "/memory") > 0);
    }
}
