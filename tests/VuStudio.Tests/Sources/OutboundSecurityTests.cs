using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Http;
using VuStudio.Sources.SystemMetrics;

namespace VuStudio.Tests.Sources;

public class DestinationGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.10.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fd00::1", false)]
    public void Loopback_link_local_and_unspecified_addresses_are_forbidden(string address, bool forbidden) =>
        Assert.Equal(forbidden, DestinationGuard.IsForbidden(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Restricted_client_does_not_connect_to_this_machine(string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = DestinationGuard.CreateRestrictedClient();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://{host}:{port}/api/config", TestContext.Current.CancellationToken));
        Assert.Contains("this computer", ex.Message);
        Assert.False(listener.Pending());
    }

    private const string Settings = """{ "url": "http://127.0.0.1:9/x", "headers": [ { "name": "X-VuStudio-Outbound", "value": "forged" } ] }""";

    [Fact]
    public async Task Http_json_source_changed_from_the_lan_refuses_loopback()
    {
        var config = TestConfig.For("http-json", Settings, "api");
        config.ChangedRemotely = true;
        await using var provider = new HttpJsonProviderFactory().Create(config, TestServices.Empty());
        var sink = new SensorHub().CreateSink("api");
        await ProviderDriver.InitializeAsync(provider, sink);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ProviderDriver.PollAsync(provider, sink));
        Assert.Contains("this computer", ex.Message);
    }

    [Fact]
    public async Task Http_json_requests_always_carry_the_real_marker()
    {
        var handler = FakeHttpHandler.Json("""{ "a": 1 }""");
        var config = TestConfig.For("http-json", Settings, "api");
        var services = TestServices.With(s => s.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler)));
        await using var provider = new HttpJsonProviderFactory().Create(config, services);
        var sink = new SensorHub().CreateSink("api");
        await ProviderDriver.InitializeAsync(provider, sink);
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(OutboundMarker.Value, handler.Requests.Single().Headers[OutboundMarker.HeaderName]);
    }
}

public class ExecutableSearchTests
{
    [Fact]
    public void Pathext_is_only_applied_when_asked()
    {
        Assert.Equal(["nvidia-smi"], ExecutableLocator.CandidateNames("nvidia-smi", withPathExt: false, ".COM;.EXE;.BAT"));
        Assert.Equal(["nvidia-smi", "nvidia-smi.COM", "nvidia-smi.EXE", "nvidia-smi.BAT"], ExecutableLocator.CandidateNames("nvidia-smi", withPathExt: true, ".COM;.EXE;.BAT"));
    }

    [Fact]
    public void Nvidia_smi_on_windows_is_only_looked_up_at_absolute_well_known_paths()
    {
        var candidates = NvidiaSmiCollector.WindowsCandidates(@"C:\Windows\System32", @"C:\Program Files");
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.EndsWith("nvidia-smi.exe", c, StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Contains("NVSMI", StringComparison.Ordinal));
        Assert.Empty(NvidiaSmiCollector.WindowsCandidates("", ""));
    }

    [Fact]
    public void Relative_path_entries_and_relative_candidates_are_ignored()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses Unix permission bits");
        using var directory = new TempDirectory("exe");
        var tool = directory.Write("mytool", "#!/bin/sh\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, directory.Path);
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.Null(ExecutableLocator.Find("mytool", relative));
        Assert.Equal(tool, ExecutableLocator.Find("mytool", directory.Path, usePathExt: false));

        Assert.Null(ExecutableLocator.FirstExisting([Path.Combine(relative, "mytool")]));
        Assert.Equal(tool, ExecutableLocator.FirstExisting(["", Path.Combine(directory.Path, "missing"), tool]));
    }
}
