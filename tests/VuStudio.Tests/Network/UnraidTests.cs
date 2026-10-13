using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Unraid;

namespace VuStudio.Tests.Network;

public class UnraidTests
{
    private const string ArrayOk = """
        {"data":{"array":{"state":"STARTED","capacity":{"kilobytes":{"free":"31900000000","used":"21000000000","total":"52900000000"}},
         "parityCheckStatus":{"status":"RUNNING","progress":42,"speed":"151"},
         "parities":[{"name":"parity","status":"DISK_OK","temp":null,"isSpinning":false,"numErrors":0}],
         "disks":[{"name":"disk1","device":"sdb","status":"DISK_OK","temp":30,"isSpinning":true,"numErrors":0,"fsSize":"17576897220","fsFree":"13557363020","fsUsed":"4019534200"},
                  {"name":"disk2","device":"sdc","status":"DISK_DSBL","temp":41,"isSpinning":true,"numErrors":3,"fsSize":"0"}],
         "caches":[{"name":"cache","status":"DISK_OK","temp":38,"isSpinning":true,"numErrors":0,"fsSize":"1000000","fsFree":"400000","fsUsed":"600000"}]}}}
        """;

    private static FakeHttp Server() => new FakeHttp().Route("/graphql", seen =>
    {
        string Reply(string body) => body;
        if (seen.Body.Contains("array {"))
        {
            return new FakeHttp.FakeReply(HttpStatusCode.OK, Reply(ArrayOk));
        }

        if (seen.Body.Contains("docker {"))
        {
            return new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"docker":{"containers":[{"names":["/plex"],"state":"RUNNING"},{"names":["/old-thing"],"state":"EXITED"},{"names":["/db"],"state":"RUNNING"}]}}}""");
        }

        if (seen.Body.Contains("vms {"))
        {
            return new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"vms":{"domains":[{"name":"Win11","state":"RUNNING"},{"name":"Test","state":"SHUTOFF"}]}}}""");
        }

        if (seen.Body.Contains("metrics {"))
        {
            return new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"metrics":{"cpu":{"percentTotal":12.5},"memory":{"total":"16000000000","used":"8000000000","percentTotal":50.0,"percentSwapTotal":3.5}}}}""");
        }

        return new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"info":{"os":{"hostname":"tower","uptime":"2026-10-10T12:00:00.000Z"}}}}""");
    });

    private static Harness Make(FakeHttp http) =>
        new(time => new UnraidProvider("p", new Uri("http://tower.local"), "KEY", null, TimeSpan.FromSeconds(10), Net.Client(http), null, time, NullLogger.Instance));

    [Fact]
    public async Task Maps_array_capacity_parity_disks_docker_vms_and_metrics()
    {
        var http = Server();
        var h = Make(http);
        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal("KEY", http.Requests[0].Headers["x-api-key"]);
        Assert.Equal("http://tower.local/graphql", http.Requests[0].Uri.ToString());

        Assert.Equal("STARTED", h.Text("array/state"));
        Assert.Equal(1, h.Number("array/started"));
        Assert.Equal(21_000_000_000d * 1024, h.Number("array/used"));
        Assert.Equal(31_900_000_000d * 1024, h.Number("array/free"));
        Assert.Equal(100 * 21_000_000_000d / 52_900_000_000, h.Number("array/percent")!.Value, 6);
        Assert.Equal("RUNNING", h.Text("parity/status"));
        Assert.Equal(1, h.Number("parity/running"));
        Assert.Equal(42, h.Number("parity/progress"));
        Assert.Equal(151_000_000, h.Number("parity/speed"));

        Assert.Equal(30, h.Number("disk/disk1/temp"));
        Assert.Equal(1, h.Number("disk/disk1/ok"));
        Assert.Equal(1, h.Number("disk/disk1/spinning"));
        Assert.Equal(100 * 4019534200d / 17576897220, h.Number("disk/disk1/percent")!.Value, 6);
        Assert.Equal(13557363020d * 1024, h.Number("disk/disk1/free"));
        Assert.Equal(0, h.Number("disk/disk2/ok"));
        Assert.Equal("DISK_DSBL", h.Text("disk/disk2/status"));
        Assert.Equal(3, h.Number("disk/disk2/errors"));
        Assert.False(h.Has("disk/disk2/percent")); // no filesystem figures
        Assert.Equal(60, h.Number("disk/cache/percent"));
        Assert.Equal(0, h.Number("disk/parity/spinning"));
        Assert.Equal("disk1 (sdb)", h.Info("disk/disk1/temp")!.Device);

        Assert.Equal(2, h.Number("docker/running"));
        Assert.Equal(3, h.Number("docker/total"));
        Assert.Equal(1, h.Number("docker/c/plex"));
        Assert.Equal(0, h.Number("docker/c/old-thing"));
        Assert.Equal(1, h.Number("vms/running"));
        Assert.Equal(2, h.Number("vms/total"));
        Assert.Equal(12.5, h.Number("cpu/total"));
        Assert.Equal(50, h.Number("memory/percent"));
        Assert.Equal(3.5, h.Number("swap/percent"));
        Assert.Equal(172800, h.Number("uptime"));
        Assert.Equal("tower", h.Info("array/used")!.Host);
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Standby_disks_keep_their_last_temperature_and_idle_parity_reads_zero()
    {
        var http = Server();
        var h = Make(http);
        await h.StepAsync();

        http.Route("/graphql", seen => seen.Body.Contains("array {")
            ? new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"array":{"state":"STARTED","parityCheckStatus":{"status":"COMPLETED","progress":100,"speed":"0"},"disks":[{"name":"disk1","status":"DISK_OK","temp":null,"isSpinning":false}]}}}""")
            : new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":{"vms":null}}"""));
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.StepAsync();

        Assert.Equal(30, h.Number("disk/disk1/temp"));
        Assert.Equal(0, h.Number("disk/disk1/spinning"));
        Assert.Equal(0, h.Number("parity/progress"));
        Assert.Equal(0, h.Number("parity/running"));
        Assert.Null(h.Number("disk/cache/temp")); // the cache pool vanished from the answer
    }

    [Fact]
    public async Task Older_api_versions_fall_back_to_a_simpler_query()
    {
        var http = new FakeHttp().Route("/graphql", seen =>
        {
            if (!seen.Body.Contains("array {"))
            {
                return new FakeHttp.FakeReply(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"Cannot query field \"docker\" on type \"Query\".","extensions":{"code":"GRAPHQL_VALIDATION_FAILED"}}]}""");
            }

            return seen.Body.Contains("parityCheckStatus")
                ? new FakeHttp.FakeReply(HttpStatusCode.BadRequest, """{"errors":[{"message":"Cannot query field \"parityCheckStatus\" on type \"UnraidArray\".","extensions":{"code":"GRAPHQL_VALIDATION_FAILED"}}]}""")
                : new FakeHttp.FakeReply(HttpStatusCode.OK, ArrayOk);
        });
        var h = Make(http);

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(1, h.Number("array/started"));
        Assert.Equal(30, h.Number("disk/disk1/temp"));
        Assert.False(h.Has("docker/running"));

        var before = http.Requests.Count;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.StepAsync();
        // Unsupported sections are not asked again for a while, and the working array query is remembered.
        Assert.Equal(1, http.Requests.Skip(before).Count());
    }

    [Fact]
    public async Task Bad_key_redirects_and_missing_settings_are_reported_as_unavailable()
    {
        var h = Make(new FakeHttp().Route("/graphql", "{}", HttpStatusCode.Unauthorized));
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("API key", h.Message);

        var redirect = Make(new FakeHttp().Route("/graphql", _ => new FakeHttp.FakeReply(HttpStatusCode.MovedPermanently, "", new() { ["Location"] = "https://tower.local/graphql" })));
        await redirect.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, redirect.Health);
        Assert.Contains("https://", redirect.Message);

        var noKey = Net.Make(new UnraidProviderFactory(), """{"url":"tower.local"}""", new FakeHttp());
        await noKey.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, noKey.Health);
        Assert.Contains("API key", noKey.Message);
    }

    [Fact]
    public void Graphql_results_distinguish_validation_errors_from_runtime_errors()
    {
        var validation = UnraidProvider.ParseResult(System.Text.Json.Nodes.JsonNode.Parse("""{"errors":[{"message":"x","extensions":{"code":"GRAPHQL_VALIDATION_FAILED"}}]}"""));
        Assert.True(validation.IsValidationError);
        Assert.Null(validation.Data);
        var runtime = UnraidProvider.ParseResult(System.Text.Json.Nodes.JsonNode.Parse("""{"data":{"docker":null},"errors":[{"message":"Docker is not running"}]}"""));
        Assert.False(runtime.IsValidationError);
        Assert.Equal("Docker is not running", Assert.Single(runtime.Errors));
    }
}
