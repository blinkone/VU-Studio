using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Server.Live;

/// <summary>
/// Serves <c>/api/session</c> for the tray app (the user-session agent). The tray publishes
/// <c>session/*</c> sensors and the output of user-session providers (scripts); the service sends it
/// notifications and the list of user-session providers to run.
/// </summary>
public sealed class SessionChannel(
    SensorHub hub,
    SessionState session,
    ConfigStore config,
    ProviderManager providers,
    NotificationHub notifications,
    ILogger<SessionChannel> logger)
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    public int ConnectedClients => _clients.Count;

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var client = new Client(socket);
        var id = Guid.NewGuid();
        _clients[id] = client;
        using var subscription = notifications.Subscribe();
        logger.LogInformation("Desktop app connected to the session channel");

        var notify = Task.Run(async () =>
        {
            await foreach (var n in subscription.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await client.SendAsync(new { type = "notify", title = n.Title, message = n.Message, icon = n.Icon }, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        try
        {
            await SendProvidersAsync(client, cts.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await LiveHub.ReadMessageAsync(socket, buffer, cts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                Handle(message);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            await cts.CancelAsync().ConfigureAwait(false);
            await LiveHub.CloseQuietlyAsync(socket).ConfigureAwait(false);
            try
            {
                await notify.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            client.Dispose();
            logger.LogInformation("Desktop app disconnected from the session channel");
        }
    }

    /// <summary>Sends the current user-session provider list to every connected tray (after config changes).</summary>
    public async Task BroadcastProvidersAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                await SendProvidersAsync(client, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private Task SendProvidersAsync(Client client, CancellationToken ct)
    {
        var items = UserSessionProviders().ToList();
        return client.SendAsync(new { type = "providers", items }, ct);
    }

    private IEnumerable<ProviderInstanceConfig> UserSessionProviders() =>
        config.Current.Providers.Where(p => p.Enabled && providers.GetFactory(p.Type)?.Descriptor.RunsInUserSession == true);

    private bool IsAllowedProvider(string providerId) =>
        providerId == WellKnownSensors.SessionProvider || UserSessionProviders().Any(p => p.Id == providerId);

    private void Handle(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var provider = root.TryGetProperty("provider", out var p) ? p.GetString() ?? WellKnownSensors.SessionProvider : WellKnownSensors.SessionProvider;
            if (type is "define" or "values" or "remove" or "status" && !IsAllowedProvider(provider))
            {
                logger.LogWarning("Session channel: rejected data for provider '{Provider}'", provider);
                return;
            }

            var sink = hub.CreateSink(provider);
            switch (type)
            {
                case "define" when root.TryGetProperty("sensors", out var sensors):
                    foreach (var info in sensors.Deserialize<List<SensorInfo>>(Json.Compact) ?? [])
                    {
                        // session/locked belongs to SessionState; a tray definition would change its staleness.
                        if (!string.IsNullOrWhiteSpace(info.Id) && !IsLockedSensor(provider, info.Id))
                        {
                            sink.Define(info);
                        }
                    }

                    break;
                case "values" when root.TryGetProperty("values", out var values):
                    foreach (var property in values.EnumerateObject())
                    {
                        Publish(sink, provider, property.Name, property.Value);
                    }

                    break;
                case "remove" when root.TryGetProperty("sensors", out var removed) && removed.ValueKind == JsonValueKind.Array:
                    foreach (var item in removed.EnumerateArray())
                    {
                        if (item.GetString() is { Length: > 0 } localId && !IsLockedSensor(provider, localId))
                        {
                            sink.Remove(localId);
                        }
                    }

                    break;
                case "status" when provider != WellKnownSensors.SessionProvider
                                   && root.TryGetProperty("health", out var h)
                                   && Enum.TryParse<ProviderHealth>(h.GetString(), ignoreCase: true, out var health):
                    providers.ReportUserSessionStatus(
                        provider,
                        health,
                        root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
                    break;
                case "hello":
                    logger.LogInformation("Desktop app: {Message}", message);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Ignoring malformed session message");
        }
    }

    private static bool IsLockedSensor(string provider, string localId) =>
        provider == WellKnownSensors.SessionProvider && localId == "locked";

    private void Publish(ISensorSink sink, string provider, string localId, JsonElement value)
    {
        if (provider == WellKnownSensors.SessionProvider && localId == "locked")
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                session.ReportFromTray(value.GetDouble() >= 0.5);
            }

            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                sink.Publish(localId, value.GetDouble());
                break;
            case JsonValueKind.String:
                sink.PublishText(localId, value.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                sink.Publish(localId, value.GetBoolean() ? 1 : 0);
                break;
            default:
                sink.Invalidate(localId);
                break;
        }
    }

    private sealed class Client(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Task SendAsync(object payload, CancellationToken ct) => LiveHub.SendAsync(socket, _sendLock, payload, ct);

        public void Dispose() => _sendLock.Dispose();
    }
}
