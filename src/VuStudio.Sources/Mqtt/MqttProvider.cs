using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Mqtt;

/// <summary>Creates <c>mqtt</c> providers: values from an MQTT broker (Tasmota, Shelly, Zigbee2MQTT, Home Assistant discovery…).</summary>
public sealed class MqttProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "mqtt",
        Name = L("MQTT", "MQTT"),
        Description = L(
            "Subscribes to topics of an MQTT broker and turns payloads (plain numbers, on/off words or values inside JSON) into sensors. Reconnects by itself. Optionally imports the sensors that Home Assistant MQTT discovery announces.",
            "Si iscrive ai topic di un broker MQTT e trasforma i payload (numeri semplici, parole on/off o valori dentro un JSON) in sensori. Si riconnette da solo. Può importare i sensori annunciati dalla discovery MQTT di Home Assistant."),
        Icon = "transmission-tower",
        Group = ProviderGroup.HomeAutomation,
        Settings =
        [
            Setting.Text("host", L("Broker host", "Host del broker"), help: L("Name or IP address of the MQTT broker.", "Nome o indirizzo IP del broker MQTT."), placeholder: "192.168.1.10", required: true),
            Setting.Number("port", L("Port", "Porta"), 1883, 1, 65535, L("1883 for plain MQTT, 8883 for TLS.", "1883 per MQTT semplice, 8883 per TLS.")),
            Setting.Bool("tls", L("Use TLS", "Usa TLS"), false),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), false),
            Setting.Text("username", L("User name", "Nome utente")),
            Setting.Secret("password", L("Password", "Password")),
            Setting.Text("clientId", L("Client id", "Id client"), help: L("Leave empty to generate one. It must be unique on the broker.", "Lascia vuoto per generarne uno. Deve essere unico sul broker.")),
            Setting.Select("protocol", L("Protocol version", "Versione del protocollo"), "3.1.1",
            [
                Setting.Option("3.1.1", "MQTT 3.1.1 (works everywhere)", "MQTT 3.1.1 (funziona ovunque)"),
                Setting.Option("5.0", "MQTT 5.0", "MQTT 5.0"),
            ]),
            Setting.Table(
                "subscriptions",
                L("Subscriptions", "Sottoscrizioni"),
                [
                    Setting.Text("topic", L("Topic", "Topic"), help: L("Wildcards: + for one level, # for the rest. A wildcard creates one sensor per topic.", "Caratteri jolly: + per un livello, # per il resto. Un carattere jolly crea un sensore per ogni topic."), placeholder: "tele/plug/SENSOR", required: true),
                    Setting.Text("id", L("ID", "ID"), help: L("Sensor id; derived from the name when empty.", "ID del sensore; se vuoto deriva dal nome.")),
                    Setting.Text("name", L("Name", "Nome")),
                    Setting.Text("path", L("JSON path", "Percorso JSON"), help: L("For JSON payloads, e.g. $.ENERGY.Power or $.temperature.tC. Empty = the payload is the value.", "Per payload JSON, ad esempio $.ENERGY.Power o $.temperature.tC. Vuoto = il payload è il valore."), placeholder: "$.ENERGY.Power"),
                    Setting.Text("unit", L("Unit", "Unità"), help: L("Canonical unit of the value after the multiplier: %, °C, W, Wh, V, A…", "Unità canonica del valore dopo il moltiplicatore: %, °C, W, Wh, V, A…")),
                    Setting.Select("category", L("Category", "Categoria"), "custom", SensorOptions.Categories),
                    Setting.Number("min", L("Min", "Min"), null),
                    Setting.Number("max", L("Max", "Max"), null),
                    Setting.Bool("text", L("Text value", "Valore di testo"), false, L("Publish the payload as text.", "Pubblica il payload come testo.")),
                    Setting.Number("multiplier", L("Multiplier", "Moltiplicatore"), 1),
                    Setting.Number("staleAfter", L("Stale after (s)", "Obsoleto dopo (s)"), 3600, 1, 604_800, L("Tasmota reports every 5 minutes by default: keep this above the report interval.", "Tasmota comunica ogni 5 minuti per impostazione predefinita: tienilo sopra l'intervallo di comunicazione.")),
                ],
                help: L("Each row becomes a sensor (one per topic when the filter has wildcards).", "Ogni riga diventa un sensore (uno per topic se il filtro ha caratteri jolly).")),
            Setting.Bool("haDiscovery", L("Import Home Assistant discovery", "Importa la discovery di Home Assistant"), false, L("Adds the sensors that devices announce under the discovery prefix.", "Aggiunge i sensori che i dispositivi annunciano sotto il prefisso di discovery.")),
            Setting.Text("haPrefix", L("Discovery prefix", "Prefisso di discovery"), "homeassistant"),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the broker address.", "Etichetta host dei sensori. Predefinito: l'indirizzo del broker.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var rows = new List<MqttRow>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in s.GetTable("subscriptions"))
        {
            var topic = row.GetString("topic");
            if (topic is null || !MqttTopics.IsValidFilter(topic))
            {
                continue;
            }

            var name = row.GetString("name") ?? topic;
            var unit = row.GetString("unit") ?? Units.None;
            var isText = row.GetBool("text");
            var multiplier = row.GetDouble("multiplier", 1);
            rows.Add(new MqttRow(
                topic,
                LocalIds.Unique(LocalIds.SlugPath(row.GetString("id") ?? name), used),
                name,
                row.GetString("path"),
                unit,
                SensorOptions.ParseCategory(row.GetString("category")),
                Finite(row.GetDouble("min", double.NaN)),
                Finite(row.GetDouble("max", double.NaN)),
                isText,
                double.IsFinite(multiplier) ? multiplier : 1,
                0,
                Math.Max(1, row.GetDouble("staleAfter", 3600))));
        }

        var clientId = s.GetString("clientId") ?? $"vustudio-{config.Id}-{LocalIds.Slug(Environment.MachineName, "pc")}";
        return new MqttProvider(
            config.Id,
            s.GetString("host"),
            Math.Clamp(s.GetInt("port", 1883), 1, 65535),
            s.GetBool("tls"),
            s.GetBool("allowInvalidCertificate"),
            s.GetString("username"),
            s.GetString("password"),
            clientId,
            s.GetString("protocol") == "5.0",
            rows,
            s.GetBool("haDiscovery"),
            (s.GetString("haPrefix") ?? "homeassistant").Trim('/'),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            ProviderServices.Time(services),
            ProviderServices.Logger<MqttProvider>(services));
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}

/// <summary>Holds a connection to a broker for as long as it lasts; reconnects with back-off. See docs/integrations/network-sources.md section 9.</summary>
internal sealed class MqttProvider : NetworkPollingProvider
{
    private readonly string? _host;
    private readonly int _port;
    private readonly bool _tls;
    private readonly bool _allowInvalidCertificate;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string _clientId;
    private readonly bool _mqtt5;
    private readonly MqttSensorMap _map;
    private readonly bool _haDiscovery;
    private readonly string _haPrefix;
    private readonly string? _nameOverride;
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);

    public MqttProvider(string id, string? host, int port, bool tls, bool allowInvalidCertificate, string? username, string? password, string clientId, bool mqtt5, IReadOnlyList<MqttRow> rows, bool haDiscovery, string haPrefix, string? nameOverride, TimeProvider time, ILogger logger)
        : base(id, TimeSpan.FromSeconds(5), time, logger, staleAfterSeconds: 3600)
    {
        _host = host;
        _port = port;
        _tls = tls;
        _allowInvalidCertificate = allowInvalidCertificate;
        _username = username;
        _password = password;
        _clientId = clientId;
        _mqtt5 = mqtt5;
        _map = new MqttSensorMap(rows);
        _haDiscovery = haDiscovery;
        _haPrefix = haPrefix;
        _nameOverride = nameOverride;
    }

    private string Host => _nameOverride ?? _host ?? "mqtt";

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_host))
        {
            throw new SourceUnavailableException("Enter the address of the MQTT broker in the settings.");
        }

        var filters = _map.Filters.ToList();
        if (_haDiscovery)
        {
            filters.AddRange(MqttDiscovery.Filters(_haPrefix));
        }

        if (filters.Count == 0)
        {
            throw new SourceUnavailableException("Add at least one subscription (or enable the Home Assistant discovery import).");
        }

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var inbox = Channel.CreateBounded<(string Topic, string? Payload)>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Handlers first: retained messages arrive right after SUBSCRIBE.
        client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Payload.Length <= MqttPayload.MaxBytes)
            {
                inbox.Writer.TryWrite((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            }

            return Task.CompletedTask;
        };
        client.DisconnectedAsync += e =>
        {
            lost.TrySetResult(e.Exception ?? new IOException("The broker closed the connection."));
            return Task.CompletedTask;
        };

        var options = BuildOptions();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await client.ConnectAsync(options, timeout.Token).ConfigureAwait(false);
            if (result.ResultCode is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.ClientIdentifierNotValid)
            {
                throw new SourceUnavailableException($"The broker refused the connection: {result.ResultCode}. Check the user name, password and client id.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{_host}:{_port}: the broker did not answer within 15 s.");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SourceUnavailableException))
        {
            throw new IOException($"Cannot connect to the MQTT broker {_host}:{_port}: {ex.Message}", ex);
        }

        try
        {
            _subscribed.Clear();
            await SubscribeAsync(client, factory, filters, cancellationToken).ConfigureAwait(false);
            ResetFailures();
            Status.Report(ProviderHealth.Ok);

            while (true)
            {
                var wait = inbox.Reader.WaitToReadAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(wait, lost.Task).ConfigureAwait(false) == lost.Task)
                {
                    throw new IOException("MQTT connection lost: " + lost.Task.Result.Message, lost.Task.Result);
                }

                while (inbox.Reader.TryRead(out var message))
                {
                    await HandleAsync(client, factory, message.Topic, message.Payload, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            InvalidateAll(); // the broker is offline: no value is better than a stale one
            if (ex is IOException or SourceUnavailableException)
            {
                throw;
            }

            throw new IOException($"MQTT error: {ex.Message}", ex);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogDebug(ex, "MQTT disconnect failed");
                }
            }
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_host, _port)
            .WithClientId(_clientId)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithProtocolVersion(_mqtt5 ? MQTTnet.Formatter.MqttProtocolVersion.V500 : MQTTnet.Formatter.MqttProtocolVersion.V311);
        if (!string.IsNullOrEmpty(_username))
        {
            builder.WithCredentials(_username, _password);
        }

        if (_tls)
        {
            builder.WithTlsOptions(o =>
            {
                o.UseTls();
                if (_allowInvalidCertificate)
                {
                    o.WithCertificateValidationHandler(_ => true);
                }
            });
        }

        return builder.Build();
    }

    private async Task SubscribeAsync(IMqttClient client, MqttClientFactory factory, IEnumerable<string> filters, CancellationToken cancellationToken)
    {
        var fresh = filters.Where(f => _subscribed.Add(f)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var builder = factory.CreateSubscribeOptionsBuilder();
        foreach (var filter in fresh)
        {
            builder.WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        await client.SubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task HandleAsync(IMqttClient? client, MqttClientFactory? factory, string topic, string? payload, CancellationToken cancellationToken)
    {
        if (_haDiscovery && MqttDiscovery.IsConfigTopic(_haPrefix, topic))
        {
            var row = MqttDiscovery.Parse(_haPrefix, topic, payload, 3600);
            var previous = _map.DiscoveredIdBase(topic);
            _map.SetDiscovered(topic, row);
            if (row is null && previous is not null)
            {
                foreach (var id in Registry.Ids.Where(i => i == previous || i.StartsWith(previous + "/", StringComparison.Ordinal)).ToList())
                {
                    SetNone(id);
                }
            }
            else if (row is not null && client is not null && factory is not null)
            {
                await SubscribeAsync(client, factory, [row.Filter], cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        Apply(topic, payload);
    }

    /// <summary>Applies one message to the sensors.</summary>
    internal void Apply(string topic, string? payload)
    {
        foreach (var update in _map.Process(topic, payload))
        {
            var info = update.Info with { Host = Host };
            if (info.ValueKind == SensorValueKind.Text)
            {
                Txt(info, update.Text);
            }
            else
            {
                Num(info, update.Number);
            }
        }
    }
}
