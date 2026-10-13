using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Session;

namespace VuStudio.Tests.Session;

public class ForwardingSinkTests
{
    [Fact]
    public void Forwards_every_call_under_the_provider_id()
    {
        var publisher = new FakePublisher();
        var sink = new ForwardingSink(publisher, "my-script");

        sink.Define(new SensorInfo { Id = "temp", Name = "T" });
        sink.Publish("temp", 21.5);
        sink.PublishText("state", "ok");
        sink.PublishText("state", null);
        sink.Invalidate("temp");
        sink.Remove("temp");
        sink.Clear();

        Assert.Equal("my-script", sink.ProviderId);
        Assert.Equal(
            ["define my-script temp", "publish my-script temp 21.5", "text my-script state ok", "text my-script state <null>", "invalidate my-script temp", "remove my-script temp", "clear my-script"],
            publisher.Calls);
    }

    [Fact]
    public void Status_is_forwarded_and_observed()
    {
        var publisher = new FakePublisher();
        ProviderHealth? seen = null;
        var status = new ForwardingStatus(publisher, "p", (h, _) => seen = h);
        status.Report(ProviderHealth.Degraded, "slow");
        Assert.Equal(("p", ProviderHealth.Degraded, "slow"), publisher.Statuses.Single());
        Assert.Equal(ProviderHealth.Degraded, seen);
    }

    [Fact]
    public void Works_with_the_real_client_buffers()
    {
        var client = new SessionClient(SessionClient.EndpointFor(1));
        var sink = new ForwardingSink(client, "my-script");
        sink.Define(new SensorInfo { Id = "my-script/temp", Name = "T" });
        sink.Publish("temp", 1);
        var text = string.Join("\n", client.TakePendingMessages().Select(System.Text.Encoding.UTF8.GetString));
        Assert.Contains("\"provider\":\"my-script\"", text);
        Assert.Contains("\"id\":\"temp\"", text);
        Assert.Contains("\"values\":{\"temp\":1}", text);
    }
}

public class UserSessionProviderHostTests
{
    private static readonly UserSessionProviderHostOptions Fast = new()
    {
        InitialRestartDelay = TimeSpan.FromMilliseconds(30),
        MaxRestartDelay = TimeSpan.FromMilliseconds(100),
        StopTimeout = TimeSpan.FromSeconds(5),
    };

    private static ProviderInstanceConfig Config(string id, string type = "fake", bool enabled = true, int value = 1) =>
        new() { Id = id, Type = type, Enabled = enabled, Settings = new JsonObject { ["value"] = value } };

    private static UserSessionProviderHost Host(FakePublisher publisher, params IProviderFactory[] factories) =>
        new(publisher, factories, new SessionServices(NullLoggerFactory.Instance), NullLogger.Instance, Fast);

    [Fact]
    public async Task Runs_user_session_providers_through_a_forwarding_sink()
    {
        var publisher = new FakePublisher();
        var factory = new FakeFactory();
        await using var host = Host(publisher, factory);

        await host.ApplyAsync([Config("s1", value: 7)], TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => publisher.Has("publish s1 value 7"), because: "first publish");
        Assert.True(publisher.Has("define s1 value"));
        Assert.Contains(publisher.Statuses, s => s is ("s1", ProviderHealth.Ok, _));
        Assert.Equal(ProviderHealth.Ok, host.GetStatus().Single().Health);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Ignores_service_side_types_unknown_types_and_disabled_or_invalid_instances()
    {
        var publisher = new FakePublisher();
        var serviceSide = new FakeFactory("lhm", runsInUserSession: false);
        var userSide = new FakeFactory();
        await using var host = Host(publisher, serviceSide, userSide);

        await host.ApplyAsync(
            [Config("a", "lhm"), Config("b", "nope"), Config("c", enabled: false), Config("Bad Id"), Config("session")],
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, serviceSide.Created);
        Assert.Equal(0, userSide.Created);
        Assert.Equal(["fake"], host.SupportedTypes);
        Assert.Contains(publisher.Statuses, s => s is ("b", ProviderHealth.Unavailable, _) || s is ("a", ProviderHealth.Unavailable, _));
        Assert.DoesNotContain(publisher.Calls, c => c.StartsWith("define", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stops_removed_instances_and_restarts_changed_ones()
    {
        var publisher = new FakePublisher();
        var factory = new FakeFactory();
        await using var host = Host(publisher, factory);

        await host.ApplyAsync([Config("s1", value: 1), Config("s2", value: 2)], TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => publisher.Has("publish s1 value 1") && publisher.Has("publish s2 value 2"));

        // Same configuration: nothing restarts.
        await host.ApplyAsync([Config("s1", value: 1), Config("s2", value: 2)], TestContext.Current.CancellationToken);
        Assert.Equal(2, factory.Created);

        // s1 changed, s2 removed.
        await host.ApplyAsync([Config("s1", value: 5)], TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => publisher.Has("publish s1 value 5"), because: "restart with new settings");
        Assert.True(publisher.Has("clear s2"));
        Assert.True(publisher.Has("clear s1"));
        Assert.Equal(3, factory.Created);
        Assert.Equal(2, factory.Disposed);
        Assert.Equal(["s1"], host.GetStatus().Select(s => s.Id));

        await host.StopAllAsync();
        Assert.Equal(3, factory.Disposed);
    }

    [Fact]
    public async Task Restarts_crashing_providers_with_backoff()
    {
        var publisher = new FakePublisher();
        var factory = new FakeFactory { CrashTimes = 3 };
        await using var host = Host(publisher, factory);

        await host.ApplyAsync([Config("s1", value: 9)], TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => publisher.Has("publish s1 value 9"), because: "eventual success");

        Assert.Equal(4, factory.Created);
        Assert.Equal(3, publisher.Statuses.Count(s => s.Provider == "s1" && s.Health == ProviderHealth.Error));
        Assert.Equal(3, host.GetStatus().Single().Restarts);
    }

    [Fact]
    public void Discovers_the_script_provider_from_the_sources_assembly()
    {
        var factories = UserSessionProviderHost.DiscoverFactories();
        Assert.Contains(factories, f => f.Descriptor.Type == "script");
        Assert.All(factories, f => Assert.True(f.Descriptor.RunsInUserSession));
    }

    private sealed class FakeFactory(string type = "fake", bool runsInUserSession = true) : IProviderFactory
    {
        private int _created;
        private int _disposed;

        public int CrashTimes { get; init; }

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public ProviderDescriptor Descriptor { get; } = new()
        {
            Type = type,
            Name = "Fake",
            RunsInUserSession = runsInUserSession,
            Platforms = PlatformSupport.Any,
        };

        public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
        {
            Assert.NotNull(services.GetService(typeof(TimeProvider)));
            var n = Interlocked.Increment(ref _created);
            return new FakeProvider(this, config.Id, config.Settings["value"]!.GetValue<int>(), crash: n <= CrashTimes);
        }

        private sealed class FakeProvider(FakeFactory owner, string id, int value, bool crash) : ISensorProvider
        {
            public string Id => id;

            public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
            {
                if (crash)
                {
                    throw new InvalidOperationException("crash");
                }

                sink.Define(new SensorInfo { Id = "value", Name = "Value" });
                sink.Publish("value", value);
                status.Report(ProviderHealth.Ok);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._disposed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
