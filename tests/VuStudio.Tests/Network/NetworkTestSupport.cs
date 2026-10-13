using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Network;

/// <summary>A scripted HTTP server: the first route whose text is contained in "METHOD path?query" answers.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);

    private readonly List<(string Match, Func<Seen, FakeReply> Reply)> _routes = [];
    private readonly List<Seen> _seen = [];

    public IReadOnlyList<Seen> Requests => _seen;

    public sealed record FakeReply(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers = null, Exception? Fail = null);

    public FakeHttp Route(string match, string body, HttpStatusCode status = HttpStatusCode.OK, Dictionary<string, string>? headers = null) =>
        Route(match, _ => new FakeReply(status, body, headers));

    public FakeHttp Route(string match, Func<Seen, FakeReply> reply)
    {
        _routes.Insert(0, (match, reply)); // later routes override earlier ones with the same text
        return this;
    }

    public FakeHttp Fail(string match, Exception? error = null) => Route(match, _ => new FakeReply(0, "", Fail: error ?? new HttpRequestException("Connection refused")));

    /// <summary>A trailing '$' in the route text anchors it to the end of the request (so "/mem$" does not match "/memswap").</summary>
    private static bool Matches(string text, string match) =>
        match.EndsWith('$') ? text.EndsWith(match[..^1], StringComparison.Ordinal) : text.Contains(match, StringComparison.Ordinal);

    public int Count(string match) => _seen.Count(s => Matches(s.Method + " " + s.Uri.PathAndQuery, match));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new Seen(request.Method, request.RequestUri!, body, headers);
        lock (_seen)
        {
            _seen.Add(seen);
        }

        var key = request.Method + " " + request.RequestUri!.PathAndQuery;
        foreach (var (match, reply) in _routes)
        {
            if (!Matches(key, match) && !Matches(request.RequestUri.AbsoluteUri, match))
            {
                continue;
            }

            var r = reply(seen);
            if (r.Fail is not null)
            {
                throw r.Fail;
            }

            var response = new HttpResponseMessage(r.Status) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json"), RequestMessage = request };
            foreach (var (name, value) in r.Headers ?? [])
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found"), RequestMessage = request };
    }
}

/// <summary>A running provider under test: a hub, a manual clock and the status reports, driven one poll at a time.</summary>
internal sealed class Harness
{
    public Harness(Func<TimeProvider, NetworkPollingProvider> create, string providerId = "p")
    {
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        Hub = new SensorHub(Time);
        Status = new RecordingStatus();
        ProviderId = providerId;
        Provider = create(Time);
        Sink = Hub.CreateSink(providerId);
        Provider.Attach(Sink, Status);
    }

    public string ProviderId { get; }

    public ManualTimeProvider Time { get; }

    public SensorHub Hub { get; }

    public RecordingStatus Status { get; }

    public ISensorSink Sink { get; }

    public NetworkPollingProvider Provider { get; }

    /// <summary>Runs one poll; returns the delay the provider asks for before the next.</summary>
    public async Task<TimeSpan> StepAsync() => await Provider.StepAsync(CancellationToken.None);

    public double? Number(string localId) => Hub.GetNumber(ProviderId + "/" + localId);

    public string? Text(string localId) => Hub.GetText(ProviderId + "/" + localId);

    public SensorInfo? Info(string localId) => Hub.Get(ProviderId + "/" + localId)?.Info;

    public bool Has(string localId) => Hub.Get(ProviderId + "/" + localId) is not null;

    public ProviderHealth Health => Status.Last?.Health ?? ProviderHealth.Starting;

    public string? Message => Status.Last?.Message;

    public IEnumerable<string> LocalIds => Hub.GetCatalog().Select(i => i.Id[(ProviderId.Length + 1)..]);

    /// <summary>Every sensor must have a valid local id, a name, a known icon and a canonical or declared unit.</summary>
    public void AssertCatalogIsSound()
    {
        var icons = NetworkAssets.IconIds();
        foreach (var info in Hub.GetCatalog())
        {
            var local = info.Id[(ProviderId.Length + 1)..];
            Assert.True(VuStudio.Sources.Common.LocalIds.IsValid(local), "bad local id: " + local);
            Assert.False(string.IsNullOrWhiteSpace(info.Name), "no name: " + local);
            if (icons is not null)
            {
                Assert.True(info.Icon is not null && icons.Contains(info.Icon), $"unknown icon '{info.Icon}' on {local}");
            }

            if (info.ValueKind == SensorValueKind.Text)
            {
                Assert.Equal(Units.None, info.Unit);
            }
        }
    }
}

internal static class NetworkAssets
{
    private static readonly Lazy<HashSet<string>?> Icons = new(Load);

    public static HashSet<string>? IconIds() => Icons.Value;

    private static HashSet<string>? Load()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "src", "VuStudio.Rendering", "Assets", "Icons", "icons.json");
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToHashSet();
            }
        }

        return null;
    }
}

internal static class Net
{
    public static HttpClient Client(FakeHttp http) => new(http, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public static Microsoft.Extensions.Logging.ILogger Log => NullLogger.Instance;

    /// <summary>Creates a provider through its factory, with a fake HTTP server and a manual clock.</summary>
    public static Harness Make(IProviderFactory factory, string settingsJson, FakeHttp http, string id = "p") =>
        new(time => (NetworkPollingProvider)factory.Create(
            TestConfig.For(factory.Descriptor.Type, settingsJson, id),
            TestServices.With(s =>
            {
                s.AddSingleton(time);
                s.AddSingleton(Client(http));
            })), id);
}
