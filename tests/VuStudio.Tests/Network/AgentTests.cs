using System.Net;
using Makaretu.Dns;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Remote;

namespace VuStudio.Tests.Network;

public class AgentTests
{
    private const string Sensors = """
        {"name":"unraid","version":"0.1.0","time":"2026-10-12T12:00:00Z","catalogVersion":7,
         "sensors":[
           {"id":"system/cpu/load","name":"CPU load","device":"CPU","category":"cpu","kind":"load","unit":"%","valueKind":"number","min":0,"max":100,"icon":"cpu-64-bit"},
           {"id":"unraid/disk/disk1/temp","name":"Disk 1 temperature","category":"storage","unit":"°C","valueKind":"number","min":20,"max":70},
           {"id":"system/os","name":"OS","category":"system","valueKind":"text"}],
         "values":{"system/cpu/load":42.5,"unraid/disk/disk1/temp":null,"system/os":"Unraid 7.2"}}
        """;

    private static Harness Make(FakeHttp http, string extra = "") =>
        Net.Make(new AgentProviderFactory(), $$"""{"url":"unraid.local:5342","token":"secret"{{extra}}}""", http);

    [Fact]
    public async Task Publishes_the_catalog_with_host_and_values()
    {
        var http = new FakeHttp().Route("/api/v1/sensors", Sensors);
        var h = Make(http);

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(42.5, h.Number("system/cpu/load"));
        Assert.Null(h.Number("unraid/disk/disk1/temp"));
        Assert.Equal("Unraid 7.2", h.Text("system/os"));
        var info = h.Info("system/cpu/load")!;
        Assert.Equal("unraid", info.Host);
        Assert.Equal(SensorCategory.Cpu, info.Category);
        Assert.Equal(Units.Percent, info.Unit);
        Assert.Equal(100, info.Max);
        Assert.Equal(SensorValueKind.Text, h.Info("system/os")!.ValueKind);
        Assert.Equal("Bearer secret", http.Requests[0].Headers["Authorization"]);
        Assert.Equal("http://unraid.local:5342/api/v1/sensors", http.Requests[0].Uri.ToString());
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Display_name_overrides_the_agent_name_and_catalog_changes_are_applied()
    {
        var version = 7;
        var http = new FakeHttp().Route("/api/v1/sensors", _ => new FakeHttp.FakeReply(HttpStatusCode.OK, version == 7 ? Sensors : """
            {"name":"unraid","catalogVersion":8,"sensors":[{"id":"system/cpu/load","name":"CPU load","category":"cpu","unit":"%"},{"id":"net/eth0/rx","name":"eth0 rx","category":"network","unit":"B/s"}],
             "values":{"system/cpu/load":10,"net/eth0/rx":1500}}
            """));
        var h = Make(http, ",\"name\":\"NAS\"");

        await h.StepAsync();
        Assert.Equal("NAS", h.Info("system/cpu/load")!.Host);
        Assert.True(h.Has("unraid/disk/disk1/temp"));

        version = 8;
        await h.StepAsync();
        Assert.False(h.Has("unraid/disk/disk1/temp"));
        Assert.Equal(1500, h.Number("net/eth0/rx"));
        Assert.Equal(10, h.Number("system/cpu/load"));
    }

    [Fact]
    public async Task Rejected_token_makes_the_provider_unavailable_and_unreachable_agent_invalidates_values()
    {
        var http = new FakeHttp().Route("/api/v1/sensors", "{}", HttpStatusCode.Unauthorized);
        var h = Make(http);
        var delay = await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("token", h.Message);
        Assert.True(delay >= TimeSpan.FromSeconds(30));

        var down = new FakeHttp().Route("/api/v1/sensors", Sensors);
        var h2 = Make(down);
        await h2.StepAsync();
        Assert.Equal(42.5, h2.Number("system/cpu/load"));
        down.Fail("/api/v1/sensors");
        for (var i = 0; i < 3; i++)
        {
            await h2.StepAsync();
        }

        Assert.Equal(ProviderHealth.Degraded, h2.Health);
        Assert.Contains("Cannot reach", h2.Message);
        Assert.Null(h2.Number("system/cpu/load"));
    }

    [Fact]
    public async Task Missing_url_is_a_clear_setup_problem()
    {
        var h = Net.Make(new AgentProviderFactory(), "{}", new FakeHttp());
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("address", h.Message);
    }

    [Fact]
    public void Discovery_joins_ptr_srv_txt_and_address_records()
    {
        var collector = new AgentRecordCollector();
        collector.Add(
        [
            new PTRRecord { Name = "_vustudio._tcp.local", DomainName = "nas._vustudio._tcp.local" },
            new SRVRecord { Name = "nas._vustudio._tcp.local", Target = "nas.vustudio.local", Port = 5342 },
            new TXTRecord { Name = "nas._vustudio._tcp.local", Strings = ["txtvers=1", "role=agent", "v=1", "name=Unraid Tower"] },
            new ARecord { Name = "nas.vustudio.local", Address = IPAddress.Parse("192.168.1.20") },
        ]);
        collector.Add([new PTRRecord { Name = "_vustudio._tcp.local", DomainName = "phone._vustudio._tcp.local" }]);

        Assert.Equal(["phone._vustudio._tcp.local"], collector.Unresolved());
        var agent = Assert.Single(collector.Results());
        Assert.Equal("Unraid Tower", agent.Name);
        Assert.Equal("http://192.168.1.20:5342", agent.Url);
        Assert.Equal("1", agent.Version);
        Assert.DoesNotContain("token", agent.Txt.Keys);
    }

    [Fact]
    public void Discovery_ignores_instances_that_are_not_agents()
    {
        var collector = new AgentRecordCollector();
        collector.Add(
        [
            new PTRRecord { Name = "_vustudio._tcp.local", DomainName = "ui._vustudio._tcp.local" },
            new SRVRecord { Name = "ui._vustudio._tcp.local", Target = "pc.local", Port = 5341 },
            new TXTRecord { Name = "ui._vustudio._tcp.local", Strings = ["role=service"] },
            new ARecord { Name = "pc.local", Address = IPAddress.Parse("192.168.1.5") },
        ]);
        Assert.Empty(collector.Results());
    }

    [Fact]
    public async Task Static_discovery_normalises_addresses()
    {
        var found = await new StaticAgentDiscovery(["deck.local:5342", ""]).DiscoverAsync(TimeSpan.Zero);
        Assert.Equal("http://deck.local:5342", Assert.Single(found).Url);
    }
}
