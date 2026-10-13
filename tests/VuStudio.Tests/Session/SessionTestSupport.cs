using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Session;

namespace VuStudio.Tests.Session;

/// <summary>A Kestrel host on a free loopback port for session-channel and REST tests.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public Uri SessionUri => SessionClient.EndpointFor(Port);

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public static async Task<TestHost> StartAsync(Action<WebApplication> map, Action<IServiceCollection>? services = null, int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseWebSockets();
        map(app);
        await app.StartAsync();
        var actual = new Uri(app.Urls.First()).Port;
        return new TestHost(app, actual);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A raw WebSocket endpoint that records every message from the client and can talk back.</summary>
internal sealed class RecordingSessionServer
{
    private readonly ConcurrentQueue<Received> _messages = new();
    private readonly ConcurrentDictionary<int, WebSocket> _sockets = new();
    private int _connections;

    public sealed record Received(int Connection, TimeSpan At, JsonElement Json)
    {
        public string Type => Json.GetProperty("type").GetString()!;
    }

    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Headers of the latest upgrade request.</summary>
    public IHeaderDictionary? LastHeaders { get; private set; }

    public IReadOnlyList<Received> Messages => _messages.ToList();

    public void Map(WebApplication app) => app.Map("/api/session", async (HttpContext http) =>
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest();
        }

        LastHeaders = http.Request.Headers;
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var id = Interlocked.Increment(ref _connections);
        _sockets[id] = socket;
        var buffer = new byte[64 * 1024];
        try
        {
            using var ms = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, http.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var json = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                    _messages.Enqueue(new Received(id, Clock.Elapsed, json));
                    ms.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }

        return Results.Empty;
    });

    public async Task SendAsync(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        foreach (var socket in _sockets.Values)
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    /// <summary>Drops every connection the way a crashing service would (no close handshake).</summary>
    public void AbortAll()
    {
        foreach (var socket in _sockets.Values)
        {
            socket.Abort();
        }
    }

    public IReadOnlyList<Received> OfType(string type, int? connection = null) =>
        Messages.Where(m => m.Type == type && (connection is null || m.Connection == connection)).ToList();

    /// <summary>Latest value of every id in the <c>values</c> messages of one provider.</summary>
    public Dictionary<string, JsonElement> LatestValues(string provider, int? connection = null)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var m in OfType("values", connection).Where(m => m.Json.GetProperty("provider").GetString() == provider))
        {
            foreach (var p in m.Json.GetProperty("values").EnumerateObject())
            {
                result[p.Name] = p.Value;
            }
        }

        return result;
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var limit = Stopwatch.StartNew();
        var max = timeout ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (limit.Elapsed > max)
            {
                Assert.Fail($"Timed out after {max.TotalSeconds:0.#} s waiting for {because ?? "condition"}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Records every call for assertions.</summary>
internal sealed class FakePublisher : ISessionPublisher
{
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToList();
            }
        }
    }

    private readonly List<(string Provider, ProviderHealth Health, string? Message)> _statuses = [];

    public IReadOnlyList<(string Provider, ProviderHealth Health, string? Message)> Statuses
    {
        get
        {
            lock (_lock)
            {
                return _statuses.ToList();
            }
        }
    }

    public void Define(string provider, IEnumerable<SensorInfo> sensors) => Add($"define {provider} {string.Join(",", sensors.Select(s => s.Id))}");

    public void Publish(string provider, string localId, double value) => Add($"publish {provider} {localId} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    public void Publish(string provider, string localId, string? text) => Add($"text {provider} {localId} {text ?? "<null>"}");

    public void Invalidate(string provider, string localId) => Add($"invalidate {provider} {localId}");

    public void Remove(string provider, string localId) => Add($"remove {provider} {localId}");

    public void ClearProvider(string provider) => Add($"clear {provider}");

    public void ReportStatus(string provider, ProviderHealth health, string? message = null)
    {
        lock (_lock)
        {
            _statuses.Add((provider, health, message));
        }

        Add($"status {provider} {health}");
    }

    public bool Has(string call) => Calls.Contains(call);

    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    private void Add(string call)
    {
        lock (_lock)
        {
            _calls.Add(call);
        }
    }
}
