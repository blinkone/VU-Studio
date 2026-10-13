using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Sources.Remote;
using VuStudio.Server.Api;
using VuStudio.Server.Hosting;
using VuStudio.Tests.Session;

namespace VuStudio.Tests.Server;

public class RuntimeOptionsTests
{
    private static Func<string, string?> Env(string? port) => name => name == "VUSTUDIO_PORT" ? port : null;

    [Fact]
    public void Port_comes_from_the_command_line_then_from_VUSTUDIO_PORT()
    {
        Assert.Equal(6001, RuntimeOptions.Parse(["--port", "6001"], Env("7002")).Port);
        Assert.Equal(6001, RuntimeOptions.Parse(["--port=6001"], Env("7002")).Port);
        Assert.Equal(7002, RuntimeOptions.Parse([], Env("7002")).Port);
        Assert.Equal(7002, RuntimeOptions.Parse(["--port", "nope"], Env("7002")).Port);
        Assert.Null(RuntimeOptions.Parse([], Env(null)).Port);
        Assert.Null(RuntimeOptions.Parse([], Env("70000")).Port);
        Assert.Null(RuntimeOptions.Parse([], Env("abc")).Port);
    }
}

public class UnknownApiTests
{
    [Fact]
    public async Task Unknown_api_paths_are_404_not_the_spa()
    {
        await using var host = await TestHost.StartAsync(app =>
        {
            app.MapGet("/api/info", () => "info");
            app.MapUnknownApi();
            app.MapFallback(() => Results.Content("<!doctype html>", "text/html"));
        });
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/a/b/c", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/nope", null, ct)).StatusCode);
        Assert.Equal("info", await client.GetStringAsync("/api/info", ct));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/history", ct)).StatusCode);
    }
}

public class AgentDiscoveryEndpointTests
{
    private sealed class FakeDiscovery(params DiscoveredAgent[] agents) : IAgentDiscovery
    {
        public int Calls;

        public string? Failure { get; set; }

        public string? LastFailure => Failure;

        public Task<IReadOnlyList<DiscoveredAgent>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<DiscoveredAgent>>(agents);
        }
    }

    private static DiscoveredAgent Agent(string name, string address, int port) =>
        new($"{name}._vustudio._tcp.local", name, $"http://{address}:{port}", port, [address], "1.2", null, new Dictionary<string, string>());

    private static async Task<(TestHost Host, FakeDiscovery Fake)> StartAsync(FakeDiscovery fake, params ProviderInstanceConfig[] providers)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vustudio-disco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new ConfigStore(Path.Combine(dir, "config.json"), NullLogger<ConfigStore>.Instance);
        store.Update(c => c.Providers.AddRange(providers));
        var host = await TestHost.StartAsync(
            app => Endpoints.MapDiscovery(app.MapGroup("/api")),
            services =>
            {
                services.AddSingleton<IAgentDiscovery>(fake);
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(store);
                services.AddSingleton<AgentDiscoveryService>();
            });
        return (host, fake);
    }

    private static ProviderInstanceConfig AgentProvider(string url) =>
        new() { Id = "nas", Type = "agent", Settings = new JsonObject { ["url"] = url } };

    [Fact]
    public async Task Lists_agents_with_the_documented_shape_and_marks_added_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, fake) = await StartAsync(new FakeDiscovery(Agent("Unraid", "192.168.1.20", 5342), Agent("Deck", "192.168.1.30", 5342)), AgentProvider("http://192.168.1.20:5342"));
        await using var _ = host;
        using var client = new HttpClient { BaseAddress = host.BaseUri };

        var json = JsonNode.Parse(await client.GetStringAsync("/api/discovery/agents", ct))!;
        var agents = json["agents"]!.AsArray();

        Assert.Equal(2, agents.Count);
        var unraid = agents.Single(a => a!["name"]!.GetValue<string>() == "Unraid")!;
        Assert.Equal("192.168.1.20", unraid["host"]!.GetValue<string>());
        Assert.Equal(5342, unraid["port"]!.GetValue<int>());
        Assert.Equal("192.168.1.20", unraid["addresses"]![0]!.GetValue<string>());
        Assert.Equal("1.2", unraid["version"]!.GetValue<string>());
        Assert.True(unraid["alreadyAdded"]!.GetValue<bool>());
        Assert.False(agents.Single(a => a!["name"]!.GetValue<string>() == "Deck")!["alreadyAdded"]!.GetValue<bool>());
        Assert.Null(json["message"]);

        // Cached: a second call within the minimum interval does not browse again.
        await client.GetStringAsync("/api/discovery/agents", ct);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Reports_a_message_when_mdns_cannot_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(new FakeDiscovery { Failure = "no multicast" });
        await using var _ = host;
        using var client = new HttpClient { BaseAddress = host.BaseUri };

        var json = JsonNode.Parse(await client.GetStringAsync("/api/discovery/agents", ct))!;

        Assert.Empty(json["agents"]!.AsArray());
        Assert.Contains("no multicast", json["message"]!.GetValue<string>());
    }
}
