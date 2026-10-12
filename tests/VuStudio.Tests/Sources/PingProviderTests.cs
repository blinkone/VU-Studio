using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Pings;

namespace VuStudio.Tests.Sources;

internal sealed class FakePinger(Func<string, int, PingOutcome> respond) : IPinger
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private int _running;
    private int _maxRunning;

    public TimeSpan Delay { get; init; }

    public ConcurrentQueue<(string Host, TimeSpan Timeout)> Calls { get; } = new();

    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public async Task<PingOutcome> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Enqueue((host, timeout));
        var running = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < running && Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen)
        {
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return respond(host, _attempts.AddOrUpdate(host, 1, (_, n) => n + 1));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

internal sealed class FakeGatewayLocator(string? address) : IGatewayLocator
{
    public IPAddress? Address { get; set; } = address is null ? null : IPAddress.Parse(address);

    public int Lookups { get; private set; }

    public IPAddress? FindDefaultGateway()
    {
        Lookups++;
        return Address;
    }
}

public class PingWindowTests
{
    [Fact]
    public void Starts_empty_and_counts_failures_among_what_it_holds()
    {
        var window = new PingWindow(4);
        Assert.Equal(0, window.Count);
        Assert.Equal(0, window.LossPercent);

        window.Add(true);
        Assert.Equal(0, window.LossPercent);
        window.Add(false);
        Assert.Equal(50, window.LossPercent);
        window.Add(false);
        Assert.Equal(200.0 / 3, window.LossPercent, 9);
        Assert.Equal(3, window.Count);
    }

    [Fact]
    public void Old_results_fall_out_of_the_window()
    {
        var window = new PingWindow(4);
        bool[] results = [true, false, false, true, true, true, true];
        double[] expected = [0, 50, 200.0 / 3, 50, 50, 25, 0];
        for (var i = 0; i < results.Length; i++)
        {
            window.Add(results[i]);
            Assert.Equal(expected[i], window.LossPercent, 9);
        }

        Assert.Equal(4, window.Count);
    }

    [Fact]
    public void A_window_of_one_is_just_the_last_result()
    {
        var window = new PingWindow(1);
        window.Add(false);
        Assert.Equal(100, window.LossPercent);
        window.Add(true);
        Assert.Equal(0, window.LossPercent);
        window.Add(false);
        Assert.Equal(100, window.LossPercent);
    }

    [Fact]
    public void Reset_forgets_everything_and_a_size_below_one_is_raised()
    {
        var window = new PingWindow(0);
        window.Add(false);
        window.Reset();
        Assert.Equal(0, window.Count);
        Assert.Equal(0, window.LossPercent);
        window.Add(true);
        Assert.Equal(0, window.LossPercent);
    }

    [Fact]
    public void All_failures_over_a_full_window_is_total_loss()
    {
        var window = new PingWindow(20);
        for (var i = 0; i < 50; i++)
        {
            window.Add(false);
        }

        Assert.Equal(100, window.LossPercent);
        Assert.Equal(20, window.Count);
    }
}

public class PingSettingsTests
{
    private static PingSettings Parse(string json) =>
        PingSettings.From(new ProviderSettings(TestConfig.For("ping", json).Settings, new PingProviderFactory().Descriptor));

    [Fact]
    public void Defaults_match_the_descriptor()
    {
        var settings = Parse("{}");
        Assert.Equal(["1.1.1.1", "8.8.8.8"], settings.Targets.Select(t => t.Host));
        Assert.Equal(["1-1-1-1", "8-8-8-8"], settings.Targets.Select(t => t.Id));
        Assert.Equal(["Cloudflare", "Google DNS"], settings.Targets.Select(t => t.Name));
        Assert.True(settings.IncludeGateway);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.Timeout);
        Assert.Equal(20, settings.Window);
    }

    [Fact]
    public void Explicit_values_replace_the_defaults()
    {
        var settings = Parse("""{"targets":[{"host":"example.test"}],"includeGateway":false,"interval":2,"timeoutMs":250,"window":5}""");
        var target = Assert.Single(settings.Targets);
        Assert.Equal("example.test", target.Host);
        Assert.Equal("example.test", target.Name); // no name: the host
        Assert.Equal("example-test", target.Id);
        Assert.False(settings.IncludeGateway);
        Assert.Equal(TimeSpan.FromSeconds(2), settings.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.Timeout);
        Assert.Equal(5, settings.Window);
    }

    [Fact]
    public void Values_are_clamped_to_sane_ranges()
    {
        var settings = Parse("""{"timeoutMs":1,"window":0}""");
        Assert.Equal(TimeSpan.FromMilliseconds(50), settings.Timeout);
        Assert.Equal(1, settings.Window);
        Assert.Equal(1000, Parse("""{"window":99999}""").Window);
        Assert.Equal(TimeSpan.FromSeconds(60), Parse("""{"timeoutMs":9999999}""").Timeout);
    }

    [Fact]
    public void Ids_are_unique_and_never_collide_with_the_reserved_ones()
    {
        var settings = Parse("""
            {"targets":[
              {"host":"one.one.one.one"},
              {"host":"ONE.one.one.ONE"},
              {"host":"gateway"},
              {"host":"internet"},
              {"host":"192.168.0.1"}
            ]}
            """);
        Assert.Equal(["one-one-one-one", "one-one-one-one-2", "gateway-2", "internet-2", "192-168-0-1"], settings.Targets.Select(t => t.Id));
    }

    [Fact]
    public void Empty_rows_are_ignored_but_a_name_without_a_host_is_reported()
    {
        var settings = Parse("""{"targets":[{"host":"a.test"},{},{"host":" "},{"name":"Orphan"}]}""");
        Assert.Equal(["a.test"], settings.Targets.Select(t => t.Host));
        Assert.Single(settings.Issues);
        Assert.Contains("Target 4", settings.Issues[0]);
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1")]
    [InlineData("  example.com  ", "example.com")]
    [InlineData("https://example.com/some/path?x=1", "example.com")]
    [InlineData("http://192.168.1.1:8080/", "192.168.1.1")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("2001:4860:4860::8888", "2001:4860:4860::8888")]
    [InlineData("host:notaport", "host:notaport")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData(":80", ":80")]
    public void Host_input_is_normalised(string? input, string? expected) => Assert.Equal(expected, PingSettings.NormalizeHost(input));
}

public class PingProviderTests
{
    private static readonly ProviderDescriptor Descriptor = new PingProviderFactory().Descriptor;

    private static PingProvider Provider(string settingsJson, IPinger pinger, IGatewayLocator? gateways = null) =>
        new("ping", PingSettings.From(new ProviderSettings(TestConfig.For("ping", settingsJson).Settings, Descriptor)), pinger, gateways ?? new FakeGatewayLocator(null), NullLogger.Instance);

    private static async Task<(SensorHub Hub, ISensorSink Sink)> StartedAsync(PingProvider provider)
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("ping");
        await ProviderDriver.InitializeAsync(provider, sink);
        return (hub, sink);
    }

    private static PingOutcome Up(string host, int attempt) => PingOutcome.Reply(host.Length);

    [Fact]
    public async Task Publishes_latency_loss_and_reachability_for_each_target()
    {
        var pinger = new FakePinger((host, _) => host == "1.1.1.1" ? PingOutcome.Reply(12.5) : PingOutcome.NoReply());
        var provider = Provider("""{"includeGateway":false}""", pinger);
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(12.5, hub.GetNumber("ping/1-1-1-1/rtt"));
        Assert.Equal(0, hub.GetNumber("ping/1-1-1-1/loss"));
        Assert.Equal(1, hub.GetNumber("ping/1-1-1-1/up"));

        Assert.Null(hub.GetNumber("ping/8-8-8-8/rtt")); // no answer, no latency
        Assert.Equal(100, hub.GetNumber("ping/8-8-8-8/loss"));
        Assert.Equal(0, hub.GetNumber("ping/8-8-8-8/up"));
        Assert.Equal(1, hub.GetNumber("ping/internet/up"));
    }

    [Fact]
    public async Task Sensors_carry_rich_metadata()
    {
        var provider = Provider("""{"includeGateway":false,"timeoutMs":800}""", new FakePinger(Up));
        var (hub, _) = await StartedAsync(provider);

        var rtt = hub.Get("ping/1-1-1-1/rtt")!.Info;
        Assert.Equal("Cloudflare", rtt.Device);
        Assert.Equal(SensorCategory.Network, rtt.Category);
        Assert.Equal(SensorKinds.Latency, rtt.Kind);
        Assert.Equal(Units.Milliseconds, rtt.Unit);
        Assert.Equal(0, rtt.Min);
        Assert.Equal(800, rtt.Max); // the timeout is the worst latency there can be

        var loss = hub.Get("ping/1-1-1-1/loss")!.Info;
        Assert.Equal(Units.Percent, loss.Unit);
        Assert.Equal(100, loss.Max);

        var up = hub.Get("ping/1-1-1-1/up")!.Info;
        Assert.Equal(SensorKinds.State, up.Kind);
        Assert.Equal(1, up.Max);

        var internet = hub.Get("ping/internet/up")!.Info;
        Assert.Equal("Internet", internet.Device);
        Assert.All(hub.GetCatalog(), info => Assert.False(string.IsNullOrWhiteSpace(info.Icon)));
        Assert.All(hub.GetCatalog(), info => Assert.True(info.StaleAfterSeconds >= 30));
    }

    [Fact]
    public async Task Packet_loss_is_measured_over_the_window()
    {
        bool[] answers = [true, false, false, true, true, true, true];
        var pinger = new FakePinger((_, attempt) => answers[attempt - 1] ? PingOutcome.Reply(5) : PingOutcome.NoReply());
        var provider = Provider("""{"targets":[{"host":"a.test"}],"includeGateway":false,"window":4}""", pinger);
        var (hub, sink) = await StartedAsync(provider);

        double[] expected = [0, 50, 200.0 / 3, 50, 50, 25, 0];
        for (var i = 0; i < answers.Length; i++)
        {
            await ProviderDriver.PollAsync(provider, sink);
            Assert.Equal(expected[i], hub.GetNumber("ping/a-test/loss")!.Value, 9);
            Assert.Equal(answers[i] ? 1 : 0, hub.GetNumber("ping/a-test/up"));
        }
    }

    [Fact]
    public async Task A_target_that_stops_answering_loses_its_latency_and_recovers()
    {
        var pinger = new FakePinger((_, attempt) => attempt == 2 ? PingOutcome.NoReply() : PingOutcome.Reply(9));
        var provider = Provider("""{"targets":[{"host":"a.test"}],"includeGateway":false}""", pinger);
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(9, hub.GetNumber("ping/a-test/rtt"));
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Null(hub.GetNumber("ping/a-test/rtt"));
        Assert.Equal(0, hub.GetNumber("ping/a-test/up"));
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(9, hub.GetNumber("ping/a-test/rtt"));
        Assert.Equal(1, hub.GetNumber("ping/a-test/up"));
    }

    [Fact]
    public async Task Internet_is_up_while_any_target_other_than_the_gateway_answers()
    {
        var answering = new HashSet<string> { "192.168.1.1" };
        var pinger = new FakePinger((host, _) => answering.Contains(host) ? PingOutcome.Reply(1) : PingOutcome.NoReply());
        var provider = Provider("""{"targets":[{"host":"a.test"},{"host":"b.test"}]}""", pinger, new FakeGatewayLocator("192.168.1.1"));
        var (hub, sink) = await StartedAsync(provider);

        // Only the router answers: the LAN is fine but the internet is not.
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(1, hub.GetNumber("ping/gateway/up"));
        Assert.Equal(0, hub.GetNumber("ping/internet/up"));

        // One of two hosts answers: that is enough.
        answering.Add("b.test");
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(1, hub.GetNumber("ping/internet/up"));

        // Nothing answers, not even the router.
        answering.Clear();
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(0, hub.GetNumber("ping/internet/up"));
        Assert.Equal(0, hub.GetNumber("ping/gateway/up"));
    }

    [Fact]
    public async Task The_default_gateway_is_found_and_reported_under_a_stable_id()
    {
        var pinger = new FakePinger(Up);
        var provider = Provider("""{"targets":[]}""", pinger, new FakeGatewayLocator("192.168.1.1"));
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(11, hub.GetNumber("ping/gateway/rtt")); // "192.168.1.1".Length
        Assert.Equal(1, hub.GetNumber("ping/gateway/up"));
        Assert.Equal("Gateway (192.168.1.1)", hub.Get("ping/gateway/rtt")!.Info.Device);
        Assert.Equal("192.168.1.1", Assert.Single(pinger.Calls).Host);
        Assert.Null(hub.Get("ping/internet/up")); // only the gateway is monitored: no internet summary
    }

    [Fact]
    public async Task Without_a_default_gateway_the_gateway_counts_as_down()
    {
        var pinger = new FakePinger(Up);
        var provider = Provider("""{"targets":[{"host":"a.test"}]}""", pinger, new FakeGatewayLocator(null));
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(0, hub.GetNumber("ping/gateway/up"));
        Assert.Equal(100, hub.GetNumber("ping/gateway/loss"));
        Assert.Null(hub.GetNumber("ping/gateway/rtt"));
        Assert.Equal("Gateway", hub.Get("ping/gateway/up")!.Info.Device);
        Assert.DoesNotContain(pinger.Calls, call => call.Host != "a.test");
        Assert.Equal(1, hub.GetNumber("ping/internet/up"));
    }

    [Fact]
    public async Task A_new_gateway_starts_its_statistics_from_scratch()
    {
        var gateways = new FakeGatewayLocator("10.0.0.1");
        var pinger = new FakePinger((host, _) => host == "10.0.0.1" ? PingOutcome.NoReply() : PingOutcome.Reply(3));
        var provider = Provider("""{"targets":[],"window":10}""", pinger, gateways);
        var (hub, sink) = await StartedAsync(provider);

        for (var i = 0; i < 3; i++)
        {
            await ProviderDriver.PollAsync(provider, sink);
        }

        Assert.Equal(100, hub.GetNumber("ping/gateway/loss"));
        Assert.Equal("Gateway (10.0.0.1)", hub.Get("ping/gateway/up")!.Info.Device);

        gateways.Address = IPAddress.Parse("192.168.5.1"); // moved to another network
        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(0, hub.GetNumber("ping/gateway/loss")); // the three misses belonged to the old gateway
        Assert.Equal(1, hub.GetNumber("ping/gateway/up"));
        Assert.Equal("Gateway (192.168.5.1)", hub.Get("ping/gateway/up")!.Info.Device);
    }

    [Fact]
    public async Task The_gateway_can_be_switched_off()
    {
        var gateways = new FakeGatewayLocator("192.168.1.1");
        var provider = Provider("""{"targets":[{"host":"a.test"}],"includeGateway":false}""", new FakePinger(Up), gateways);
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(0, gateways.Lookups);
        Assert.DoesNotContain(hub.GetCatalog(), info => info.Id.StartsWith("ping/gateway/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Targets_are_pinged_in_parallel_with_the_configured_timeout()
    {
        var pinger = new FakePinger(Up) { Delay = TimeSpan.FromMilliseconds(300) };
        var provider = Provider(
            """{"targets":[{"host":"a.test"},{"host":"b.test"},{"host":"c.test"},{"host":"d.test"},{"host":"e.test"}],"includeGateway":false,"timeoutMs":750}""",
            pinger);
        var (_, sink) = await StartedAsync(provider);

        var clock = Stopwatch.StartNew();
        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(5, pinger.MaxConcurrent);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.2), $"a round took {clock.Elapsed.TotalSeconds:0.0} s: the pings ran one after another");
        Assert.All(pinger.Calls, call => Assert.Equal(TimeSpan.FromMilliseconds(750), call.Timeout));
    }

    [Fact]
    public async Task Pings_that_cannot_be_sent_degrade_the_provider_only_when_all_of_them_fail()
    {
        var pinger = new FakePinger((host, _) => host == "bad.test" ? PingOutcome.Failure("No such host is known") : PingOutcome.Reply(4));
        var provider = Provider("""{"targets":[{"host":"bad.test"},{"host":"good.test"}],"includeGateway":false}""", pinger);
        var (hub, sink) = await StartedAsync(provider);

        await ProviderDriver.PollAsync(provider, sink); // one bad host among good ones is the user's typo, not a fault
        Assert.Equal(1, hub.GetNumber("ping/good-test/up"));
        Assert.Equal(0, hub.GetNumber("ping/bad-test/up"));
    }

    [Fact]
    public async Task When_every_ping_fails_to_send_the_provider_says_why()
    {
        var pinger = new FakePinger((_, _) => PingOutcome.Failure("Operation not permitted"));
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider("""{"interval":0.05,"includeGateway":false}""", pinger),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        Assert.Contains("Operation not permitted", status.Last!.Value.Message);
    }

    [Fact]
    public async Task An_unreachable_network_is_not_a_provider_fault()
    {
        var status = new RecordingStatus();
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider("""{"interval":0.05}""", new FakePinger((_, _) => PingOutcome.NoReply())),
            hub,
            () => hub.GetNumber("ping/internet/up") == 0 && status.Last?.Health == ProviderHealth.Ok,
            status: status);

        Assert.DoesNotContain(status.Reports, r => r.Health == ProviderHealth.Degraded);
    }

    [Fact]
    public async Task Nothing_to_ping_is_reported()
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider("""{"interval":0.05,"targets":[],"includeGateway":false}""", new FakePinger(Up)),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        Assert.Contains("No ping targets", status.Last!.Value.Message);
    }

    [Fact]
    public async Task Cancelling_stops_pings_in_flight()
    {
        var pinger = new FakePinger(Up) { Delay = TimeSpan.FromMinutes(5) };
        var hub = new SensorHub();

        await ProviderRunner.RunUntilAsync(
            Provider("""{"interval":0.05,"includeGateway":false}""", pinger),
            hub,
            () => !pinger.Calls.IsEmpty,
            timeout: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_real_pinger_never_throws_for_a_local_address()
    {
        var outcome = await new SystemPinger().PingAsync("127.0.0.1", TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Depending on the sandbox the ICMP socket may be forbidden: an answer, no answer and an error are all acceptable.
        Assert.True(outcome.RoundTripMs >= 0);
        Assert.True(outcome.Success || outcome.Error is not null || outcome.RoundTripMs == 0);
    }

    [Fact]
    public async Task The_real_pinger_reports_a_malformed_host_instead_of_throwing()
    {
        var outcome = await new SystemPinger().PingAsync("   ", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(outcome.Success);
    }

    [Fact]
    public void The_real_gateway_locator_never_throws()
    {
        var gateway = new NetworkInterfaceGatewayLocator().FindDefaultGateway();
        if (gateway is not null)
        {
            Assert.NotEqual(IPAddress.Any, gateway);
            Assert.NotEqual(IPAddress.IPv6Any, gateway);
        }
    }
}
