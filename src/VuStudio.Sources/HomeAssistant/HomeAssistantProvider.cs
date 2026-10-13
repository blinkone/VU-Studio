using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.HomeAssistant;

/// <summary>Creates <c>homeassistant</c> providers: entity states of a Home Assistant instance (WebSocket, with REST polling as fallback).</summary>
public sealed class HomeAssistantProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "homeassistant",
        Name = L("Home Assistant", "Home Assistant"),
        Description = L(
            "Entity states from Home Assistant: sensors with units, binary sensors and switches as 0/1, text states, and attributes (entity#attribute). Needs a long-lived access token (profile > Security).",
            "Stati delle entità di Home Assistant: sensori con unità, sensori binari e interruttori come 0/1, stati di testo e attributi (entità#attributo). Serve un token di accesso a lunga durata (profilo > Sicurezza)."),
        Icon = "home-assistant",
        Group = ProviderGroup.HomeAutomation,
        Settings =
        [
            Setting.Url("url", L("Home Assistant address", "Indirizzo di Home Assistant"), help: L("For example http://homeassistant.local:8123. Use https:// for Nabu Casa or a reverse proxy.", "Ad esempio http://homeassistant.local:8123. Usa https:// per Nabu Casa o un reverse proxy."), placeholder: "http://homeassistant.local:8123", required: true),
            Setting.Secret("token", L("Long-lived access token", "Token di accesso a lunga durata"), L("Create it in your Home Assistant profile under Security. A user with limited rights is enough.", "Crealo nel tuo profilo di Home Assistant, sezione Sicurezza. Basta un utente con diritti limitati.")),
            Setting.TextList("entities", L("Entities", "Entità"), [], L("One per line, e.g. sensor.grid_power or climate.living#current_temperature. Leave empty to publish every numeric sensor.", "Una per riga, ad esempio sensor.grid_power o climate.living#current_temperature. Lascia vuoto per pubblicare tutti i sensori numerici."), "sensor.grid_power"),
            Setting.Bool("useWebSocket", L("Use WebSocket (live updates)", "Usa WebSocket (aggiornamenti in tempo reale)"), true, L("Recommended. When it cannot connect, the provider polls the REST API instead.", "Consigliato. Se non riesce a connettersi, il provider interroga l'API REST.")),
            Setting.Interval(10, min: 2, max: 3600, help: L("Seconds between reads when polling the REST API.", "Secondi tra le letture quando si interroga l'API REST.")),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the address.", "Etichetta host dei sensori. Predefinito: l'indirizzo.")),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), false),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var insecure = s.GetBool("allowInvalidCertificate");
        var (client, owner) = NetHttp.CreateClient(services, insecure);
        return new HomeAssistantProvider(
            config.Id,
            NetHttp.NormalizeBase(s.GetString("url")),
            s.GetString("token"),
            s.GetStringList("entities").Select(HaSelector.Parse).OfType<HaSelector>().ToList(),
            s.GetBool("useWebSocket", true),
            insecure,
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 10),
            client,
            owner,
            ProviderServices.Time(services),
            ProviderServices.Logger<HomeAssistantProvider>(services));
    }
}

/// <summary>Reads Home Assistant. See docs/integrations/network-sources.md section 8.</summary>
internal sealed class HomeAssistantProvider : NetworkPollingProvider
{
    private const double StaleSeconds = 86_400; // WebSocket events only arrive on change
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WebSocketRetry = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> NumericDomains = new(StringComparer.Ordinal) { "sensor", "number", "input_number", "counter" };
    private static readonly HashSet<string> SwitchDomains = new(StringComparer.Ordinal) { "binary_sensor", "switch", "light", "input_boolean", "person", "device_tracker", "lock", "cover", "fan", "automation", "remote", "siren", "humidifier" };

    private readonly Uri? _baseUri;
    private readonly string? _token;
    private readonly IReadOnlyList<HaSelector> _selectors;
    private readonly bool _useWebSocket;
    private readonly bool _allowInvalidCertificate;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly HaEntityStore _store = new();
    private readonly Dictionary<string, bool> _isText = new(StringComparer.Ordinal);
    private int _webSocketFailures;
    private TimeSpan? _webSocketRetryAt;

    public HomeAssistantProvider(string id, Uri? baseUri, string? token, IReadOnlyList<HaSelector> selectors, bool useWebSocket, bool allowInvalidCertificate, string? nameOverride, TimeSpan interval, HttpClient client, IDisposable? owner, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger, owner, StaleSeconds)
    {
        _baseUri = baseUri;
        _token = token;
        _selectors = selectors;
        _useWebSocket = useWebSocket;
        _allowInvalidCertificate = allowInvalidCertificate;
        _nameOverride = nameOverride;
        _client = client;
    }

    private string Host => _nameOverride ?? _baseUri?.Host ?? "Home Assistant";

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null)
        {
            throw new SourceUnavailableException("Enter the address of Home Assistant in the settings.");
        }

        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new SourceUnavailableException("Enter a long-lived access token in the settings.");
        }

        if (_useWebSocket && (_webSocketRetryAt is not { } retryAt || Clock >= retryAt))
        {
            try
            {
                await RunWebSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not SourceUnavailableException)
            {
                InvalidateAll();
                if (++_webSocketFailures >= 3)
                {
                    _webSocketRetryAt = Clock + WebSocketRetry;
                    _webSocketFailures = 0;
                    Logger.LogWarning("Home Assistant WebSocket failed ({Message}); polling the REST API for a while", ex.Message);
                }

                throw;
            }

            return;
        }

        await PollRestAsync(cancellationToken).ConfigureAwait(false);
        if (_useWebSocket)
        {
            Warning = "WebSocket unavailable, polling the REST API.";
        }
    }

    // ----- REST -----

    private async Task PollRestAsync(CancellationToken cancellationToken)
    {
        using var sweep = Sweep(_ => true);
        var wanted = _selectors.Select(s => s.EntityId).Distinct().ToList();
        if (wanted.Count is > 0 and <= 10)
        {
            foreach (var id in wanted)
            {
                var node = await GetRestAsync($"api/states/{id}", allowNotFound: true, cancellationToken).ConfigureAwait(false);
                if (node is null)
                {
                    Warning ??= $"Entity '{id}' was not found in Home Assistant.";
                    continue;
                }

                _store.Set(id, node);
                ApplyEntity(id);
            }

            return;
        }

        var all = await GetRestAsync("api/states", allowNotFound: false, cancellationToken).ConfigureAwait(false);
        foreach (var id in _store.LoadAll(all))
        {
            ApplyEntity(id);
        }
    }

    private async Task<JsonNode?> GetRestAsync(string path, bool allowNotFound, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NetHttp.Combine(_baseUri!, path));
        request.Headers.Authorization = NetHttp.Bearer(_token);
        var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken, maxBytes: 64 * 1024 * 1024).ConfigureAwait(false);
        if (allowNotFound && response.Status == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return NetHttp.EnsureSuccess(response, "access token").Json();
    }

    // ----- WebSocket -----

    private async Task RunWebSocketAsync(CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(NetHttp.Combine(_baseUri!, "api/websocket")) { Scheme = _baseUri!.Scheme == "https" ? "wss" : "ws" };
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (_allowInvalidCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(RequestTimeout);
            try
            {
                await socket.ConnectAsync(builder.Uri, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{_baseUri.Authority}: no response within {RequestTimeout.TotalSeconds:0} s.");
            }
            catch (WebSocketException ex)
            {
                throw new IOException($"Cannot open the WebSocket of {_baseUri.Authority}: {ex.InnerException?.Message ?? ex.Message}", ex);
            }
        }

        var inbox = Channel.CreateUnbounded<JsonNode>();
        var reader = ReadLoopAsync(socket, inbox.Writer, cancellationToken);
        try
        {
            await AuthenticateAsync(socket, inbox.Reader, cancellationToken).ConfigureAwait(false);
            await SessionAsync(socket, inbox.Reader, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", close.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                }
            }

            socket.Abort();
            await reader.ConfigureAwait(false);
        }
    }

    private static async Task ReadLoopAsync(ClientWebSocket socket, ChannelWriter<JsonNode> writer, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        writer.TryComplete(new IOException("Home Assistant closed the connection."));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 64 * 1024 * 1024)
                    {
                        throw new InvalidDataException("WebSocket message too large.");
                    }
                }
                while (!result.EndOfMessage);

                if (HttpJson.Parse(message.ToArray()) is { } node)
                {
                    writer.TryWrite(node);
                }
            }
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex is OperationCanceledException ? ex : new IOException("Home Assistant connection lost: " + ex.Message, ex));
            return;
        }

        writer.TryComplete();
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode> NextAsync(ChannelReader<JsonNode> inbox, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await inbox.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Home Assistant stopped answering.");
        }
        catch (ChannelClosedException ex)
        {
            throw ex.InnerException is OperationCanceledException ? new OperationCanceledException(cancellationToken) : ex.InnerException ?? new IOException("Home Assistant closed the connection.");
        }
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, ChannelReader<JsonNode> inbox, CancellationToken cancellationToken)
    {
        var hello = await NextAsync(inbox, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (JsonRead.Str(hello, "type") != "auth_required")
        {
            throw new InvalidDataException("This does not look like Home Assistant (no auth_required message).");
        }

        await SendAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = _token }, cancellationToken).ConfigureAwait(false);
        var reply = await NextAsync(inbox, RequestTimeout, cancellationToken).ConfigureAwait(false);
        switch (JsonRead.Str(reply, "type"))
        {
            case "auth_ok":
                return;
            case "auth_invalid":
                throw new SourceUnavailableException("Home Assistant rejected the access token: " + (JsonRead.Str(reply, "message") ?? "invalid token") + ".");
            default:
                throw new InvalidDataException("Unexpected answer to the authentication.");
        }
    }

    private async Task SessionAsync(ClientWebSocket socket, ChannelReader<JsonNode> inbox, CancellationToken cancellationToken)
    {
        var nextId = 1;
        var subscribeId = nextId++;
        var entityIds = _selectors.Select(s => s.EntityId).Distinct().ToList();
        var subscribe = new JsonObject { ["id"] = subscribeId, ["type"] = "subscribe_entities" };
        if (entityIds.Count > 0)
        {
            subscribe["entity_ids"] = new JsonArray(entityIds.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        }

        await SendAsync(socket, subscribe, cancellationToken).ConfigureAwait(false);

        var compressed = true;
        var snapshotDone = false;
        var verboseStatesId = -1;
        var pingOutstanding = false;
        while (true)
        {
            JsonNode message;
            try
            {
                message = await NextAsync(inbox, pingOutstanding ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!pingOutstanding)
            {
                pingOutstanding = true;
                await SendAsync(socket, new JsonObject { ["id"] = nextId++, ["type"] = "ping" }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            pingOutstanding = false;
            var type = JsonRead.Str(message, "type");
            var id = (int?)JsonRead.Num(message, "id");
            if (type == "result" && id == subscribeId)
            {
                if (JsonRead.Bool(message, "success") != true)
                {
                    // subscribe_entities is not in the public docs: fall back to get_states + state_changed events.
                    compressed = false;
                    Logger.LogInformation("Home Assistant does not support subscribe_entities; using state_changed events");
                    var eventsId = nextId++;
                    await SendAsync(socket, new JsonObject { ["id"] = eventsId, ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, cancellationToken).ConfigureAwait(false);
                    verboseStatesId = nextId++;
                    await SendAsync(socket, new JsonObject { ["id"] = verboseStatesId, ["type"] = "get_states" }, cancellationToken).ConfigureAwait(false);
                    subscribeId = eventsId;
                }

                continue;
            }

            if (type == "result" && id == verboseStatesId)
            {
                if (JsonRead.Bool(message, "success") != true)
                {
                    throw new IOException("Home Assistant refused get_states: " + JsonRead.Str(JsonRead.Get(message, "error"), "message"));
                }

                var ids = _store.LoadAll(JsonRead.Get(message, "result"));
                ids.ForEach(ApplyEntity);
                ReportSnapshot();
                snapshotDone = true;
                continue;
            }

            if (type != "event" || id != subscribeId)
            {
                continue; // pong, results of our pings
            }

            var ev = JsonRead.Get(message, "event");
            if (compressed)
            {
                var (changed, removed) = _store.ApplyCompressed(ev);
                foreach (var entity in changed)
                {
                    ApplyEntity(entity);
                }

                foreach (var entity in removed)
                {
                    RemoveEntity(entity);
                }

                if (!snapshotDone)
                {
                    snapshotDone = true;
                    ReportSnapshot();
                }
            }
            else if (snapshotDone || verboseStatesId < 0)
            {
                if (_store.ApplyStateChanged(ev) is { } changed)
                {
                    if (_store.States.ContainsKey(changed))
                    {
                        ApplyEntity(changed);
                    }
                    else
                    {
                        RemoveEntity(changed);
                    }
                }
            }
        }
    }

    private void ReportSnapshot()
    {
        ResetFailures();
        _webSocketFailures = 0;
        var missing = _selectors.Select(s => s.EntityId).Distinct().Where(e => !_store.States.ContainsKey(e)).ToList();
        if (missing.Count > 0)
        {
            Status.Report(ProviderHealth.Degraded, "Entity not found in Home Assistant: " + string.Join(", ", missing.Take(3)));
        }
        else
        {
            Status.Report(ProviderHealth.Ok);
        }
    }

    // ----- mapping to sensors -----

    private void RemoveEntity(string entityId)
    {
        foreach (var id in Registry.Ids.Where(i => i == LocalFor(entityId, null) || i.StartsWith(LocalFor(entityId, null) + "/", StringComparison.Ordinal)).ToList())
        {
            SetNone(id);
        }
    }

    private static string LocalFor(string entityId, string? attribute)
    {
        var path = entityId.Replace('.', '/');
        return attribute is null ? path : path + "/" + LocalIds.Slug(attribute);
    }

    /// <summary>Publishes an entity (and each of its selected attributes) according to the entity list.</summary>
    internal void ApplyEntity(string entityId)
    {
        if (!_store.States.TryGetValue(entityId, out var state))
        {
            return;
        }

        var matching = _selectors.Where(s => s.EntityId == entityId).ToList();
        if (matching.Count == 0)
        {
            if (_selectors.Count == 0 && IsAutoNumeric(entityId, state))
            {
                Publish(entityId, null, state);
            }

            return;
        }

        foreach (var selector in matching)
        {
            Publish(entityId, selector.Attribute, state);
        }
    }

    private static bool IsAutoNumeric(string entityId, HaState state)
    {
        var domain = entityId[..entityId.IndexOf('.', StringComparison.Ordinal)];
        if (!NumericDomains.Contains(domain))
        {
            return false;
        }

        return HaMapping.ToNumber(state.State, allowWords: false) is not null
               || state.Text("unit_of_measurement") is not null
               || state.Text("state_class") is not null;
    }

    private void Publish(string entityId, string? attribute, HaState state)
    {
        var domain = entityId[..entityId.IndexOf('.', StringComparison.Ordinal)];
        var friendly = state.Text("friendly_name") ?? entityId;
        var local = LocalFor(entityId, attribute);
        var rawText = attribute is null ? state.State : JsonValues.ToText(JsonRead.Get(state.Attributes, attribute));
        var unitText = attribute is null ? state.Text("unit_of_measurement") : AttributeUnit(attribute);
        var deviceClass = attribute is null ? state.Text("device_class") : AttributeClass(attribute);
        var name = attribute is null ? friendly : $"{friendly} {attribute.Replace('_', ' ')}";

        var switchLike = attribute is null && SwitchDomains.Contains(domain) && unitText is null;
        var number = HaMapping.ToNumber(rawText, allowWords: switchLike);
        var hasUnitOrClass = unitText is not null || (attribute is null && state.Text("state_class") is not null);
        if (!_isText.TryGetValue(local, out var text))
        {
            // Decided once, when the entity is first seen, so that the sensor keeps its type when it later goes "unavailable".
            text = !switchLike && !hasUnitOrClass && number is null
                   && (!HaMapping.IsNoValue(rawText) || (attribute is null && !NumericDomains.Contains(domain)));
            _isText[local] = text;
        }

        if (text)
        {
            var info = Info(local, name, SensorCategory.Home, Units.None, "Home Assistant", Host, SensorKinds.Text, icon: DomainIcon(domain), text: true);
            Txt(info with { StaleAfterSeconds = StaleSeconds }, HaMapping.IsNoValue(rawText) ? null : rawText);
            return;
        }

        if (switchLike)
        {
            Num(Info(local, name, SensorCategory.Home, Units.None, "Home Assistant", Host, SensorKinds.State, 0, 1, DomainIcon(domain), staleAfterSeconds: StaleSeconds), number);
            return;
        }

        var map = HaMapping.Map(unitText, deviceClass, entityId, friendly);
        var kind = map.Kind;
        var info2 = Info(local, name, map.Category, map.Unit, "Home Assistant", Host, kind, map.Min, map.Max, map.Icon, staleAfterSeconds: StaleSeconds);
        Num(info2, number is { } n ? map.Convert(n) : null);
    }

    private static string? AttributeUnit(string attribute) => attribute switch
    {
        "temperature" or "current_temperature" or "apparent_temperature" or "dew_point" or "target_temperature" => "°C",
        "humidity" or "current_humidity" or "battery_level" or "battery" => "%",
        "pressure" => "hPa",
        "wind_speed" or "wind_gust_speed" => "km/h",
        _ => null,
    };

    private static string? AttributeClass(string attribute) => attribute switch
    {
        "battery_level" or "battery" => "battery",
        "humidity" or "current_humidity" => "humidity",
        _ => null,
    };

    private static string DomainIcon(string domain) => domain switch
    {
        "light" => "lightbulb",
        "switch" or "input_boolean" => "power-socket-eu",
        "lock" => "lock",
        "cover" => "garage",
        "person" or "device_tracker" => "account",
        "climate" => "air-conditioner",
        "weather" => "weather-partly-cloudy",
        _ => "home-assistant",
    };
}
