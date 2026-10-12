using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Unraid;

/// <summary>Creates <c>unraid</c> providers: array, parity, disks, Docker and VMs through the official Unraid GraphQL API (7.2+).</summary>
public sealed class UnraidProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "unraid",
        Name = L("Unraid", "Unraid"),
        Description = L(
            "Array state and capacity, parity check progress, disk temperatures and health, Docker containers and VMs, through the Unraid API (Unraid 7.2 or newer). Create a read-only API key (role VIEWER) in Settings > Management Access > API Keys.",
            "Stato e capacità dell'array, avanzamento del controllo di parità, temperature e salute dei dischi, container Docker e VM, tramite l'API di Unraid (Unraid 7.2 o successivo). Crea una chiave API di sola lettura (ruolo VIEWER) in Impostazioni > Management Access > API Keys."),
        Icon = "nas",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Url("url", L("Server address", "Indirizzo del server"), help: L("The address you use for the Unraid web interface.", "L'indirizzo che usi per l'interfaccia web di Unraid."), placeholder: "http://tower.local", required: true),
            Setting.Secret("apiKey", L("API key", "Chiave API"), L("Role VIEWER is enough.", "Basta il ruolo VIEWER.")),
            Setting.Interval(10, min: 2, max: 3600, help: L("Seconds between reads. The API has rate limits: do not go below 5.", "Secondi tra una lettura e l'altra. L'API ha dei limiti di frequenza: non scendere sotto 5.")),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the server address.", "Etichetta host dei sensori. Predefinito: l'indirizzo del server.")),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), true, L("Unraid uses a self-signed certificate when SSL is set to Yes.", "Unraid usa un certificato autofirmato quando SSL è impostato su Sì.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        // A redirect from http to https would turn the POST into a GET: do not follow it, explain it.
        var (client, owner) = NetHttp.CreateClient(services, s.GetBool("allowInvalidCertificate", true), followRedirects: false);
        return new UnraidProvider(
            config.Id,
            NetHttp.NormalizeBase(s.GetString("url")),
            s.GetString("apiKey"),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 10),
            client,
            owner,
            ProviderServices.Time(services),
            ProviderServices.Logger<UnraidProvider>(services));
    }
}

/// <summary>Reads the Unraid GraphQL API. See docs/integrations/network-sources.md section 4.2.</summary>
internal sealed class UnraidProvider : NetworkPollingProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryUnsupportedAfter = TimeSpan.FromMinutes(10);

    /// <summary>Each section lists alternative queries from the richest to the most basic; fields differ between API versions.</summary>
    private static readonly (string Section, string[] Queries)[] Sections =
    [
        ("array",
        [
            "array { state capacity { kilobytes { free used total } } parityCheckStatus { status progress speed } parities { name status temp isSpinning numErrors } disks { name device status temp isSpinning numErrors fsSize fsFree fsUsed } caches { name status temp isSpinning numErrors fsSize fsFree fsUsed } }",
            "array { state capacity { kilobytes { free used total } } parities { name status temp isSpinning numErrors } disks { name device status temp isSpinning numErrors fsSize fsFree fsUsed } caches { name status temp fsSize fsFree fsUsed } }",
            "array { state capacity { kilobytes { free used total } } }",
        ]),
        ("docker", ["docker { containers { names state status } }", "docker { containers { names state } }"]),
        ("vms", ["vms { domains { name state } }"]),
        ("metrics", ["metrics { cpu { percentTotal } memory { total available used percentTotal swapUsed percentSwapTotal } }", "metrics { cpu { percentTotal } memory { total used percentTotal } }", "metrics { cpu { percentTotal } }"]),
        ("info", ["info { os { hostname uptime } }", "info { os { uptime } }"]),
    ];

    private readonly Uri? _endpoint;
    private readonly string? _apiKey;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly Dictionary<string, int> _working = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _unsupportedSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastTemp = new(StringComparer.Ordinal);
    private string? _hostname;

    public UnraidProvider(string id, Uri? baseUri, string? apiKey, string? nameOverride, TimeSpan interval, HttpClient client, IDisposable? owner, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger, owner)
    {
        _endpoint = baseUri is null ? null : baseUri.AbsolutePath.TrimEnd('/').EndsWith("/graphql", StringComparison.OrdinalIgnoreCase) ? baseUri : NetHttp.Combine(baseUri, "graphql");
        _apiKey = apiKey;
        _nameOverride = nameOverride;
        _client = client;
    }

    private string Host => _nameOverride ?? _hostname ?? _endpoint?.Host ?? "unraid";

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is null)
        {
            throw new SourceUnavailableException("Enter the address of the Unraid server in the settings.");
        }

        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new SourceUnavailableException("Enter an Unraid API key in the settings (Settings > Management Access > API Keys, role VIEWER).");
        }

        var data = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (section, queries) in Sections)
        {
            data[section] = await RunSectionAsync(section, queries, cancellationToken).ConfigureAwait(false);
        }

        _hostname = JsonRead.Str(JsonRead.Path(data["info"], "info", "os"), "hostname") ?? _hostname;
        if (data["array"] is null)
        {
            throw new IOException("The Unraid API did not return the array. Is the API enabled (Settings > Management Access > API) and is the server on Unraid 7.2 or newer?");
        }

        PublishArray(JsonRead.Get(data["array"], "array"));
        PublishDocker(JsonRead.Get(data["docker"], "docker"));
        PublishVms(JsonRead.Get(data["vms"], "vms"));
        PublishMetrics(JsonRead.Get(data["metrics"], "metrics"));
        PublishInfo(JsonRead.Get(data["info"], "info"));
    }

    // ----- transport -----

    private async Task<JsonNode?> RunSectionAsync(string section, string[] queries, CancellationToken cancellationToken)
    {
        if (_unsupportedSince.TryGetValue(section, out var since) && Clock - since < RetryUnsupportedAfter)
        {
            return null;
        }

        for (var i = _working.GetValueOrDefault(section); i < queries.Length; i++)
        {
            var result = await QueryAsync("query { " + queries[i] + " }", cancellationToken).ConfigureAwait(false);
            if (result.Data is { } data && (result.Errors.Count == 0 || HasAnyField(data)))
            {
                _working[section] = i;
                _unsupportedSince.Remove(section);
                if (result.Errors.Count > 0)
                {
                    Warning ??= $"Unraid API ({section}): {result.Errors[0]}";
                }

                return data;
            }

            if (!result.IsValidationError)
            {
                Warning ??= $"Unraid API ({section}): {(result.Errors.Count > 0 ? result.Errors[0] : "no data")}";
                break; // not a schema problem: a simpler query would not help
            }
        }

        _unsupportedSince[section] = Clock;
        if (section != "array")
        {
            Logger.LogInformation("Unraid API section '{Section}' is not available on this server", section);
        }

        return null;
    }

    private static bool HasAnyField(JsonNode data) => data is JsonObject o && o.Any(kv => kv.Value is not null);

    internal sealed record GraphQlResult(JsonNode? Data, IReadOnlyList<string> Errors, bool IsValidationError);

    private async Task<GraphQlResult> QueryAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(new JsonObject { ["query"] = query }.ToJsonString(), new UTF8Encoding(false), "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);

        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SourceUnavailableException($"The Unraid API rejected the API key (HTTP {(int)response.Status}). Check the key and its role.");
        }

        if ((int)response.Status is >= 300 and < 400)
        {
            throw new SourceUnavailableException($"Unraid redirected the request to {response.Headers.Location}. Use that address (https://) in the settings.");
        }

        JsonNode? body = null;
        try
        {
            body = response.Json();
        }
        catch (InvalidDataException) when (!response.IsSuccess)
        {
        }

        if (body is null && !response.IsSuccess)
        {
            NetHttp.EnsureSuccess(response);
        }

        return ParseResult(body, response.Status == HttpStatusCode.BadRequest);
    }

    /// <summary>Reads <c>{"data":…, "errors":[…]}</c>.</summary>
    internal static GraphQlResult ParseResult(JsonNode? body, bool httpBadRequest = false)
    {
        var errors = JsonRead.Items(body, "errors").Select(e => JsonRead.Str(e, "message") ?? "error").ToList();
        var validation = httpBadRequest
                         || JsonRead.Items(body, "errors").Any(e => JsonRead.Str(JsonRead.Get(e, "extensions"), "code") == "GRAPHQL_VALIDATION_FAILED")
                         || errors.Any(e => e.Contains("Cannot query field", StringComparison.OrdinalIgnoreCase));
        var data = JsonRead.Get(body, "data");
        return new GraphQlResult(data is JsonObject ? data : null, errors, validation);
    }

    // ----- mapping -----

    private SensorInfo S(string id, string name, SensorCategory category, string unit, string? device = null, string? kind = null, double? min = null, double? max = null, string? icon = null) =>
        Info(id, name, category, unit, device, Host, kind, min, max, icon);

    private static double? Kib(JsonNode? node, string key) => JsonRead.Num(node, key) is { } kib ? kib * 1024 : null;

    internal void PublishArray(JsonNode? array)
    {
        var state = JsonRead.Str(array, "state");
        Txt(S("array/state", "Array state", SensorCategory.Storage, Units.None, "Array", icon: "nas") with { ValueKind = SensorValueKind.Text, Kind = SensorKinds.State }, state);
        Num(S("array/started", "Array started", SensorCategory.Storage, Units.None, "Array", SensorKinds.State, 0, 1, "nas"), state is null ? null : state.Equals("STARTED", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

        var capacity = JsonRead.Path(array, "capacity", "kilobytes");
        var used = Kib(capacity, "used");
        var total = Kib(capacity, "total");
        Num(S("array/used", "Array used", SensorCategory.Storage, Units.Bytes, "Array", min: 0, icon: "harddisk"), used);
        Num(S("array/free", "Array free", SensorCategory.Storage, Units.Bytes, "Array", min: 0, icon: "harddisk"), Kib(capacity, "free"));
        Num(S("array/total", "Array capacity", SensorCategory.Storage, Units.Bytes, "Array", min: 0, icon: "harddisk"), total);
        Num(S("array/percent", "Array used %", SensorCategory.Storage, Units.Percent, "Array", SensorKinds.Percent, 0, 100, "harddisk"), used is { } u && total is > 0 ? 100 * u / total : null);

        PublishParity(JsonRead.Get(array, "parityCheckStatus"));

        using var _ = Sweep("disk/");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "parities", "disks", "caches" })
        {
            foreach (var disk in JsonRead.Items(array, key))
            {
                PublishDisk(disk, names);
            }
        }
    }

    private void PublishParity(JsonNode? parity)
    {
        var stale = StaleAfterSeconds;
        if (parity is null)
        {
            return;
        }

        var status = JsonRead.Str(parity, "status");
        var running = status?.ToUpperInvariant() is "RUNNING";
        var active = running || status?.ToUpperInvariant() is "PAUSED";
        Txt(S("parity/status", "Parity check status", SensorCategory.Storage, Units.None, "Parity", SensorKinds.State, icon: "shield-check") with { ValueKind = SensorValueKind.Text }, status);
        Num(S("parity/running", "Parity check running", SensorCategory.Storage, Units.None, "Parity", SensorKinds.State, 0, 1, "shield-check"), status is null ? null : running ? 1 : 0);
        Num(S("parity/progress", "Parity check progress", SensorCategory.Storage, Units.Percent, "Parity", SensorKinds.Percent, 0, 100, "shield-check"), status is null ? null : active ? JsonRead.Num(parity, "progress") ?? 0 : 0);
        var speed = JsonRead.Num(parity, "speed"); // MB/s
        Num(S("parity/speed", "Parity check speed", SensorCategory.Storage, Units.BytesPerSecond, "Parity", SensorKinds.Throughput, 0, icon: "shield-check"), status is null ? null : running && speed is { } mb ? mb * 1_000_000 : 0);
        _ = stale;
    }

    private void PublishDisk(JsonObject disk, HashSet<string> names)
    {
        var name = JsonRead.Str(disk, "name");
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var slug = LocalIds.Unique(LocalIds.Slug(name), names);
        var device = JsonRead.Str(disk, "device") is { Length: > 0 } dev ? $"{name} ({dev})" : name;
        var title = char.ToUpperInvariant(name[0]) + name[1..];

        var temp = JsonRead.Num(disk, "temp");
        if (temp is { } t)
        {
            _lastTemp[slug] = t;
        }

        // Standby disks report no temperature: keep the last known one rather than waking the disk or showing nothing.
        Num(S($"disk/{slug}/temp", $"{title} temperature", SensorCategory.Storage, Units.Celsius, device, SensorKinds.Temperature, 20, 70), temp ?? _lastTemp.GetValueOrDefault(slug, double.NaN));
        var spinning = JsonRead.Bool(disk, "isSpinning");
        Num(S($"disk/{slug}/spinning", $"{title} spinning", SensorCategory.Storage, Units.None, device, SensorKinds.State, 0, 1), spinning is null ? null : spinning.Value ? 1 : 0);

        var status = JsonRead.Str(disk, "status");
        Txt(S($"disk/{slug}/status", $"{title} status", SensorCategory.Storage, Units.None, device, SensorKinds.State) with { ValueKind = SensorValueKind.Text }, status);
        Num(S($"disk/{slug}/ok", $"{title} healthy", SensorCategory.Storage, Units.None, device, SensorKinds.State, 0, 1, "check-circle"), status is null ? null : status.Equals("DISK_OK", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        Num(S($"disk/{slug}/errors", $"{title} errors", SensorCategory.Storage, Units.None, device, SensorKinds.Count, 0, icon: "alert-circle"), JsonRead.Num(disk, "numErrors"));

        var size = Kib(disk, "fsSize");
        if (size is > 0)
        {
            var usedBytes = Kib(disk, "fsUsed") ?? (Kib(disk, "fsFree") is { } free ? size - free : null);
            Num(S($"disk/{slug}/percent", $"{title} used %", SensorCategory.Storage, Units.Percent, device, SensorKinds.Percent, 0, 100), usedBytes is { } u ? 100 * u / size : null);
            Num(S($"disk/{slug}/used", $"{title} used", SensorCategory.Storage, Units.Bytes, device, min: 0), usedBytes);
            Num(S($"disk/{slug}/free", $"{title} free", SensorCategory.Storage, Units.Bytes, device, min: 0), Kib(disk, "fsFree") ?? (usedBytes is { } ub ? size - ub : null));
            Num(S($"disk/{slug}/size", $"{title} size", SensorCategory.Storage, Units.Bytes, device, min: 0), size);
        }
    }

    internal void PublishDocker(JsonNode? docker)
    {
        if (docker is null)
        {
            return;
        }

        var containers = JsonRead.Items(docker, "containers").ToList();
        Num(S("docker/running", "Containers running", SensorCategory.System, Units.None, "Docker", SensorKinds.Count, 0, icon: "docker"), containers.Count(c => IsRunning(c)));
        Num(S("docker/total", "Containers", SensorCategory.System, Units.None, "Docker", SensorKinds.Count, 0, icon: "docker"), containers.Count);

        using var _ = Sweep("docker/c/");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            var name = ContainerName(c);
            if (name is null)
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(name), used);
            Num(S($"docker/c/{slug}", $"{name} running", SensorCategory.System, Units.None, "Docker", SensorKinds.State, 0, 1, "docker"), IsRunning(c) ? 1 : 0);
        }
    }

    private static bool IsRunning(JsonNode c) => JsonRead.Str(c, "state")?.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ContainerName(JsonNode c)
    {
        var first = JsonRead.Get(c, "names") is JsonArray { Count: > 0 } names ? JsonValues.ToText(names[0]) : JsonRead.Str(c, "name");
        return first?.TrimStart('/') is { Length: > 0 } n ? n : null;
    }

    internal void PublishVms(JsonNode? vms)
    {
        if (vms is null)
        {
            return;
        }

        var domains = JsonRead.Items(vms, "domains").ToList();
        Num(S("vms/running", "VMs running", SensorCategory.System, Units.None, "VMs", SensorKinds.Count, 0, icon: "monitor"), domains.Count(d => JsonRead.Str(d, "state")?.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) == true));
        Num(S("vms/total", "VMs", SensorCategory.System, Units.None, "VMs", SensorKinds.Count, 0, icon: "monitor"), domains.Count);
    }

    internal void PublishMetrics(JsonNode? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        if (JsonRead.Num(JsonRead.Get(metrics, "cpu"), "percentTotal") is { } cpu)
        {
            Num(S("cpu/total", "CPU load", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), cpu);
        }

        var mem = JsonRead.Get(metrics, "memory");
        if (mem is null)
        {
            return;
        }

        Num(S("memory/percent", "Memory used", SensorCategory.Memory, Units.Percent, "Memory", SensorKinds.Percent, 0, 100), JsonRead.Num(mem, "percentTotal"));
        Num(S("memory/used", "Memory used (bytes)", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), JsonRead.Num(mem, "used"));
        Num(S("memory/total", "Memory total", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), JsonRead.Num(mem, "total"));
        if (JsonRead.Num(mem, "percentSwapTotal") is { } swap)
        {
            Num(S("swap/percent", "Swap used", SensorCategory.Memory, Units.Percent, "Swap", SensorKinds.Percent, 0, 100), swap);
        }
    }

    internal void PublishInfo(JsonNode? info)
    {
        var uptime = JsonRead.Get(JsonRead.Get(info, "os"), "uptime");
        double? seconds = null;
        if (JsonRead.Num(uptime) is { } n)
        {
            seconds = n;
        }
        else if (JsonValues.ToText(uptime) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var boot))
        {
            seconds = Math.Max(0, (Time.GetUtcNow() - boot).TotalSeconds); // the API reports the boot time
        }

        if (uptime is not null)
        {
            Num(S("uptime", "Uptime", SensorCategory.System, Units.Seconds, "System", SensorKinds.Duration, 0), seconds);
        }
    }
}
