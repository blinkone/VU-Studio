using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Remote;

/// <summary>Creates <c>glances</c> providers: a machine running Glances in web server mode (REST API v4, fallback v3).</summary>
public sealed class GlancesProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "glances",
        Name = L("Glances", "Glances"),
        Description = L(
            "Reads CPU, memory, disks, network, sensors, GPU and containers from a Glances server (glances -w, REST API v4 or v3).",
            "Legge CPU, memoria, dischi, rete, sensori, GPU e container da un server Glances (glances -w, API REST v4 o v3)."),
        Icon = "server",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Url("url", L("Glances address", "Indirizzo di Glances"), help: L("Address and port of the Glances web server (default port 61208).", "Indirizzo e porta del server web di Glances (porta predefinita 61208)."), placeholder: "http://nas.local:61208", required: true),
            Setting.Text("username", L("User name", "Nome utente"), help: L("Only if Glances was started with --password (HTTP Basic).", "Solo se Glances è stato avviato con --password (HTTP Basic).")),
            Setting.Secret("password", L("Password", "Password")),
            Setting.Interval(3, min: 1, max: 3600),
            Setting.Bool("perCore", L("Per-core CPU load", "Carico CPU per core"), false, L("Adds one sensor per CPU core.", "Aggiunge un sensore per ogni core della CPU.")),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Shown as the host of the sensors. Leave empty to use the host name reported by Glances.", "Mostrato come host dei sensori. Lascia vuoto per usare il nome host comunicato da Glances.")),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), false),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var (client, owner) = NetHttp.CreateClient(services, s.GetBool("allowInvalidCertificate"));
        return new GlancesProvider(
            config.Id,
            NetHttp.NormalizeBase(s.GetString("url")),
            NetHttp.Basic(s.GetString("username"), s.GetString("password")),
            s.GetBool("perCore"),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 3),
            client,
            owner,
            ProviderServices.Time(services),
            ProviderServices.Logger<GlancesProvider>(services));
    }
}

/// <summary>Reads a Glances REST server. See docs/integrations/network-sources.md section 1.</summary>
internal sealed partial class GlancesProvider : NetworkPollingProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryUnsupportedAfter = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
        { "overlay", "tmpfs", "devtmpfs", "squashfs", "shm", "proc", "sysfs", "cgroup", "cgroup2" };

    private readonly Uri? _baseUri;
    private readonly System.Net.Http.Headers.AuthenticationHeaderValue? _auth;
    private readonly bool _perCore;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly Dictionary<string, TimeSpan> _lastFetch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _unsupportedSince = new(StringComparer.Ordinal);
    private int _api;
    private string? _hostname;

    public GlancesProvider(string id, Uri? baseUri, System.Net.Http.Headers.AuthenticationHeaderValue? auth, bool perCore, string? nameOverride, TimeSpan interval, HttpClient client, IDisposable? owner, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger, owner)
    {
        _baseUri = baseUri;
        _auth = auth;
        _perCore = perCore;
        _nameOverride = nameOverride;
        _client = client;
    }

    private string Host => _nameOverride ?? _hostname ?? _baseUri?.Host ?? "glances";

    protected override void OnSourceLost()
    {
        _api = 0; // probe again: the server may have been replaced by another version
        _lastFetch.Clear();
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null)
        {
            throw new SourceUnavailableException("Enter the address of the Glances server in the settings.");
        }

        if (_api == 0)
        {
            await DetectAsync(cancellationToken).ConfigureAwait(false);
        }

        // Always: the cheap overview and the fast-moving plugins.
        var quick = await GetAsync("quicklook", required: true, cancellationToken).ConfigureAwait(false);
        var cpu = await GetAsync("cpu", required: false, cancellationToken).ConfigureAwait(false);
        PublishCpu(quick, cpu);
        PublishLoad(await GetAsync("load", false, cancellationToken).ConfigureAwait(false), quick);
        PublishMemory(await GetAsync("mem", false, cancellationToken).ConfigureAwait(false), await GetAsync("memswap", false, cancellationToken).ConfigureAwait(false), quick);
        if (_perCore)
        {
            PublishPerCore(await GetAsync("percpu", false, cancellationToken).ConfigureAwait(false));
        }

        PublishNetwork(await GetAsync("network", false, cancellationToken).ConfigureAwait(false));
        PublishDiskIo(await GetAsync("diskio", false, cancellationToken).ConfigureAwait(false));
        PublishGpu(await GetAsync("gpu", false, cancellationToken).ConfigureAwait(false));

        // Plugins Glances itself refreshes slowly (fs every 60 s, sensors every 10 s).
        if (Due("fs", 15))
        {
            PublishFileSystems(await GetAsync("fs", false, cancellationToken).ConfigureAwait(false));
        }

        if (Due("sensors", 10))
        {
            PublishSensors(await GetAsync("sensors", false, cancellationToken).ConfigureAwait(false));
        }

        if (Due("containers", 10))
        {
            var containers = await GetAsync("containers", false, cancellationToken).ConfigureAwait(false)
                             ?? await GetAsync("docker", false, cancellationToken).ConfigureAwait(false);
            PublishContainers(containers);
        }

        if (Due("uptime", 30))
        {
            PublishUptime(await GetAsync("uptime", false, cancellationToken).ConfigureAwait(false));
        }
    }

    private bool Due(string endpoint, double periodSeconds)
    {
        var now = Clock;
        if (_lastFetch.TryGetValue(endpoint, out var last) && (now - last).TotalSeconds < periodSeconds)
        {
            return false;
        }

        _lastFetch[endpoint] = now;
        return true;
    }

    private double Slow(double periodSeconds) => Math.Max(StaleAfterSeconds, periodSeconds * 3 + Interval.TotalSeconds);

    // ----- transport -----

    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        foreach (var version in new[] { 4, 3 })
        {
            using var request = Request($"api/{version}/status");
            var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Status == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }

            NetHttp.EnsureSuccess(response, "user name or password");
            _api = version;
            var system = await GetAsync("system", false, cancellationToken).ConfigureAwait(false);
            _hostname = JsonRead.Str(system, "hostname") ?? _hostname;
            return;
        }

        throw new SourceUnavailableException($"No Glances API (v4 or v3) found at {_baseUri!.Authority}. Start Glances with 'glances -w' and check the address.");
    }

    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, NetHttp.Combine(_baseUri!, path));
        request.Headers.Authorization = _auth;
        return request;
    }

    /// <summary>Fetches one plugin. Optional plugins that answer 404 (disabled in Glances) are skipped for a while.</summary>
    private async Task<JsonNode?> GetAsync(string plugin, bool required, CancellationToken cancellationToken)
    {
        if (!required && _unsupportedSince.TryGetValue(plugin, out var since) && Clock - since < RetryUnsupportedAfter)
        {
            return null;
        }

        using var request = Request($"api/{_api}/{plugin}");
        var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.Status == System.Net.HttpStatusCode.NotFound && !required)
        {
            _unsupportedSince[plugin] = Clock;
            return null;
        }

        _unsupportedSince.Remove(plugin);
        if (!response.IsSuccess && !required && (int)response.Status >= 500)
        {
            Warning = $"Glances plugin '{plugin}' failed (HTTP {(int)response.Status}).";
            return null;
        }

        return NetHttp.EnsureSuccess(response, "user name or password").Json();
    }

    // ----- sensors -----

    private SensorInfo S(string id, string name, SensorCategory category, string unit, string? device = null, string? kind = null, double? min = null, double? max = null, string? icon = null, double? stale = null) =>
        Info(id, name, category, unit, device, Host, kind, min, max, icon, staleAfterSeconds: stale);

    private void PublishCpu(JsonNode? quick, JsonNode? cpu)
    {
        var total = JsonRead.Num(cpu, "total") ?? JsonRead.Num(quick, "cpu");
        Num(S("cpu/total", "CPU load", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), total);
        if (cpu is not null)
        {
            Num(S("cpu/user", "CPU user", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), JsonRead.Num(cpu, "user"));
            Num(S("cpu/system", "CPU system", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), JsonRead.Num(cpu, "system"));
            Num(S("cpu/iowait", "CPU I/O wait", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), JsonRead.Num(cpu, "iowait"));
        }

        if (JsonRead.Num(quick, "cpu_hz_current") is > 0 and var hz)
        {
            Num(S("cpu/freq", "CPU clock", SensorCategory.Cpu, Units.MHz, "CPU"), hz / 1_000_000);
        }
    }

    private void PublishPerCore(JsonNode? percpu)
    {
        using var _ = Sweep("cpu/core/");
        foreach (var core in JsonRead.Items(percpu))
        {
            if (JsonRead.Num(core, "cpu_number") is { } n)
            {
                Num(S($"cpu/core/{(int)n}", $"CPU core {(int)n}", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), JsonRead.Num(core, "total"));
            }
        }
    }

    private void PublishLoad(JsonNode? load, JsonNode? quick)
    {
        if (load is not null)
        {
            foreach (var (key, id, label) in new[] { ("min1", "load/1", "1 min"), ("min5", "load/5", "5 min"), ("min15", "load/15", "15 min") })
            {
                Num(S(id, $"Load average ({label})", SensorCategory.Cpu, Units.None, "CPU", SensorKinds.Load, 0, null), JsonRead.Num(load, key));
            }
        }

        if (JsonRead.Num(quick, "load") is { } percent)
        {
            Num(S("load/percent", "Load (15 min, % of cores)", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), percent);
        }
    }

    private void PublishMemory(JsonNode? mem, JsonNode? swap, JsonNode? quick)
    {
        Num(S("memory/percent", "Memory used", SensorCategory.Memory, Units.Percent, "Memory", SensorKinds.Percent, 0, 100), JsonRead.Num(mem, "percent") ?? JsonRead.Num(quick, "mem"));
        if (mem is not null)
        {
            Num(S("memory/used", "Memory used (bytes)", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), JsonRead.Num(mem, "used"));
            Num(S("memory/available", "Memory available", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), JsonRead.Num(mem, "available"));
            Num(S("memory/total", "Memory total", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), JsonRead.Num(mem, "total"));
        }

        Num(S("swap/percent", "Swap used", SensorCategory.Memory, Units.Percent, "Swap", SensorKinds.Percent, 0, 100), JsonRead.Num(swap, "percent") ?? JsonRead.Num(quick, "swap"));
        if (swap is not null)
        {
            Num(S("swap/used", "Swap used (bytes)", SensorCategory.Memory, Units.Bytes, "Swap", min: 0), JsonRead.Num(swap, "used"));
            Num(S("swap/total", "Swap total", SensorCategory.Memory, Units.Bytes, "Swap", min: 0), JsonRead.Num(swap, "total"));
        }
    }

    private void PublishFileSystems(JsonNode? fs)
    {
        using var _ = Sweep("fs/");
        var stale = Slow(15);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonRead.Items(fs))
        {
            var mount = JsonRead.Str(item, "mnt_point");
            var type = JsonRead.Str(item, "fs_type");
            if (mount is null || (type is not null && PseudoFileSystems.Contains(type)) || mount.StartsWith("/etc/", StringComparison.Ordinal) || mount.StartsWith("/proc", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(mount, "root"), used);
            Num(S($"fs/{slug}/percent", $"{mount} used", SensorCategory.Storage, Units.Percent, mount, SensorKinds.Percent, 0, 100, stale: stale), JsonRead.Num(item, "percent"));
            Num(S($"fs/{slug}/used", $"{mount} used (bytes)", SensorCategory.Storage, Units.Bytes, mount, min: 0, stale: stale), JsonRead.Num(item, "used"));
            Num(S($"fs/{slug}/free", $"{mount} free", SensorCategory.Storage, Units.Bytes, mount, min: 0, stale: stale), JsonRead.Num(item, "free"));
            Num(S($"fs/{slug}/size", $"{mount} size", SensorCategory.Storage, Units.Bytes, mount, min: 0, stale: stale), JsonRead.Num(item, "size"));
        }
    }

    /// <summary>v4: <c>{field}_rate_per_sec</c>. v3 (and v4 without it): the delta since the previous update divided by <c>time_since_update</c>.</summary>
    internal static double? RatePerSecond(JsonObject item, string v4Field, string v3Field)
    {
        if (JsonRead.Num(item, v4Field + "_rate_per_sec") is { } direct)
        {
            return direct;
        }

        var seconds = JsonRead.Num(item, "time_since_update");
        var delta = JsonRead.Num(item, v3Field) ?? JsonRead.Num(item, v4Field);
        return seconds is > 0 && delta is { } d ? d / seconds : null;
    }

    private void PublishNetwork(JsonNode? network)
    {
        using var _ = Sweep("net/");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonRead.Items(network))
        {
            var name = JsonRead.Str(item, "interface_name");
            if (name is null || name == "lo" || name.StartsWith("veth", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(name), used);
            Num(S($"net/{slug}/rx", $"{name} download", SensorCategory.Network, Units.BytesPerSecond, name, SensorKinds.Throughput, 0, icon: "download-network"), RatePerSecond(item, "bytes_recv", "rx"));
            Num(S($"net/{slug}/tx", $"{name} upload", SensorCategory.Network, Units.BytesPerSecond, name, SensorKinds.Throughput, 0, icon: "upload-network"), RatePerSecond(item, "bytes_sent", "tx"));
        }
    }

    private void PublishDiskIo(JsonNode? diskio)
    {
        using var _ = Sweep("disk/");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonRead.Items(diskio))
        {
            var name = JsonRead.Str(item, "disk_name");
            if (name is null || name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(name), used);
            Num(S($"disk/{slug}/read", $"{name} read", SensorCategory.Storage, Units.BytesPerSecond, name, SensorKinds.Throughput, 0), RatePerSecond(item, "read_bytes", "read_bytes"));
            Num(S($"disk/{slug}/write", $"{name} write", SensorCategory.Storage, Units.BytesPerSecond, name, SensorKinds.Throughput, 0), RatePerSecond(item, "write_bytes", "write_bytes"));
        }
    }

    private void PublishSensors(JsonNode? sensors)
    {
        using var _ = Sweep("sensors/");
        var stale = Slow(10);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonRead.Items(sensors))
        {
            var label = JsonRead.Str(item, "label");
            var value = JsonRead.Num(item, "value");
            if (label is null)
            {
                continue;
            }

            var type = JsonRead.Str(item, "type") ?? "";
            var unit = JsonRead.Str(item, "unit") ?? "";
            var slug = LocalIds.Slug(label);
            if (type.StartsWith("temperature", StringComparison.Ordinal) || unit == "C")
            {
                var category = type.Contains("hdd", StringComparison.Ordinal) ? SensorCategory.Storage : type.Contains("core", StringComparison.Ordinal) ? SensorCategory.Cpu : SensorCategory.Motherboard;
                var id = LocalIds.Unique("sensors/temp/" + slug, used);
                Num(S(id, label, category, Units.Celsius, label, SensorKinds.Temperature, 0, 100, stale: stale), value);
            }
            else if (type == "fan_speed" || unit == "R")
            {
                var id = LocalIds.Unique("sensors/fan/" + slug, used);
                Num(S(id, label, SensorCategory.Cooling, Units.Rpm, label, SensorKinds.Fan, 0, stale: stale), value);
            }
            else if (type == "battery")
            {
                var id = LocalIds.Unique("sensors/battery/" + slug, used);
                Num(S(id, label, SensorCategory.Battery, Units.Percent, label, SensorKinds.Level, 0, 100, stale: stale), value);
            }
        }
    }

    private void PublishGpu(JsonNode? gpu)
    {
        using var _ = Sweep("gpu/");
        foreach (var item in JsonRead.Items(gpu))
        {
            var gpuId = JsonRead.Str(item, "gpu_id") ?? JsonRead.Str(item, "name") ?? "gpu";
            var name = JsonRead.Str(item, "name") ?? gpuId;
            var slug = LocalIds.Slug(gpuId, "gpu");
            Num(S($"gpu/{slug}/load", $"{name} load", SensorCategory.Gpu, Units.Percent, name, SensorKinds.Load, 0, 100), JsonRead.Num(item, "proc"));
            Num(S($"gpu/{slug}/memory", $"{name} memory", SensorCategory.Gpu, Units.Percent, name, SensorKinds.Percent, 0, 100), JsonRead.Num(item, "mem"));
            Num(S($"gpu/{slug}/temp", $"{name} temperature", SensorCategory.Gpu, Units.Celsius, name, SensorKinds.Temperature, 0, 100), JsonRead.Num(item, "temperature"));
            if (JsonRead.Num(item, "fan_speed") is { } fan)
            {
                Num(S($"gpu/{slug}/fan", $"{name} fan", SensorCategory.Gpu, Units.Rpm, name, SensorKinds.Fan, 0), fan);
            }
        }
    }

    private void PublishContainers(JsonNode? containers)
    {
        if (containers is null)
        {
            return;
        }

        var stale = Slow(10);
        var items = (containers as JsonArray is not null ? JsonRead.Items(containers) : JsonRead.Items(containers, "containers")).ToList();
        var running = items.Count(c => (JsonRead.Str(c, "status") ?? JsonRead.Str(c, "Status") ?? "").StartsWith("running", StringComparison.OrdinalIgnoreCase)
                                       || (JsonRead.Str(c, "status") ?? JsonRead.Str(c, "Status") ?? "").StartsWith("up", StringComparison.OrdinalIgnoreCase));
        Num(S("containers/running", "Containers running", SensorCategory.System, Units.None, "Containers", SensorKinds.Count, 0, icon: "docker", stale: stale), running);
        Num(S("containers/total", "Containers", SensorCategory.System, Units.None, "Containers", SensorKinds.Count, 0, icon: "docker", stale: stale), items.Count);
    }

    [GeneratedRegex(@"^\s*(?:(\d+)\s+days?,\s*)?(\d+):(\d{2}):(\d{2})(?:\.\d+)?\s*$")]
    private static partial Regex UptimePattern();

    /// <summary>Parses Python's <c>str(timedelta)</c>: <c>7:49:45</c>, <c>1 day, 7:49:45</c>, <c>12 days, 0:00:03</c>.</summary>
    internal static double? ParseUptime(string? text)
    {
        if (text is null || UptimePattern().Match(text) is not { Success: true } m)
        {
            return null;
        }

        var days = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return (days * 86400d) + (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 3600d)
               + (int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * 60d) + int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
    }

    private void PublishUptime(JsonNode? uptime)
    {
        // v4 answers with a bare string, v3 with {"uptime": "..."}.
        var text = uptime is JsonValue ? JsonValues.ToText(uptime) : JsonRead.Str(uptime, "uptime");
        Num(S("uptime", "Uptime", SensorCategory.System, Units.Seconds, "System", SensorKinds.Duration, 0, stale: Slow(30)), ParseUptime(text));
    }
}
