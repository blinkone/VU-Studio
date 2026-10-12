using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Http;

namespace VuStudio.Tests.Sources;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and remembers what it was asked.</summary>
internal sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public static FakeHttpHandler Json(string json) => new(_ => JsonResponse(json));

    /// <summary>Answers with each document in turn, then keeps repeating the last one.</summary>
    public static FakeHttpHandler Sequence(params string[] documents)
    {
        var index = -1;
        return new FakeHttpHandler(_ => JsonResponse(documents[Math.Min(Interlocked.Increment(ref index), documents.Length - 1)]));
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        // A header that appears twice (request and content) is joined, so a duplicate shows up in assertions.
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method, request.RequestUri, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return respond(recorded);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> RequestedNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

public class HttpJsonProviderTests
{
    private const string Weather = """
        {
          "main": { "temp": 21.5, "humidity": 40 },
          "weather": [ { "description": "light rain" } ],
          "city": "Rome",
          "ok": true,
          "cpu": "88.5"
        }
        """;

    private static readonly ProviderDescriptor Descriptor = new HttpJsonProviderFactory().Descriptor;

    private static HttpJsonProvider Provider(string settingsJson, HttpMessageHandler handler, string? device = null)
    {
        var settings = new ProviderSettings(TestConfig.For("http-json", settingsJson).Settings, Descriptor);
        return new HttpJsonProvider("weather", HttpJsonOptions.From(settings), new HttpClient(handler, disposeHandler: false), device, NullLogger.Instance);
    }

    private static string Settings(string fields = "[]", string extra = "") =>
        "{\"url\":\"https://api.example.test/v1/now\",\"interval\":0.05" + extra + ",\"fields\":" + fields + "}";

    [Fact]
    public async Task Extracts_the_configured_fields_with_their_metadata()
    {
        var handler = FakeHttpHandler.Json(Weather);
        var fields = """
            [
              {"id":"temp","name":"Temperature","path":"$.main.temp","unit":"°C","category":"weather","min":-20,"max":50},
              {"id":"desc","name":"Sky","path":"$.weather[0].description","text":true,"category":"weather"},
              {"id":"humidity","path":"main.humidity","multiplier":0.01,"unit":"","kind":"humidity"},
              {"id":"ok","path":"$.ok"},
              {"id":"cpu","path":"$.cpu","unit":"%"},
              {"id":"nothing","path":"$.does.not.exist"}
            ]
            """;
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(fields), handler), hub, () => hub.GetNumber("weather/cpu") is not null);

        Assert.Equal(21.5, hub.GetNumber("weather/temp"));
        Assert.Equal("light rain", hub.GetText("weather/desc"));
        Assert.Equal(0.4, hub.GetNumber("weather/humidity")!.Value, 9);
        Assert.Equal(1, hub.GetNumber("weather/ok"));
        Assert.Equal(88.5, hub.GetNumber("weather/cpu"));
        Assert.Null(hub.GetNumber("weather/nothing"));

        var temp = hub.Get("weather/temp")!.Info;
        Assert.Equal("Temperature", temp.Name);
        Assert.Equal(Units.Celsius, temp.Unit);
        Assert.Equal(SensorCategory.Weather, temp.Category);
        Assert.Equal(SensorKinds.Temperature, temp.Kind); // inferred from the unit
        Assert.Equal(-20, temp.Min);
        Assert.Equal(50, temp.Max);
        Assert.Equal("api.example.test", temp.Device);
        Assert.False(string.IsNullOrWhiteSpace(temp.Icon));
        Assert.True(temp.StaleAfterSeconds >= 30);

        var desc = hub.Get("weather/desc")!.Info;
        Assert.Equal(SensorValueKind.Text, desc.ValueKind);
        Assert.Equal(Units.None, desc.Unit);

        Assert.Equal("humidity", hub.Get("weather/humidity")!.Info.Kind);
        Assert.Equal("humidity", hub.Get("weather/humidity")!.Info.Name); // no name: the id
    }

    [Fact]
    public async Task Slow_sources_are_not_reported_stale_between_polls()
    {
        var interval = 60.0;
        var provider = Provider($$"""{"url":"https://x.test/","interval":{{interval}},"fields":[{"id":"a","path":"$.a"}]}""", FakeHttpHandler.Json("""{"a":1}"""));
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber("weather/a") is not null);
        Assert.Equal(180, hub.Get("weather/a")!.Info.StaleAfterSeconds);
    }

    [Fact]
    public async Task Sends_a_get_with_sensible_default_headers()
    {
        var handler = FakeHttpHandler.Json("{}");
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(), handler), hub, () => handler.Requests.Count >= 1);

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.example.test/v1/now", request.Uri!.ToString());
        Assert.Contains("application/json", request.Headers["Accept"]);
        Assert.StartsWith("VuStudio/", request.Headers["User-Agent"]);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Sends_a_post_with_a_body_and_custom_headers()
    {
        var handler = FakeHttpHandler.Json("""{"v":1}""");
        var extra = """
            ,"method":"POST","body":"{\"query\":\"cpu\"}",
            "headers":[
              {"name":"Authorization","value":"Bearer secret-token"},
              {"name":"X-Api-Key","value":"abc123"},
              {"name":"Accept","value":"application/vnd.api+json"},
              {"name":"Content-Type","value":"application/x-custom+json"},
              {"name":"","value":"ignored empty row"}
            ]
            """;
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(extra: extra), handler), hub, () => handler.Requests.Count >= 1);

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{"query":"cpu"}""", request.Body);
        Assert.Equal("Bearer secret-token", request.Headers["Authorization"]);
        Assert.Equal("abc123", request.Headers["X-Api-Key"]);
        Assert.Equal("application/vnd.api+json", request.Headers["Accept"]); // replaces the default
        Assert.Equal("application/x-custom+json", request.Headers["Content-Type"]); // sent once, not twice
    }

    [Fact]
    public async Task Headers_that_describe_a_body_are_ignored_when_there_is_no_body()
    {
        var handler = FakeHttpHandler.Json("{}");
        var extra = ",\"headers\":[{\"name\":\"Content-Type\",\"value\":\"text/plain\"},{\"name\":\"X-Keep\",\"value\":\"1\"}]";
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(extra: extra), handler), hub, () => handler.Requests.Count >= 1);

        var request = handler.Requests[0];
        Assert.False(request.Headers.ContainsKey("Content-Type"));
        Assert.Equal("1", request.Headers["X-Keep"]);
    }

    [Fact]
    public async Task A_content_length_header_is_never_forced_onto_the_request()
    {
        var handler = FakeHttpHandler.Json("{}");
        var extra = ",\"method\":\"POST\",\"body\":\"{}\",\"headers\":[{\"name\":\"Content-Length\",\"value\":\"999\"}]";
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(extra: extra), handler), hub, () => handler.Requests.Count >= 1);

        // The user's value must not override the real length of the body; HttpClient decides that.
        Assert.NotEqual("999", handler.Requests[0].Headers.GetValueOrDefault("Content-Length"));
        Assert.Equal("{}", handler.Requests[0].Body);
    }

    [Fact]
    public async Task A_post_body_is_json_unless_told_otherwise()
    {
        var handler = FakeHttpHandler.Json("{}");
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(extra: ",\"method\":\"POST\",\"body\":\"{}\""), handler),
            hub,
            () => handler.Requests.Count >= 1);

        Assert.StartsWith("application/json", handler.Requests[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task A_get_ignores_the_body()
    {
        var handler = FakeHttpHandler.Json("{}");
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(extra: ",\"body\":\"ignored\""), handler), hub, () => handler.Requests.Count >= 1);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task Without_fields_every_leaf_becomes_a_sensor_and_vanished_leaves_go_offline()
    {
        var handler = FakeHttpHandler.Sequence(
            """{"main":{"temp":21.5,"label":"warm"},"list":[5,true,null,"s"],"gone":3,"Odd Key":2}""",
            """{"main":{"temp":22.5,"label":"warm"},"list":[5,true,null,"s"],"Odd Key":2}""");
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), handler, device: "Home weather"),
            hub,
            () => hub.GetNumber("weather/main/temp") == 22.5 && hub.GetNumber("weather/gone") is null);

        Assert.Equal("warm", hub.GetText("weather/main/label"));
        Assert.Equal(5, hub.GetNumber("weather/list/0"));
        Assert.Equal(1, hub.GetNumber("weather/list/1"));
        Assert.Equal("s", hub.GetText("weather/list/3"));
        Assert.Equal(2, hub.GetNumber("weather/odd-key"));
        Assert.NotNull(hub.Get("weather/gone")); // offline, not deleted
        Assert.Null(hub.GetNumber("weather/gone"));

        var info = hub.Get("weather/main/temp")!.Info;
        Assert.Equal("main/temp", info.Name);
        Assert.Equal("Home weather", info.Device);
        Assert.Equal(SensorValueKind.Text, hub.Get("weather/main/label")!.Info.ValueKind);
    }

    [Fact]
    public async Task The_instance_name_is_used_as_device_when_the_factory_has_one()
    {
        var handler = FakeHttpHandler.Json("""{"a":1}""");
        var config = TestConfig.For("http-json", Settings("""[{"id":"a","path":"$.a"}]"""), id: "casa");
        config.Name = "  Casa  ";
        var provider = new HttpJsonProviderFactory().Create(config, TestServices.With(s => s.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler))));

        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber("casa/a") is not null);
        Assert.Equal("Casa", hub.Get("casa/a")!.Info.Device);
    }

    [Fact]
    public async Task The_factory_takes_its_client_from_the_http_client_factory()
    {
        var handler = FakeHttpHandler.Json("""{"a":7}""");
        var clients = new FakeHttpClientFactory(handler);
        var provider = new HttpJsonProviderFactory().Create(
            TestConfig.For("http-json", Settings("""[{"id":"a","path":"$.a"}]""")),
            TestServices.With(s => s.AddSingleton<IHttpClientFactory>(clients)));

        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber("http-json/a") == 7);
        Assert.Equal([ProviderServices.HttpClientName], clients.RequestedNames);
    }

    [Fact]
    public async Task A_failing_status_degrades_the_provider_and_recovery_clears_it()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) <= 2
            ? FakeHttpHandler.JsonResponse("{}", HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.JsonResponse("""{"a":5}"""));
        var status = new RecordingStatus();
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings("""[{"id":"a","path":"$.a"}]"""), handler),
            hub,
            () => hub.GetNumber("weather/a") == 5 && status.Last?.Health == ProviderHealth.Ok,
            status: status);

        var reports = status.Reports;
        var degraded = reports.First(r => r.Health == ProviderHealth.Degraded);
        Assert.Contains("503", degraded.Message);
        Assert.Equal(ProviderHealth.Ok, reports[^1].Health);
    }

    [Fact]
    public async Task A_body_that_is_not_json_degrades_the_provider()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>nope</html>") });
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(Provider(Settings(), handler), new SensorHub(), () => status.Last?.Health == ProviderHealth.Degraded, status: status);
        Assert.Contains("not valid JSON", status.Last!.Value.Message);
    }

    [Fact]
    public async Task A_request_that_takes_too_long_times_out()
    {
        var handler = new SlowHandler();
        var status = new RecordingStatus();
        var provider = Provider(Settings(extra: ",\"timeout\":0.5"), handler);
        await ProviderRunner.RunUntilAsync(provider, new SensorHub(), () => status.Last?.Health == ProviderHealth.Degraded, status: status);
        Assert.Contains("No response within 0.5 s", status.Last!.Value.Message);
    }

    [Fact]
    public async Task Cancelling_stops_a_request_in_flight()
    {
        var handler = new SlowHandler();
        var provider = Provider(Settings(extra: ",\"timeout\":60"), handler);
        var hub = new SensorHub();

        // RunUntilAsync cancels as soon as the request has started and then awaits the provider: a hang here is the failure.
        await ProviderRunner.RunUntilAsync(provider, hub, () => handler.Started, timeout: TimeSpan.FromSeconds(10));
        Assert.True(handler.Started);
    }

    [Theory]
    [InlineData("""{"interval":0.05}""", "URL is not set")]
    [InlineData("""{"url":"ftp://example.test/x","interval":0.05}""", "not a valid http or https address")]
    [InlineData("""{"url":"not a url","interval":0.05}""", "not a valid http or https address")]
    public async Task A_missing_or_invalid_url_is_reported_and_nothing_is_requested(string json, string expected)
    {
        var handler = FakeHttpHandler.Json("{}");
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(Provider(json, handler), new SensorHub(), () => status.Last?.Health == ProviderHealth.Degraded, status: status);

        Assert.Contains(expected, status.Last!.Value.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unusable_rows_are_skipped_and_reported_while_the_good_ones_work()
    {
        var handler = FakeHttpHandler.Json("""{"a":1}""");
        var fields = """[{"id":"good","path":"$.a"},{"id":"bad","path":"$.a["},{"id":"empty","path":""}]""";
        var extra = ",\"headers\":[{\"name\":\"Bad Header\",\"value\":\"x\"}]";
        var status = new RecordingStatus();
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(fields, extra), handler),
            hub,
            () => hub.GetNumber("weather/good") == 1 && status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        var message = status.Last!.Value.Message!;
        Assert.Contains("Field 2", message);
        Assert.Contains("Field 3", message);
        Assert.Contains("Bad Header", message);
        Assert.Null(hub.Get("weather/bad"));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        private volatile bool _started;

        public bool Started => _started;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

public class HttpJsonHelperTests
{
    private static HttpClient Client(Func<RecordedRequest, HttpResponseMessage> respond) => new(new FakeHttpHandler(respond));

    private static Task<System.Text.Json.Nodes.JsonNode?> Send(HttpClient client, int maxBytes = HttpJson.DefaultMaxBytes, double timeout = 5) =>
        HttpJson.SendAsync(client, new HttpRequestMessage(HttpMethod.Get, "https://x.test/"), TimeSpan.FromSeconds(timeout), TestContext.Current.CancellationToken, maxBytes);

    [Fact]
    public async Task Parses_a_successful_response()
    {
        var node = await Send(Client(_ => FakeHttpHandler.JsonResponse("""{"a":[1,2]}""")));
        Assert.Equal(2, node!["a"]![1]!.GetValue<int>());
    }

    [Fact]
    public async Task Tolerates_a_byte_order_mark_comments_and_trailing_commas()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{ /* note */ \"a\": 1, }")).ToArray();
        var node = await Send(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Non_success_status_codes_throw_with_the_code(HttpStatusCode status, string expected)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Send(Client(_ => FakeHttpHandler.JsonResponse("{}", status))));
        Assert.Contains(expected, error.Message);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_response_declared_too_large()
    {
        var content = new StringContent(new string('x', 100));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Send(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }), maxBytes: 10));
        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public async Task Rejects_a_streamed_response_that_grows_past_the_limit()
    {
        // A forward-only stream has no known length (chunked transfer), so the cap has to be enforced while reading.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Send(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnlyStream(new byte[100])) }), maxBytes: 10));
        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public async Task A_streamed_response_within_the_limit_is_read_completely()
    {
        var json = Encoding.UTF8.GetBytes("""{"a":[1,2,3]}""");
        var node = await Send(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnlyStream(json)) }), maxBytes: 100);
        Assert.Equal(3, node!["a"]!.AsArray().Count);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Rejects_documents_that_are_not_json()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Send(Client(_ => FakeHttpHandler.JsonResponse("not json"))));
        Assert.Contains("not valid JSON", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => Send(Client(_ => FakeHttpHandler.JsonResponse(""))));
    }

    [Fact]
    public async Task Reports_a_timeout_as_a_timeout()
    {
        var client = new HttpClient(new HangingHandler());
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Send(client, timeout: 0.2));
        Assert.Contains("0.2 s", error.Message);
    }

    [Fact]
    public async Task Cancellation_is_not_confused_with_a_timeout()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var client = new HttpClient(new HangingHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://x.test/");

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpJson.SendAsync(client, request, TimeSpan.FromSeconds(30), cts.Token));
        Assert.IsNotType<TimeoutException>(error);
    }

    [Fact]
    public async Task The_timeout_does_not_depend_on_the_clients_own_timeout()
    {
        // A shared client may have any HttpClient.Timeout; the per-request timeout is what counts.
        var client = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromHours(1) };
        await Assert.ThrowsAsync<TimeoutException>(() => Send(client, timeout: 0.1));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
