using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Sensors;
using VuStudio.Server.Api;

namespace VuStudio.Server.Live;

/// <summary>
/// Serves <c>/api/live</c>: pushes engine state (≤ 10 Hz), batched sensor values (2 Hz, for subscribed
/// sensors), catalog/config change hints and notifications to UI clients.
/// </summary>
public sealed class LiveHub(
    DialEngine engine,
    StateBuilder state,
    SensorHub hub,
    ConfigStore config,
    NotificationHub notifications,
    ILogger<LiveHub> logger)
{
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ValuesInterval = TimeSpan.FromMilliseconds(500);

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var subscription = notifications.Subscribe();
        var client = new ClientState();
        var sendLock = new SemaphoreSlim(1, 1);

        var receive = ReceiveLoopAsync(socket, client, cts.Token);
        try
        {
            await SendAsync(socket, sendLock, new { type = "hello", revision = config.Current.Revision, catalogVersion = hub.CatalogVersion, version = AppInfo.Version }, cts.Token).ConfigureAwait(false);

            long lastStateVersion = -1;
            var lastValuesVersion = hub.Version;
            var lastCatalog = hub.CatalogVersion;
            var lastRevision = config.Current.Revision;
            var lastValuesSent = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(StateInterval);

            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open && !receive.IsCompleted)
            {
                if (engine.SnapshotVersion != lastStateVersion)
                {
                    lastStateVersion = engine.SnapshotVersion;
                    await SendAsync(socket, sendLock, new { type = "state", data = state.Build() }, cts.Token).ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                if (now - lastValuesSent >= ValuesInterval)
                {
                    lastValuesSent = now;
                    var changed = hub.GetChangedSince(lastValuesVersion, out var current);
                    lastValuesVersion = current;
                    var filtered = changed.Where(client.Wants).Select(ToValue).ToList();
                    if (filtered.Count > 0)
                    {
                        await SendAsync(socket, sendLock, new { type = "values", data = filtered }, cts.Token).ConfigureAwait(false);
                    }
                }

                if (hub.CatalogVersion != lastCatalog)
                {
                    lastCatalog = hub.CatalogVersion;
                    await SendAsync(socket, sendLock, new { type = "catalog", catalogVersion = lastCatalog }, cts.Token).ConfigureAwait(false);
                }

                if (config.Current.Revision != lastRevision)
                {
                    lastRevision = config.Current.Revision;
                    await SendAsync(socket, sendLock, new { type = "config", revision = lastRevision }, cts.Token).ConfigureAwait(false);
                }

                while (subscription.Reader.TryRead(out var notification))
                {
                    await SendAsync(socket, sendLock, new { type = "notification", data = notification }, cts.Token).ConfigureAwait(false);
                }

                await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }

            sendLock.Dispose();
        }
    }

    private static object ToValue(SensorSnapshot s) => new
    {
        id = s.Id,
        v = s.Value,
        t = s.Text,
        s = s.IsStale,
        u = s.Updated,
    };

    private async Task ReceiveLoopAsync(WebSocket socket, ClientState client, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReadMessageAsync(socket, buffer, ct).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "subscribe" && root.TryGetProperty("sensors", out var sensors))
                {
                    client.SetSubscription(sensors);
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Ignoring malformed live message");
            }
        }
    }

    internal static async Task<string?> ReadMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1024 * 1024)
            {
                throw new WebSocketException("Message too large");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    internal static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Compact);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    internal static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private sealed class ClientState
    {
        private volatile HashSet<string>? _sensors = [];
        private volatile bool _all;

        public bool Wants(SensorSnapshot snapshot) => _all || (_sensors?.Contains(snapshot.Id) ?? false);

        public void SetSubscription(JsonElement sensors)
        {
            if (sensors.ValueKind == JsonValueKind.String && sensors.GetString() == "*")
            {
                _all = true;
                return;
            }

            if (sensors.ValueKind == JsonValueKind.Array)
            {
                _all = false;
                _sensors = sensors.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
    }
}
