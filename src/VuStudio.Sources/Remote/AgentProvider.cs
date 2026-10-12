using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Remote;

/// <summary>Creates <c>agent</c> providers: sensors of another machine running the VU Studio Agent.</summary>
public sealed class AgentProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "agent",
        Name = L("VU Studio Agent", "VU Studio Agent"),
        Description = L(
            "Reads the sensors of another machine (Unraid, Steam Deck, Raspberry Pi, a second PC) from its VU Studio Agent.",
            "Legge i sensori di un'altra macchina (Unraid, Steam Deck, Raspberry Pi, un secondo PC) dal suo VU Studio Agent."),
        Icon = "server-network",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Url("url", L("Agent address", "Indirizzo dell'agent"), help: L("Address and port of the agent (default port 5342).", "Indirizzo e porta dell'agent (porta predefinita 5342)."), placeholder: "http://unraid.local:5342", required: true),
            Setting.Secret("token", L("Access token", "Token di accesso"), L("Needed when the agent has a token configured.", "Necessario se l'agent ha un token configurato.")),
            Setting.Interval(2, min: 0.5, max: 3600),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Shown as the host of the sensors. Leave empty to use the name the agent reports.", "Mostrato come host dei sensori. Lascia vuoto per usare il nome comunicato dall'agent.")),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), false, L("Only for https agents with a self-signed certificate.", "Solo per agent https con certificato autofirmato.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var (client, owner) = NetHttp.CreateClient(services, s.GetBool("allowInvalidCertificate"));
        return new AgentProvider(
            config.Id,
            NetHttp.NormalizeBase(s.GetString("url")),
            s.GetString("token"),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 2),
            client,
            owner,
            ProviderServices.Time(services),
            ProviderServices.Logger<AgentProvider>(services));
    }
}

/// <summary>One sensor of an agent's catalog: the agent's own id and the metadata to publish.</summary>
internal sealed record AgentSensor(string RemoteId, SensorInfo Info);

/// <summary>The parsed answer of <c>GET /api/v1/sensors</c>.</summary>
internal sealed record AgentSnapshot(string? Name, string? Version, string CatalogVersion, IReadOnlyList<AgentSensor> Sensors, JsonObject Values)
{
    /// <summary>Parses the answer tolerantly: unknown categories become <c>Custom</c>, missing names fall back to the id.</summary>
    public static AgentSnapshot Parse(JsonNode? root, string? host)
    {
        if (root is not JsonObject obj)
        {
            throw new InvalidDataException("The agent did not answer with a JSON object.");
        }

        var sensors = new List<AgentSensor>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in (obj["sensors"] as JsonArray) ?? [])
        {
            if (item is not JsonObject s || JsonValues.ToText(s["id"]) is not { Length: > 0 } remoteId)
            {
                continue;
            }

            var local = LocalIds.IsValid(remoteId) ? remoteId : LocalIds.SlugPath(remoteId);
            if (!used.Add(local))
            {
                continue;
            }

            var valueKind = Enum.TryParse<SensorValueKind>(JsonValues.ToText(s["valueKind"]), true, out var vk) ? vk : SensorValueKind.Number;
            var unit = JsonValues.ToText(s["unit"]) ?? Units.None;
            var kind = JsonValues.ToText(s["kind"]);
            var category = SensorOptions.ParseCategory(JsonValues.ToText(s["category"]));
            sensors.Add(new AgentSensor(remoteId, new SensorInfo
            {
                Id = local,
                Name = JsonValues.ToText(s["name"]) ?? remoteId,
                Device = JsonValues.ToText(s["device"]),
                Category = category,
                Kind = kind,
                Unit = unit,
                ValueKind = valueKind,
                Min = Num(s["min"]),
                Max = Num(s["max"]),
                Icon = JsonValues.ToText(s["icon"]) ?? SensorHints.Icon(category, kind),
                Host = host,
                StaleAfterSeconds = Num(s["staleAfterSeconds"]),
            }));
        }

        var catalogVersion = JsonValues.ToText(obj["catalogVersion"])
            ?? "ids:" + string.Join('|', sensors.Select(x => x.RemoteId)).GetHashCode().ToString(CultureInfo.InvariantCulture);
        return new AgentSnapshot(JsonValues.ToText(obj["name"]), JsonValues.ToText(obj["version"]), catalogVersion, sensors, obj["values"] as JsonObject ?? []);
    }

    private static double? Num(JsonNode? node) => JsonValues.TryGetNumber(node, out var v) ? v : null;
}

/// <summary>Polls <c>/api/v1/sensors</c> of a VU Studio Agent and re-publishes its sensors.</summary>
internal sealed class AgentProvider : NetworkPollingProvider
{
    private readonly Uri? _baseUri;
    private readonly string? _token;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private string? _catalogVersion;
    private List<AgentSensor> _catalog = [];

    public AgentProvider(string id, Uri? baseUri, string? token, string? nameOverride, TimeSpan interval, HttpClient client, IDisposable? owner, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger, owner)
    {
        _baseUri = baseUri;
        _token = token;
        _nameOverride = nameOverride;
        _client = client;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(interval.TotalSeconds * 2, 5, 15));
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null)
        {
            throw new SourceUnavailableException("Enter the address of the agent in the settings.");
        }

        var root = await NetHttp.GetJsonAsync(
            _client,
            NetHttp.Combine(_baseUri, "api/v1/sensors"),
            _timeout,
            cancellationToken,
            r => r.Headers.Authorization = NetHttp.Bearer(_token),
            "access token").ConfigureAwait(false);

        var snapshot = AgentSnapshot.Parse(root, _nameOverride);
        var host = _nameOverride ?? snapshot.Name ?? _baseUri.Host;
        if (snapshot.CatalogVersion != _catalogVersion)
        {
            Redefine(snapshot, host);
        }

        foreach (var sensor in _catalog)
        {
            var value = snapshot.Values[sensor.RemoteId];
            if (sensor.Info.ValueKind == SensorValueKind.Number)
            {
                Num(sensor.Info, JsonValues.TryGetNumber(value, out var number) ? number : null);
            }
            else
            {
                Txt(sensor.Info, value is null ? null : JsonValues.ToText(value));
            }
        }
    }

    private void Redefine(AgentSnapshot snapshot, string host)
    {
        var next = snapshot.Sensors.Select(s => s with { Info = s.Info with { Host = host } }).ToList();
        var keep = next.Select(s => s.Info.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _catalog.Where(o => !keep.Contains(o.Info.Id)))
        {
            Forget(old.Info.Id);
        }

        foreach (var sensor in next)
        {
            Registry.Define(sensor.Info);
        }

        _catalog = next;
        _catalogVersion = snapshot.CatalogVersion;
    }
}
