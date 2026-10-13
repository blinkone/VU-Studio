using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.History;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Server.Api;
using VuStudio.Server.Hosting;
using VuStudio.Server.Security;
using VuStudio.Sources.Common;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Server;

/// <summary>A provider type with the setting shapes the secret-restore rules care about. Never started.</summary>
internal sealed class SecretTestFactory : IProviderFactory
{
    public const string TypeName = "secret-test";

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Name = new LocalizedText("Secret test"),
        Settings =
        [
            Setting.Url("url", new LocalizedText("URL")),
            Setting.Text("username", new LocalizedText("User")),
            Setting.Secret("password", new LocalizedText("Password")),
            Setting.Interval(30),
            Setting.Table(
                "headers",
                new LocalizedText("Headers"),
                [Setting.Text("name", new LocalizedText("Name")), Setting.Secret("value", new LocalizedText("Value"))]),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) => throw new NotSupportedException();
}

internal sealed class ServerFixture : IDisposable
{
    private readonly TempDirectory _dir = new("server");

    public ServerFixture()
    {
        Store = new ConfigStore(_dir.Combine("config.json"), NullLogger<ConfigStore>.Instance);
        Providers = new ProviderManager([new SecretTestFactory()], new SensorHub(), TestServices.Empty(), NullLogger<ProviderManager>.Instance);
        Service = new ConfigService(Store, Providers);
        Store.Update(c =>
        {
            c.Web.PasswordHash = PasswordHasher.Hash("password123");
            if (c.Profiles.Count == 0)
            {
                c.Profiles.Add(new VuStudio.Core.Profiles.Profile { Id = "default", Name = "Default" });
            }
            c.Providers.Add(new ProviderInstanceConfig
            {
                Id = "src",
                Type = SecretTestFactory.TypeName,
                Settings = JsonNode.Parse("""
                    { "url": "http://nas.lan/api", "username": "bob", "password": "p1", "interval": 5,
                      "headers": [ { "name": "X-A", "value": "sa" }, { "name": "X-B", "value": "sb" } ] }
                    """)!.AsObject(),
            });
        });
    }

    public ConfigStore Store { get; }

    public ProviderManager Providers { get; }

    public ConfigService Service { get; }

    public JsonObject Settings(AppConfig config) => config.Providers.Single(p => p.Id == "src").Settings;

    public JsonObject Stored => Settings(Store.Current);

    public void Dispose() => _dir.Dispose();
}

public class LocalTrustTests
{
    [Theory]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "[::1]", true)]
    [InlineData("127.0.0.1", "vu.localhost", true)]
    [InlineData("127.0.0.1", "LOCALHOST", true)]
    [InlineData("127.0.0.1", "mypc", false)]
    [InlineData("127.0.0.1", "mypc.local", false)]
    [InlineData("127.0.0.1", "vu.example.com", false)]
    [InlineData("127.0.0.1", ".localhost", false)]
    [InlineData("127.0.0.1", "192.168.1.5", false)]
    [InlineData("192.168.1.20", "localhost", false)]
    [InlineData("192.168.1.20", "127.0.0.1", false)]
    public void Only_loopback_peers_using_loopback_names_are_local(string remote, string host, bool local) =>
        Assert.Equal(local, SecurityMiddleware.IsLocalRequest(IPAddress.Parse(remote), host));

    private static async Task<(HttpContext Context, bool Passed)> RunAsync(ConfigStore store, string remote, string host, string path, Action<HttpRequest>? configure = null)
    {
        var passed = false;
        var middleware = new SecurityMiddleware(_ => { passed = true; return Task.CompletedTask; }, store, new AuthSessions(TimeProvider.System), NullLogger<SecurityMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context.Request);
        await middleware.InvokeAsync(context);
        return (context, passed);
    }

    [Fact]
    public async Task An_allowed_host_name_does_not_skip_the_lan_login()
    {
        using var fixture = new ServerFixture();
        fixture.Store.Update(c =>
        {
            c.Web.AllowLan = true;
            c.Web.AllowedHosts.Add("vu.example.com");
        });

        var (rebound, passed) = await RunAsync(fixture.Store, "127.0.0.1", "vu.example.com:5341", "/api/config");
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, rebound.Response.StatusCode);
        Assert.False(SecurityMiddleware.IsLocal(rebound));

        var (machine, machinePassed) = await RunAsync(fixture.Store, "127.0.0.1", Environment.MachineName.ToLowerInvariant() + ".local", "/api/config");
        Assert.False(machinePassed);
        Assert.Equal(StatusCodes.Status401Unauthorized, machine.Response.StatusCode);

        var (local, localPassed) = await RunAsync(fixture.Store, "127.0.0.1", "localhost:5341", "/api/config");
        Assert.True(localPassed);
        Assert.True(SecurityMiddleware.IsLocal(local));
    }

    [Fact]
    public async Task Allowed_host_names_with_lan_disabled_are_refused_from_loopback()
    {
        using var fixture = new ServerFixture();
        fixture.Store.Update(c => c.Web.AllowedHosts.Add("vu.example.com"));

        var (context, passed) = await RunAsync(fixture.Store, "127.0.0.1", "vu.example.com", "/api/config");
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Requests_from_the_services_own_sources_are_refused()
    {
        using var fixture = new ServerFixture();
        var (context, passed) = await RunAsync(fixture.Store, "127.0.0.1", "localhost", "/api/config",
            r => r.Headers[OutboundMarker.HeaderName] = "anything");
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Marker_handler_marks_every_request_and_overrides_a_user_value()
    {
        var inner = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(OutboundMarker.Wrap(inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.test/");
        request.Headers.TryAddWithoutValidation(OutboundMarker.HeaderName, "forged");
        await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(OutboundMarker.Value, inner.Requests.Single().Headers[OutboundMarker.HeaderName]);
        Assert.Equal(24, OutboundMarker.Value.Length);
    }
}

public class WebSettingsTests
{
    [Fact]
    public void Lan_requests_cannot_change_web_settings()
    {
        using var fixture = new ServerFixture();
        var mutations = new Action<AppConfig>[]
        {
            c => c.Web.AllowedHosts.Add("evil.example.com"),
            c => c.Web.Port = 6000,
            c => c.Web.AllowLan = !c.Web.AllowLan,
            c => c.Web.ApiToken = "0123456789abcdef0123",
        };
        foreach (var mutate in mutations)
        {
            var incoming = fixture.Service.GetMasked();
            mutate(incoming);
            var ex = Assert.Throws<ConfigForbiddenException>(() => fixture.Service.Save(incoming, null, local: false));
            Assert.Contains("only be changed on this PC", ex.Message);
        }

        Assert.Empty(fixture.Store.Current.Web.AllowedHosts);
    }

    [Fact]
    public void Lan_requests_may_save_other_settings_and_local_requests_may_change_web()
    {
        using var fixture = new ServerFixture();
        var incoming = fixture.Service.GetMasked();
        incoming.History.RetentionDays = 7;
        fixture.Service.Save(incoming, null, local: false);
        Assert.Equal(7, fixture.Store.Current.History.RetentionDays);

        incoming = fixture.Service.GetMasked();
        incoming.Web.AllowedHosts.Add("vu.example.com");
        fixture.Service.Save(incoming, null, local: true);
        Assert.Equal(["vu.example.com"], fixture.Store.Current.Web.AllowedHosts);
    }
}

public class SecretRestoreTests
{
    private const string P = ConfigService.SecretPlaceholder;

    private static AppConfig Masked(ServerFixture fixture, Action<JsonObject> edit)
    {
        var config = fixture.Service.GetMasked();
        edit(fixture.Settings(config));
        return config;
    }

    [Fact]
    public void Unchanged_source_gets_its_secrets_back()
    {
        using var fixture = new ServerFixture();
        var incoming = Masked(fixture, s => Assert.Equal(P, (string?)s["password"]));
        fixture.Service.Save(incoming, null, local: false);
        Assert.Equal("p1", (string?)fixture.Stored["password"]);
        Assert.Equal("sa", (string?)fixture.Stored["headers"]![0]!["value"]);
    }

    [Fact]
    public void Changing_only_the_interval_keeps_the_secrets()
    {
        using var fixture = new ServerFixture();
        fixture.Service.Save(Masked(fixture, s => s["interval"] = 60), null, local: false);
        Assert.Equal("p1", (string?)fixture.Stored["password"]);
    }

    [Theory]
    [InlineData("url", "http://attacker.example/")]
    [InlineData("username", "eve")]
    public void Changing_where_the_secret_goes_requires_re_entering_it(string key, string value)
    {
        using var fixture = new ServerFixture();
        var ex = Assert.Throws<ConfigValidationException>(() => fixture.Service.Save(Masked(fixture, s => s[key] = value), null, local: false));
        Assert.Contains(ex.Errors, e => e.Contains("re-enter the secret", StringComparison.Ordinal));
        Assert.Equal("http://nas.lan/api", (string?)fixture.Stored["url"]);
    }

    [Fact]
    public void A_new_url_with_a_freshly_typed_secret_is_accepted()
    {
        using var fixture = new ServerFixture();
        fixture.Service.Save(Masked(fixture, s =>
        {
            s["url"] = "http://other.lan/";
            s["password"] = "p2";
            s["headers"] = JsonNode.Parse("""[ { "name": "X-A", "value": "na" }, { "name": "X-B", "value": "nb" } ]""");
        }), null, local: true);
        Assert.Equal("p2", (string?)fixture.Stored["password"]);
    }

    [Fact]
    public void Changing_the_type_requires_re_entering_the_secret()
    {
        using var fixture = new ServerFixture();
        var incoming = fixture.Service.GetMasked();
        var source = incoming.Providers.Single(p => p.Id == "src");
        incoming.Providers.Remove(source);
        incoming.Providers.Add(new ProviderInstanceConfig { Id = "other", Type = SecretTestFactory.TypeName, Settings = source.Settings });
        Assert.Throws<ConfigValidationException>(() => fixture.Service.Save(incoming, null, local: true));
    }

    [Fact]
    public void Table_rows_are_matched_by_their_non_secret_columns_not_by_position()
    {
        using var fixture = new ServerFixture();
        fixture.Service.Save(Masked(fixture, s =>
        {
            var rows = s["headers"]!.AsArray();
            var first = rows[0]!.DeepClone();
            rows.RemoveAt(0);
            rows.Add(first);
        }), null, local: false);

        var headers = fixture.Stored["headers"]!.AsArray();
        Assert.Equal("X-B", (string?)headers[0]!["name"]);
        Assert.Equal("sb", (string?)headers[0]!["value"]);
        Assert.Equal("sa", (string?)headers[1]!["value"]);
    }

    [Fact]
    public void A_renamed_row_or_a_new_url_does_not_inherit_a_row_secret()
    {
        using var fixture = new ServerFixture();
        Assert.Throws<ConfigValidationException>(() =>
            fixture.Service.Save(Masked(fixture, s => s["headers"]![0]!["name"] = "X-Evil"), null, local: false));

        // The password is re-typed, but the header still holds a placeholder while the URL moved.
        Assert.Throws<ConfigValidationException>(() =>
            fixture.Service.Save(Masked(fixture, s =>
            {
                s["url"] = "http://attacker.example/";
                s["password"] = "typed";
            }), null, local: false));
        Assert.Equal("sa", (string?)fixture.Stored["headers"]![0]!["value"]);
    }
}

public class ChangedRemotelyTests
{
    [Fact]
    public void The_service_tracks_who_changed_a_source_last()
    {
        using var fixture = new ServerFixture();
        ProviderInstanceConfig Source() => fixture.Store.Current.Providers.Single(p => p.Id == "src");
        Assert.False(Source().ChangedRemotely);

        // A LAN change marks it.
        var incoming = fixture.Service.GetMasked();
        fixture.Settings(incoming)["interval"] = 10;
        fixture.Service.Save(incoming, null, local: false);
        Assert.True(Source().ChangedRemotely);

        // A local save that does not touch it keeps the mark; the client cannot clear it either.
        incoming = fixture.Service.GetMasked();
        incoming.Providers.Single(p => p.Id == "src").ChangedRemotely = false;
        fixture.Service.Save(incoming, null, local: true);
        Assert.True(Source().ChangedRemotely);

        // A local change clears it.
        incoming = fixture.Service.GetMasked();
        fixture.Settings(incoming)["interval"] = 20;
        fixture.Service.Save(incoming, null, local: true);
        Assert.False(Source().ChangedRemotely);

        // A source created from the LAN is marked from the start.
        incoming = fixture.Service.GetMasked();
        incoming.Providers.Add(new ProviderInstanceConfig { Id = "lan-made", Type = "http-json", Settings = JsonNode.Parse("""{ "url": "http://x/" }""")!.AsObject() });
        fixture.Service.Save(incoming, null, local: false);
        Assert.True(fixture.Store.Current.Providers.Single(p => p.Id == "lan-made").ChangedRemotely);
    }
}

public class PushTokenTests
{
    private static HttpRequest Request(string? authorization, string? query)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        return context.Request;
    }

    [Fact]
    public void Only_the_bearer_header_is_accepted()
    {
        const string token = "0123456789abcdef0123";
        Assert.True(Endpoints.IsPushAuthorized(Request("Bearer " + token, null), token));
        Assert.False(Endpoints.IsPushAuthorized(Request(null, "?token=" + token), token));
        Assert.False(Endpoints.IsPushAuthorized(Request("Bearer wrong", null), token));
        Assert.False(Endpoints.IsPushAuthorized(Request("Bearer ", null), token));
        Assert.False(Endpoints.IsPushAuthorized(Request("Bearer " + token, null), ""));
    }
}

public class HistoryTrackingTests
{
    private static SensorHub Hub(int count)
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("p");
        for (var i = 0; i < count; i++)
        {
            sink.Define(new SensorInfo { Id = $"s{i}", Name = $"S{i}" });
        }

        return hub;
    }

    [Fact]
    public void Only_existing_sensors_are_tracked_and_the_set_is_capped()
    {
        var hub = Hub(HistoryRecorder.MaxTracked + 1);
        var history = new HistoryRecorder(hub, NullLogger<HistoryRecorder>.Instance);

        Assert.False(history.Track("p/does-not-exist"));
        Assert.False(history.Track(""));
        for (var i = 0; i < HistoryRecorder.MaxTracked; i++)
        {
            Assert.True(history.Track($"p/s{i}"));
        }

        Assert.True(history.Track("p/s0")); // already tracked
        Assert.False(history.Track($"p/s{HistoryRecorder.MaxTracked}"));
        Assert.Equal(HistoryRecorder.MaxTracked, history.TrackedCount);
        Assert.DoesNotContain("p/does-not-exist", history.RecordedSensors);
    }

    [Fact]
    public void Configured_sensors_do_not_count_and_a_config_change_resets_on_demand_tracking()
    {
        var hub = Hub(3);
        var history = new HistoryRecorder(hub, NullLogger<HistoryRecorder>.Instance);
        history.SetRecordedSensors(["p/s0"]);
        Assert.True(history.Track("p/s0"));
        Assert.True(history.Track("p/s1"));
        Assert.Equal(1, history.TrackedCount);
        Assert.Equal(["p/s0", "p/s1"], history.RecordedSensors.Order());

        history.SetRecordedSensors(["p/s2"]);
        Assert.Equal(0, history.TrackedCount);
        Assert.Equal(["p/s2"], history.RecordedSensors);
    }

    [Fact]
    public void Tracked_sensors_are_sampled()
    {
        var hub = Hub(1);
        hub.CreateSink("p").Publish("s0", 7);
        var history = new HistoryRecorder(hub, NullLogger<HistoryRecorder>.Instance);
        history.Track("p/s0");
        history.Sample();
        Assert.Equal(7, history.GetRecent("p/s0", TimeSpan.FromMinutes(1)).Single().Value);
    }

    [Fact]
    public void Track_endpoint_refuses_unknown_sensors()
    {
        var hub = Hub(1);
        var history = new HistoryRecorder(hub, NullLogger<HistoryRecorder>.Instance);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(Endpoints.Track(new TrackRequest("p/nope"), history, hub)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(Endpoints.Track(new TrackRequest(null), history, hub)));
        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(Endpoints.Track(new TrackRequest("p/s0"), history, hub)));
        Assert.Contains("p/s0", history.RecordedSensors);
    }

    private static int? StatusOf(IResult result) => (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
}

public class AppPathsSecurityTests
{
    [Fact]
    public void Linked_subdirectories_are_replaced_by_real_ones_when_secure()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need a privilege on Windows");
        using var dir = new TempDirectory("paths");
        var target = Directory.CreateDirectory(dir.Combine("elsewhere")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var root = dir.Combine("data");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(Path.Combine(root, "logs"), target);

        var paths = new AppPaths(root, secure: true);

        Assert.False(new DirectoryInfo(paths.Logs).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.Contains(paths.Problems, p => p.Contains("logs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_linked_data_directory_is_replaced_when_secure_and_kept_otherwise()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need a privilege on Windows");
        using var dir = new TempDirectory("paths");
        var target = Directory.CreateDirectory(dir.Combine("elsewhere")).FullName;
        var root = dir.Combine("data");
        Directory.CreateSymbolicLink(root, target);

        var relaxed = new AppPaths(root);
        Assert.Empty(relaxed.Problems);
        Assert.True(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));

        var secure = new AppPaths(root, secure: true);
        Assert.False(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Single(secure.Problems);
        Assert.True(Directory.Exists(Path.Combine(target, "logs"))); // the target is left alone
    }
}
