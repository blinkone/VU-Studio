using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Session;

/// <summary>
/// Client of the service's session channel (<c>ws://127.0.0.1:{port}/api/session</c>, see docs/api.md).
/// <list type="bullet">
/// <item>Never sets an <c>Origin</c> header: the service only accepts non-browser loopback clients.</item>
/// <item>Reconnects forever with backoff (1 s → 30 s) and sends <c>hello</c> on every connection.</item>
/// <item><see cref="Define"/> and <c>Publish</c> only record state. A background loop sends it in batches,
///   at most one flush per <see cref="SessionClientOptions.MinSendInterval"/> (≈20 per second), latest value wins per id.</item>
/// <item>After a reconnect every definition, last value and status is sent again, so the service never misses state.</item>
/// </list>
/// Events are raised on a background thread; UI handlers must marshal to their own thread.
/// </summary>
public sealed class SessionClient : ISessionPublisher, IAsyncDisposable
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SessionClientOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProviderState> _providers = new(StringComparer.Ordinal);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _nudge = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private Uri _endpoint;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _connection;
    private Task? _loop;
    private volatile bool _connected;
    private long _messagesSent;
    private int _failures;

    public SessionClient(Uri endpoint, SessionClientOptions? options = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _endpoint = Validate(endpoint);
        _options = options ?? new SessionClientOptions();
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A notification arrived from the service (raised on a background thread).</summary>
    public event EventHandler<SessionNotification>? NotificationReceived;

    /// <summary>The service sent the user-session providers to run (raised on a background thread).</summary>
    public event EventHandler<IReadOnlyList<ProviderInstanceConfig>>? ProvidersReceived;

    /// <summary>The connection was established (<c>true</c>) or lost (<c>false</c>).</summary>
    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected => _connected;

    /// <summary>Messages sent since the client was created (diagnostics and tests).</summary>
    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    public Uri Endpoint
    {
        get
        {
            lock (_lock)
            {
                return _endpoint;
            }
        }
    }

    public static Uri EndpointFor(int port) => new($"ws://127.0.0.1:{port}/api/session");

    /// <summary>Starts the connection loop. Idempotent.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Points the client at another endpoint (the service port changed) and reconnects at once.</summary>
    public void SetEndpoint(Uri endpoint)
    {
        Validate(endpoint);
        CancellationTokenSource? connection;
        lock (_lock)
        {
            if (_endpoint == endpoint)
            {
                return;
            }

            _endpoint = endpoint;
            connection = _connection;
        }

        try
        {
            connection?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Nudge();
    }

    /// <summary>Skips the current reconnect delay (e.g. the service was just seen coming up).</summary>
    public void Nudge() => _nudge.Writer.TryWrite(true);

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_lock)
        {
            loop = _loop;
            stop = _stop;
            _loop = null;
            _stop = null;
        }

        if (loop is null || stop is null)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Session channel did not stop within 5 s");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stop.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ---- publishing (thread-safe, never blocks on I/O) --------------------------------------------

    public void Define(string provider, IEnumerable<SensorInfo> sensors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(sensors);
        var changed = false;
        lock (_lock)
        {
            var state = GetState(provider);
            foreach (var info in sensors)
            {
                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    continue;
                }

                var localId = NormalizeId(provider, info.Id);
                var normalized = info.Id == localId ? info : info with { Id = localId };
                state.Definitions[localId] = normalized;
                state.DirtyDefinitions.Add(localId);
                state.PendingRemovals.Remove(localId);
                changed = true;
            }
        }

        if (changed)
        {
            Signal();
        }
    }

    public void Publish(string provider, string localId, double value) =>
        SetValue(provider, localId, double.IsFinite(value) ? SessionValue.FromNumber(value) : SessionValue.Null);

    public void Publish(string provider, string localId, string? text) =>
        SetValue(provider, localId, text is null ? SessionValue.Null : SessionValue.FromText(text));

    public void Invalidate(string provider, string localId) => SetValue(provider, localId, SessionValue.Null);

    public void Remove(string provider, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        lock (_lock)
        {
            RemoveLocked(GetState(provider), NormalizeId(provider, localId));
        }

        Signal();
    }

    public void ClearProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        lock (_lock)
        {
            if (!_providers.TryGetValue(provider, out var state))
            {
                return;
            }

            foreach (var id in state.Definitions.Keys.Concat(state.Values.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                RemoveLocked(state, id);
            }

            state.Status = null;
            state.StatusDirty = false;
        }

        Signal();
    }

    public void ReportStatus(string provider, ProviderHealth health, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        lock (_lock)
        {
            var state = GetState(provider);
            if (state.Status is { } current && current.Health == health && current.Message == message)
            {
                return;
            }

            state.Status = (health, message);
            state.StatusDirty = true;
        }

        Signal();
    }

    private void SetValue(string provider, string localId, SessionValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        lock (_lock)
        {
            var state = GetState(provider);
            var id = NormalizeId(provider, localId);
            state.Values[id] = value;
            state.PendingValues[id] = value;
        }

        Signal();
    }

    private static void RemoveLocked(ProviderState state, string id)
    {
        var known = state.Definitions.Remove(id) | state.Values.Remove(id);
        state.DirtyDefinitions.Remove(id);
        if (known)
        {
            state.PendingValues[id] = SessionValue.Null;
            state.PendingRemovals.Add(id);
        }
    }

    private ProviderState GetState(string provider)
    {
        if (!_providers.TryGetValue(provider, out var state))
        {
            state = new ProviderState();
            _providers[provider] = state;
        }

        return state;
    }

    /// <summary>
    /// The service strips a leading <c>{provider}/</c> anyway, but it special-cases the exact local id
    /// <c>locked</c>, so ids are normalised here.
    /// </summary>
    internal static string NormalizeId(string provider, string id)
    {
        var trimmed = id.Trim();
        var prefix = provider + "/";
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.Length > prefix.Length ? trimmed[prefix.Length..] : trimmed;
    }

    private void Signal() => _wake.Writer.TryWrite(true);

    private static Uri Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss"))
        {
            throw new ArgumentException("The session endpoint must be a ws:// or wss:// URI.", nameof(endpoint));
        }

        return endpoint;
    }

    // ---- connection loop ------------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken stop)
    {
        var backoff = new Backoff(_options.InitialReconnectDelay, _options.MaxReconnectDelay);
        while (!stop.IsCancellationRequested)
        {
            long? connectedAt = null;
            Uri endpoint;
            // Cancelled by SetEndpoint only; stopping goes through 'stop' so the close handshake can complete.
            var connection = new CancellationTokenSource();
            lock (_lock)
            {
                endpoint = _endpoint;
                _connection = connection;
            }

            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.KeepAliveInterval = _options.KeepAlive;
                socket.Options.KeepAliveTimeout = _options.KeepAlive;

                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop, connection.Token))
                {
                    connect.CancelAfter(_options.ConnectTimeout);
                    await socket.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
                }

                connectedAt = _time.GetTimestamp();
                if (_failures > 0)
                {
                    _logger.LogInformation("Session channel connected to {Endpoint} after {Failures} failed attempts", endpoint, _failures);
                }
                else
                {
                    _logger.LogInformation("Session channel connected to {Endpoint}", endpoint);
                }

                _failures = 0;
                SetConnected(true);
                await RunConnectionAsync(socket, stop, connection.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                if (connectedAt is not null)
                {
                    _logger.LogInformation("Session channel connection lost: {Message}", ex.Message);
                }
                else if (_failures++ == 0)
                {
                    _logger.LogInformation("Session channel unavailable at {Endpoint}: {Message}", endpoint, ex.Message);
                }
                else
                {
                    _logger.LogDebug("Session channel still unavailable ({Failures}): {Message}", _failures, ex.Message);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (_connection == connection)
                    {
                        _connection = null;
                    }
                }

                connection.Dispose();
                SetConnected(false);
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            if (connectedAt is { } at && _time.GetElapsedTime(at) >= _options.StableConnection)
            {
                backoff.Reset();
            }

            try
            {
                await DelayAsync(backoff.Next(), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var delayTask = Task.Delay(delay, _time, cts.Token);
        var nudgeTask = _nudge.Reader.WaitToReadAsync(cts.Token).AsTask();
        await Task.WhenAny(delayTask, nudgeTask).ConfigureAwait(false);
        _nudge.Reader.TryRead(out _);
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(delayTask, nudgeTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        stop.ThrowIfCancellationRequested();
    }

    private async Task RunConnectionAsync(ClientWebSocket socket, CancellationToken stop, CancellationToken connection)
    {
        // The receive side has its own token so that a stop can still complete the close handshake.
        using var receiveCts = new CancellationTokenSource();
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(stop, connection);
        await SendAsync(socket, BuildHello(), sendCts.Token).ConfigureAwait(false);
        MarkAllForResync();

        var receive = ReceiveLoopAsync(socket, receiveCts.Token);
        var send = SendLoopAsync(socket, sendCts.Token);
        using var abort = connection.Register(() => receiveCts.Cancel());

        var first = await Task.WhenAny(receive, send).ConfigureAwait(false);
        await sendCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            if (first == send && ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Session channel send failed");
            }
        }

        if (first == send && socket.State == WebSocketState.Open)
        {
            // Stopping (or the endpoint changed): close politely, then give the service a moment to answer.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, stop.IsCancellationRequested ? "bye" : null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        try
        {
            await receive.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            await receiveCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
        }

        stop.ThrowIfCancellationRequested();
        connection.ThrowIfCancellationRequested();
        throw new WebSocketException("The service closed the session channel.");
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken ct)
    {
        long lastFlush = 0;
        while (true)
        {
            await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
            _wake.Reader.TryRead(out _);
            if (lastFlush != 0)
            {
                var wait = _options.MinSendInterval - _time.GetElapsedTime(lastFlush);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                }
            }

            lastFlush = _time.GetTimestamp();
            foreach (var message in TakePendingMessages())
            {
                await SendAsync(socket, message, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(WebSocket socket, byte[] message, CancellationToken ct)
    {
        await socket.SendAsync(message, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _messagesSent);
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > 4 * 1024 * 1024)
                {
                    throw new WebSocketException("Message from the service is too large.");
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal void HandleMessage(string text)
    {
        string? type;
        SessionNotification? notification = null;
        List<ProviderInstanceConfig>? providers = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "notify":
                    notification = new SessionNotification(
                        GetString(root, "title") ?? "VU Studio",
                        GetString(root, "message") ?? "",
                        GetString(root, "icon"));
                    break;
                case "providers":
                    providers = root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                        ? items.Deserialize<List<ProviderInstanceConfig>>(Json.Compact) ?? []
                        : [];
                    providers.RemoveAll(p => p is null);
                    break;
                default:
                    _logger.LogDebug("Ignoring session message of type '{Type}'", type);
                    return;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring malformed session message");
            return;
        }

        try
        {
            if (notification is not null)
            {
                NotificationReceived?.Invoke(this, notification);
            }

            if (providers is not null)
            {
                ProvidersReceived?.Invoke(this, providers);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session message handler for '{Type}' failed", type);
        }

        static string? GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected)
        {
            return;
        }

        _connected = connected;
        try
        {
            ConnectionChanged?.Invoke(this, connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConnectionChanged handler failed");
        }
    }

    // ---- batching ---------------------------------------------------------------------------------

    private void MarkAllForResync()
    {
        lock (_lock)
        {
            foreach (var state in _providers.Values)
            {
                state.DirtyDefinitions.UnionWith(state.Definitions.Keys);
                foreach (var (id, value) in state.Values)
                {
                    state.PendingValues[id] = value;
                }

                state.StatusDirty = state.Status is not null;
            }
        }

        Signal();
    }

    /// <summary>Takes everything pending and turns it into wire messages (defines first, then values, removals, status).</summary>
    internal List<byte[]> TakePendingMessages()
    {
        var work = new List<Batch>();
        lock (_lock)
        {
            foreach (var (provider, state) in _providers)
            {
                if (state.DirtyDefinitions.Count == 0 && state.PendingValues.Count == 0 && state.PendingRemovals.Count == 0 && !state.StatusDirty)
                {
                    continue;
                }

                var batch = new Batch(
                    provider,
                    state.DirtyDefinitions.Select(id => state.Definitions[id]).ToList(),
                    state.PendingValues.ToList(),
                    state.PendingRemovals.ToList(),
                    state.StatusDirty ? state.Status : null);
                state.DirtyDefinitions.Clear();
                state.PendingValues.Clear();
                state.PendingRemovals.Clear();
                state.StatusDirty = false;
                work.Add(batch);
            }
        }

        var messages = new List<byte[]>();
        var max = Math.Max(1, _options.MaxItemsPerMessage);
        foreach (var batch in work)
        {
            foreach (var chunk in batch.Definitions.Chunk(max))
            {
                messages.Add(Write(w =>
                {
                    w.WriteString("type", "define");
                    w.WriteString("provider", batch.Provider);
                    w.WritePropertyName("sensors");
                    JsonSerializer.Serialize(w, chunk, Json.Compact);
                }));
            }

            foreach (var chunk in batch.Values.Chunk(max))
            {
                messages.Add(Write(w =>
                {
                    w.WriteString("type", "values");
                    w.WriteString("provider", batch.Provider);
                    w.WriteStartObject("values");
                    foreach (var (id, value) in chunk)
                    {
                        switch (value.Kind)
                        {
                            case SessionValueKind.Number:
                                w.WriteNumber(id, value.Number);
                                break;
                            case SessionValueKind.Text:
                                w.WriteString(id, value.Text);
                                break;
                            default:
                                w.WriteNull(id);
                                break;
                        }
                    }

                    w.WriteEndObject();
                }));
            }

            // Not understood by the service yet (it ignores unknown types); removed sensors were already invalidated above.
            foreach (var chunk in batch.Removals.Chunk(max))
            {
                messages.Add(Write(w =>
                {
                    w.WriteString("type", "remove");
                    w.WriteString("provider", batch.Provider);
                    w.WriteStartArray("sensors");
                    foreach (var id in chunk)
                    {
                        w.WriteStringValue(id);
                    }

                    w.WriteEndArray();
                }));
            }

            if (batch.Status is { } status)
            {
                messages.Add(Write(w =>
                {
                    w.WriteString("type", "status");
                    w.WriteString("provider", batch.Provider);
                    w.WriteString("health", JsonNamingPolicy.CamelCase.ConvertName(status.Health.ToString()));
                    if (status.Message is null)
                    {
                        w.WriteNull("message");
                    }
                    else
                    {
                        w.WriteString("message", status.Message);
                    }
                }));
            }
        }

        return messages;
    }

    private byte[] BuildHello() => Write(w =>
    {
        w.WriteString("type", "hello");
        w.WriteString("user", _options.User);
        w.WriteString("machine", _options.Machine);
        w.WriteString("version", _options.Version);
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed record Batch(
        string Provider,
        List<SensorInfo> Definitions,
        List<KeyValuePair<string, SessionValue>> Values,
        List<string> Removals,
        (ProviderHealth Health, string? Message)? Status);

    private sealed class ProviderState
    {
        public Dictionary<string, SensorInfo> Definitions { get; } = new(StringComparer.Ordinal);

        /// <summary>Last value of every sensor, re-sent after a reconnect.</summary>
        public Dictionary<string, SessionValue> Values { get; } = new(StringComparer.Ordinal);

        public HashSet<string> DirtyDefinitions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SessionValue> PendingValues { get; } = new(StringComparer.Ordinal);

        public HashSet<string> PendingRemovals { get; } = new(StringComparer.Ordinal);

        public (ProviderHealth Health, string? Message)? Status { get; set; }

        public bool StatusDirty { get; set; }
    }
}

internal enum SessionValueKind : byte
{
    Null,
    Number,
    Text,
}

internal readonly record struct SessionValue(SessionValueKind Kind, double Number, string? Text)
{
    public static readonly SessionValue Null = new(SessionValueKind.Null, 0, null);

    public static SessionValue FromNumber(double value) => new(SessionValueKind.Number, value, null);

    public static SessionValue FromText(string text) => new(SessionValueKind.Text, 0, text);
}
