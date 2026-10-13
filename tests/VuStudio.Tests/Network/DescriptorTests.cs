using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Remote;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Network;

public class NetworkDescriptorTests
{
    private static readonly System.Reflection.Assembly Sources = typeof(AgentProviderFactory).Assembly;

    private static readonly Dictionary<string, ProviderGroup> Expected = new()
    {
        ["agent"] = ProviderGroup.Network,
        ["glances"] = ProviderGroup.Network,
        ["prometheus"] = ProviderGroup.Network,
        ["lhm-remote"] = ProviderGroup.Network,
        ["unraid"] = ProviderGroup.Network,
        ["upnp-wan"] = ProviderGroup.Network,
        ["weather"] = ProviderGroup.Online,
        ["crypto"] = ProviderGroup.Online,
    };

    public static TheoryData<string> Types()
    {
        var data = new TheoryData<string>();
        foreach (var type in Expected.Keys)
        {
            data.Add(type);
        }

        return data;
    }

    private static IProviderFactory Factory(string type) => ProviderCatalog.Discover(Sources).Single(f => f.Descriptor.Type == type);

    [Fact]
    public void All_network_providers_are_discovered_by_reflection()
    {
        var found = ProviderCatalog.Discover(Sources).Select(f => f.Descriptor.Type).ToHashSet();
        Assert.All(Expected.Keys, type => Assert.Contains(type, found));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Group_platform_and_instances_are_as_expected(string type)
    {
        var d = Factory(type).Descriptor;
        Assert.Equal(Expected[type], d.Group);
        Assert.False(d.Singleton);
        Assert.False(d.AutoCreate);
        Assert.Equal(PlatformSupport.Any, d.Platforms);
        Assert.Contains(d.Settings, s => s.Key == "interval");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Credentials_use_secret_fields(string type)
    {
        foreach (var field in Flatten(Factory(type).Descriptor.Settings))
        {
            var key = field.Key.ToLowerInvariant();
            if (key.Contains("password") || key.Contains("token") || key.Contains("apikey"))
            {
                Assert.Equal(SettingKind.Secret, field.Kind);
            }
        }
    }

    private static IEnumerable<SettingField> Flatten(IEnumerable<SettingField> fields) =>
        fields.SelectMany(f => new[] { f }.Concat(Flatten(f.Columns ?? [])));

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Create_works_with_an_empty_service_provider_and_defaults_are_usable(string type)
    {
        var factory = Factory(type);
        var provider = factory.Create(TestConfig.For(type, "{}", "test-" + type), TestServices.Empty());
        Assert.Equal("test-" + type, provider.Id);
        var settings = new ProviderSettings(new JsonObject(), factory.Descriptor);
        Assert.True(settings.GetSeconds("interval", 60) > TimeSpan.Zero);
        await provider.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task An_unconfigured_provider_runs_without_crashing_and_says_what_is_missing(string type)
    {
        var factory = Factory(type);
        var provider = factory.Create(TestConfig.For(type, "{}", "t"), TestServices.Empty());
        var hub = new SensorHub();
        var status = new RecordingStatus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            await provider.RunAsync(hub.CreateSink("t"), status, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await provider.DisposeAsync();
        Assert.NotEmpty(status.Reports);
    }

    [Fact]
    public async Task Bad_addresses_never_throw_out_of_the_provider()
    {
        foreach (var (type, settings) in new[]
        {
            ("agent", """{"url":"::::not a url"}"""), ("glances", """{"url":"ftp://x"}"""), ("prometheus", """{"url":" "}"""),
            ("lhm-remote", """{"url":"http://"}"""), ("unraid", """{"url":"???","apiKey":"k"}"""),
        })
        {
            var h = Net.Make(Factory(type), settings, new FakeHttp());
            await h.StepAsync();
            Assert.Equal(ProviderHealth.Unavailable, h.Health);
        }
    }
}
