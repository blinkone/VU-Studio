using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Remote;

/// <summary>Creates <c>prometheus</c> providers: an exporter's <c>/metrics</c> page or a Prometheus server queried with PromQL.</summary>
public sealed class PrometheusProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "prometheus",
        Name = L("Prometheus", "Prometheus"),
        Description = L(
            "Reads a Prometheus exporter (node_exporter presets plus your own metrics) or runs PromQL queries on a Prometheus server.",
            "Legge un exporter Prometheus (preset node_exporter più metriche personalizzate) oppure esegue query PromQL su un server Prometheus."),
        Icon = "chart-timeline-variant",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Select("mode", L("Mode", "Modalità"), "exporter",
            [
                Setting.Option("exporter", "Exporter (/metrics page)", "Exporter (pagina /metrics)"),
                Setting.Option("server", "Prometheus server (PromQL)", "Server Prometheus (PromQL)"),
            ]),
            Setting.Url("url", L("Address", "Indirizzo"), help: L("Exporter: http://host:9100/metrics. Server: http://host:9090.", "Exporter: http://host:9100/metrics. Server: http://host:9090."), placeholder: "http://nas.local:9100/metrics", required: true),
            Setting.Text("username", L("User name", "Nome utente"), help: L("Optional HTTP Basic authentication.", "Autenticazione HTTP Basic facoltativa.")),
            Setting.Secret("password", L("Password", "Password")),
            Setting.Secret("token", L("Bearer token", "Token Bearer"), L("Optional, instead of user name and password.", "Facoltativo, in alternativa a nome utente e password.")),
            Setting.Interval(5, min: 1, max: 3600),
            Setting.Bool("presets", L("node_exporter presets", "Preset node_exporter"), true, L("Exporter mode: CPU, memory, load, filesystems, network, disks, temperatures, fans, battery and uptime.", "Modalità exporter: CPU, memoria, carico, filesystem, rete, dischi, temperature, ventole, batteria e uptime.")),
            Setting.Table(
                "metrics",
                L("Custom metrics (exporter mode)", "Metriche personalizzate (modalità exporter)"),
                [
                    Setting.Text("metric", L("Metric", "Metrica"), placeholder: "node_hwmon_temp_celsius", required: true),
                    Setting.Text("labels", L("Label filter", "Filtro etichette"), help: L("For example chip=\"k10temp\",sensor=~\"temp1|temp2\".", "Ad esempio chip=\"k10temp\",sensor=~\"temp1|temp2\"."), placeholder: "mode=\"idle\""),
                    Setting.Bool("rate", L("As rate", "Come tasso"), false, L("Turns a counter into a per-second rate.", "Trasforma un contatore in un tasso al secondo.")),
                    Setting.Text("unit", L("Unit", "Unità"), help: L("Canonical unit: %, °C, W, B, B/s, s…", "Unità canonica: %, °C, W, B, B/s, s…")),
                    Setting.Number("multiplier", L("Multiplier", "Moltiplicatore"), 1),
                    Setting.Text("name", L("Sensor name", "Nome del sensore")),
                    Setting.Text("id", L("ID", "ID")),
                ],
                help: L("Each row becomes a sensor (one per matching series).", "Ogni riga diventa un sensore (uno per serie corrispondente).")),
            Setting.Table(
                "queries",
                L("PromQL queries (server mode)", "Query PromQL (modalità server)"),
                [
                    Setting.Text("query", L("Query", "Query"), placeholder: "100 * (1 - avg(rate(node_cpu_seconds_total{mode=\"idle\"}[1m])))", required: true),
                    Setting.Text("unit", L("Unit", "Unità")),
                    Setting.Number("multiplier", L("Multiplier", "Moltiplicatore"), 1),
                    Setting.Text("name", L("Sensor name", "Nome del sensore")),
                    Setting.Text("id", L("ID", "ID")),
                ],
                help: L("Each query becomes a sensor (one per returned series).", "Ogni query diventa un sensore (uno per serie restituita).")),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the host name reported by node_exporter.", "Etichetta host dei sensori. Predefinito: il nome host comunicato da node_exporter.")),
            Setting.Bool("allowInvalidCertificate", L("Accept self-signed certificate", "Accetta certificato autofirmato"), false),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var (client, owner) = NetHttp.CreateClient(services, s.GetBool("allowInvalidCertificate"));
        var server = s.GetString("mode") == "server";
        var uri = NetHttp.NormalizeBase(s.GetString("url"));
        if (uri is not null && !server && uri.AbsolutePath is "" or "/")
        {
            uri = NetHttp.Combine(uri, "metrics");
        }

        return new PrometheusProvider(
            config.Id,
            server,
            uri,
            NetHttp.Basic(s.GetString("username"), s.GetString("password")) ?? NetHttp.Bearer(s.GetString("token")),
            s.GetBool("presets", true),
            PrometheusProvider.ReadRows(s.GetTable("metrics"), "metric"),
            PrometheusProvider.ReadRows(s.GetTable("queries"), "query"),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 5),
            client,
            owner,
            ProviderServices.Time(services),
            ProviderServices.Logger<PrometheusProvider>(services));
    }
}

/// <summary>One row of the custom metrics or queries table.</summary>
internal sealed record PromRow(string Expression, LabelMatcher Labels, bool AsRate, string Unit, double Multiplier, string IdBase, string Name);

/// <summary>Scrapes an exporter or queries a Prometheus server. See docs/integrations/network-sources.md section 2.</summary>
internal sealed partial class PrometheusProvider : NetworkPollingProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"^(lo|veth.*|docker.*|br-.*|virbr.*|vnet.*|tun\d*|tap\d*)$")]
    private static partial Regex IgnoredInterfaces();

    [GeneratedRegex(@"^(loop|ram|zram|fd)\d*")]
    private static partial Regex IgnoredDisks();

    private static readonly HashSet<string> IgnoredFsTypes = new(StringComparer.OrdinalIgnoreCase)
        { "tmpfs", "devtmpfs", "overlay", "squashfs", "ramfs", "proc", "sysfs", "cgroup", "cgroup2", "devpts", "fuse.lxcfs", "nsfs", "autofs", "shm", "efivarfs" };

    private static readonly string[] IgnoredMounts = ["/run", "/sys", "/proc", "/dev", "/var/lib/docker", "/var/lib/kubelet", "/snap", "/etc/"];

    private readonly bool _serverMode;
    private readonly Uri? _uri;
    private readonly System.Net.Http.Headers.AuthenticationHeaderValue? _auth;
    private readonly bool _presets;
    private readonly IReadOnlyList<PromRow> _metrics;
    private readonly IReadOnlyList<PromRow> _queries;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly CounterStore _counters = new();
    private string? _hostname;

    public PrometheusProvider(string id, bool serverMode, Uri? uri, System.Net.Http.Headers.AuthenticationHeaderValue? auth, bool presets, IReadOnlyList<PromRow> metrics, IReadOnlyList<PromRow> queries, string? nameOverride, TimeSpan interval, HttpClient client, IDisposable? owner, TimeProvider time, ILogger logger)
        : base(id, interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval, time, logger, owner)
    {
        _serverMode = serverMode;
        _uri = uri;
        _auth = auth;
        _presets = presets;
        _metrics = metrics;
        _queries = queries;
        _nameOverride = nameOverride;
        _client = client;
    }

    private string Host => _nameOverride ?? _hostname ?? _uri?.Host ?? "prometheus";

    protected override void OnSourceLost() => _counters.Clear();

    internal static IReadOnlyList<PromRow> ReadRows(IReadOnlyList<ProviderSettings> table, string expressionKey)
    {
        var rows = new List<PromRow>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table)
        {
            var expression = row.GetString(expressionKey);
            if (expression is null)
            {
                continue;
            }

            var name = row.GetString("name") ?? expression;
            var idBase = LocalIds.Unique(LocalIds.SlugPath(row.GetString("id") ?? name), used);
            var multiplier = row.GetDouble("multiplier", 1);
            rows.Add(new PromRow(expression, LabelMatcher.Parse(row.GetString("labels")), row.GetBool("rate"), row.GetString("unit") ?? Units.None, double.IsFinite(multiplier) ? multiplier : 1, idBase, name));
        }

        return rows;
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_uri is null)
        {
            throw new SourceUnavailableException("Enter the address in the settings.");
        }

        if (_serverMode)
        {
            await PollServerAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PollExporterAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ----- exporter mode -----

    private async Task PollExporterAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Authorization = _auth;
        request.Headers.TryAddWithoutValidation("Accept", "text/plain;version=0.0.4;q=0.9,*/*;q=0.1");
        var response = NetHttp.EnsureSuccess(await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false), "credentials");
        var now = Clock.TotalSeconds;
        var scrape = PrometheusText.Parse(response.Text, name => (_presets && name.StartsWith("node_", StringComparison.Ordinal)) || _metrics.Any(m => m.Expression == name));

        _hostname = scrape.Named("node_uname_info").Select(s => s.Label("nodename")).FirstOrDefault(n => n is not null) ?? _hostname;
        if (_presets)
        {
            if (scrape.Samples.Any(s => s.Name.StartsWith("node_", StringComparison.Ordinal)))
            {
                PublishNodeExporter(scrape, now);
            }
            else if (_metrics.Count == 0)
            {
                Warning = "No node_exporter metrics found at this address. Turn the presets off and add your own metrics, or check the URL.";
            }
        }

        for (var i = 0; i < _metrics.Count; i++)
        {
            PublishCustom(_metrics[i], i, scrape, now);
        }
    }

    private void PublishCustom(PromRow row, int index, PromScrape scrape, double now)
    {
        var baseId = "custom/" + row.IdBase;
        using var _ = Sweep(i => i == baseId || i.StartsWith(baseId + "/", StringComparison.Ordinal));
        var matches = scrape.Named(row.Expression).Where(row.Labels.Matches).ToList();
        if (matches.Count == 0)
        {
            Warning ??= $"Metric '{row.Expression}' has no matching series.";
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in matches)
        {
            var suffix = matches.Count > 1 ? LocalIds.Unique(LocalIds.Slug(sample.LabelText(), "series"), used) : null;
            var id = "custom/" + row.IdBase + (suffix is null ? "" : "/" + suffix);
            var name = suffix is null ? row.Name : $"{row.Name} {sample.LabelText()}";
            double? value = double.IsNaN(sample.Value) ? null : sample.Value;
            if (row.AsRate && value is { } v)
            {
                value = _counters.Rate($"custom|{index}|{id}", v, now);
            }

            Num(Info(id, name, SensorCategory.Custom, row.Unit, null, Host), value * row.Multiplier);
        }
    }

    private SensorInfo S(string id, string name, SensorCategory category, string unit, string? device = null, string? kind = null, double? min = null, double? max = null, string? icon = null) =>
        Info(id, name, category, unit, device, Host, kind, min, max, icon);

    /// <summary>The node_exporter presets: gauges are published as they are, counters become rates with reset handling.</summary>
    private void PublishNodeExporter(PromScrape scrape, double now)
    {
        PublishCpu(scrape, now);

        foreach (var (metric, id, label) in new[] { ("node_load1", "load/1", "1 min"), ("node_load5", "load/5", "5 min"), ("node_load15", "load/15", "15 min") })
        {
            if (scrape.First(metric) is { } load)
            {
                Num(S(id, $"Load average ({label})", SensorCategory.Cpu, Units.None, "CPU", SensorKinds.Load, 0), load);
            }
        }

        PublishMemory(scrape);
        PublishFileSystems(scrape);
        PublishNetwork(scrape, now);
        PublishDisks(scrape, now);
        PublishHwmon(scrape);
        PublishMisc(scrape);
    }

    private void PublishCpu(PromScrape scrape, double now)
    {
        double total = 0, idle = 0;
        var complete = false;
        var ok = true;
        foreach (var s in scrape.Named("node_cpu_seconds_total"))
        {
            complete = true;
            if (_counters.Delta($"cpu|{s.Label("cpu")}|{s.Label("mode")}", s.Value, now) is not { } d)
            {
                ok = false;
                continue;
            }

            total += d.Delta;
            if (s.Label("mode") is "idle" or "iowait")
            {
                idle += d.Delta;
            }
        }

        if (complete)
        {
            Num(S("cpu/total", "CPU load", SensorCategory.Cpu, Units.Percent, "CPU", SensorKinds.Load, 0, 100), ok && total > 0 ? Math.Clamp(100 * (1 - (idle / total)), 0, 100) : null);
        }
    }

    private void PublishMemory(PromScrape scrape)
    {
        if (scrape.First("node_memory_MemTotal_bytes") is not { } total || total <= 0)
        {
            return;
        }

        var available = scrape.First("node_memory_MemAvailable_bytes") ?? scrape.First("node_memory_MemFree_bytes");
        if (available is { } avail)
        {
            Num(S("memory/percent", "Memory used", SensorCategory.Memory, Units.Percent, "Memory", SensorKinds.Percent, 0, 100), 100 * (1 - (avail / total)));
            Num(S("memory/used", "Memory used (bytes)", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), total - avail);
            Num(S("memory/available", "Memory available", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), avail);
        }

        Num(S("memory/total", "Memory total", SensorCategory.Memory, Units.Bytes, "Memory", min: 0), total);
        if (scrape.First("node_memory_SwapTotal_bytes") is { } swap && scrape.First("node_memory_SwapFree_bytes") is { } swapFree)
        {
            Num(S("swap/percent", "Swap used", SensorCategory.Memory, Units.Percent, "Swap", SensorKinds.Percent, 0, 100), swap > 0 ? 100 * (1 - (swapFree / swap)) : 0);
            Num(S("swap/used", "Swap used (bytes)", SensorCategory.Memory, Units.Bytes, "Swap", min: 0), swap - swapFree);
        }
    }

    private void PublishFileSystems(PromScrape scrape)
    {
        using var _ = Sweep("fs/");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var size in scrape.Named("node_filesystem_size_bytes"))
        {
            var mount = size.Label("mountpoint");
            var type = size.Label("fstype");
            if (mount is null || size.Value <= 0 || (type is not null && IgnoredFsTypes.Contains(type)) || IgnoredMounts.Any(p => mount.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            var device = size.Label("device");
            var match = scrape.Named("node_filesystem_avail_bytes").FirstOrDefault(a => a.Label("mountpoint") == mount && a.Label("device") == device);
            if (match is null)
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(mount, "root"), used);
            Num(S($"fs/{slug}/percent", $"{mount} used", SensorCategory.Storage, Units.Percent, mount, SensorKinds.Percent, 0, 100), 100 * (1 - (match.Value / size.Value)));
            Num(S($"fs/{slug}/used", $"{mount} used (bytes)", SensorCategory.Storage, Units.Bytes, mount, min: 0), size.Value - match.Value);
            Num(S($"fs/{slug}/free", $"{mount} free", SensorCategory.Storage, Units.Bytes, mount, min: 0), match.Value);
            Num(S($"fs/{slug}/size", $"{mount} size", SensorCategory.Storage, Units.Bytes, mount, min: 0), size.Value);
        }
    }

    private void PublishNetwork(PromScrape scrape, double now)
    {
        using var _ = Sweep("net/");
        foreach (var rx in scrape.Named("node_network_receive_bytes_total"))
        {
            var device = rx.Label("device");
            if (device is null || IgnoredInterfaces().IsMatch(device))
            {
                continue;
            }

            var slug = LocalIds.Slug(device);
            Num(S($"net/{slug}/rx", $"{device} download", SensorCategory.Network, Units.BytesPerSecond, device, SensorKinds.Throughput, 0, icon: "download-network"), _counters.Rate("rx|" + device, rx.Value, now));
            var tx = scrape.Named("node_network_transmit_bytes_total").FirstOrDefault(t => t.Label("device") == device);
            if (tx is not null)
            {
                Num(S($"net/{slug}/tx", $"{device} upload", SensorCategory.Network, Units.BytesPerSecond, device, SensorKinds.Throughput, 0, icon: "upload-network"), _counters.Rate("tx|" + device, tx.Value, now));
            }
        }
    }

    private void PublishDisks(PromScrape scrape, double now)
    {
        using var _ = Sweep("disk/");
        foreach (var read in scrape.Named("node_disk_read_bytes_total"))
        {
            var device = read.Label("device");
            if (device is null || IgnoredDisks().IsMatch(device))
            {
                continue;
            }

            var slug = LocalIds.Slug(device);
            Num(S($"disk/{slug}/read", $"{device} read", SensorCategory.Storage, Units.BytesPerSecond, device, SensorKinds.Throughput, 0), _counters.Rate("dr|" + device, read.Value, now));
            if (scrape.Named("node_disk_written_bytes_total").FirstOrDefault(w => w.Label("device") == device) is { } write)
            {
                Num(S($"disk/{slug}/write", $"{device} write", SensorCategory.Storage, Units.BytesPerSecond, device, SensorKinds.Throughput, 0), _counters.Rate("dw|" + device, write.Value, now));
            }

            if (scrape.Named("node_disk_io_time_seconds_total").FirstOrDefault(w => w.Label("device") == device) is { } busy)
            {
                var rate = _counters.Rate("db|" + device, busy.Value, now);
                Num(S($"disk/{slug}/busy", $"{device} busy", SensorCategory.Storage, Units.Percent, device, SensorKinds.Load, 0, 100), rate is { } r ? Math.Clamp(r * 100, 0, 100) : null);
            }
        }
    }

    private void PublishHwmon(PromScrape scrape)
    {
        using var _ = Sweep("hwmon/");
        var chipNames = scrape.Named("node_hwmon_chip_names").Where(s => s.Label("chip") is not null).GroupBy(s => s.Label("chip")!).ToDictionary(g => g.Key, g => g.First().Label("chip_name") ?? g.Key);
        var labels = scrape.Named("node_hwmon_sensor_label").ToDictionary(s => (s.Label("chip") ?? "") + "|" + (s.Label("sensor") ?? ""), s => s.Label("label") ?? "", StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        void Emit(string metric, string kindPath, SensorCategory? forcedCategory, string unit, string kind, double? max)
        {
            foreach (var sample in scrape.Named(metric))
            {
                var chip = sample.Label("chip") ?? "chip";
                var sensor = sample.Label("sensor") ?? "sensor";
                var chipName = chipNames.GetValueOrDefault(chip, chip);
                var label = labels.GetValueOrDefault(chip + "|" + sensor);
                var shown = string.IsNullOrEmpty(label) ? sensor : label;
                var id = LocalIds.Unique($"hwmon/{kindPath}/{LocalIds.Slug(chipName)}/{LocalIds.Slug(shown)}", used);
                var category = forcedCategory ?? CategoryOf(chipName);
                Num(S(id, $"{chipName} {shown}", category, unit, chipName, kind, 0, max), double.IsNaN(sample.Value) ? null : sample.Value);
            }
        }

        Emit("node_hwmon_temp_celsius", "temp", null, Units.Celsius, SensorKinds.Temperature, 100);
        Emit("node_hwmon_fan_rpm", "fan", SensorCategory.Cooling, Units.Rpm, SensorKinds.Fan, null);
        Emit("node_hwmon_power_average_watt", "power", SensorCategory.Power, Units.Watt, SensorKinds.Power, null);
        foreach (var zone in scrape.Named("node_thermal_zone_temp"))
        {
            var type = zone.Label("type") ?? "zone";
            var id = LocalIds.Unique($"hwmon/temp/thermal/{LocalIds.Slug(type)}", used);
            Num(S(id, $"Thermal zone {type}", type.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? SensorCategory.Cpu : SensorCategory.Motherboard, Units.Celsius, type, SensorKinds.Temperature, 0, 100), zone.Value);
        }
    }

    private static SensorCategory CategoryOf(string chip)
    {
        var c = chip.ToLowerInvariant();
        if (c.Contains("k10temp") || c.Contains("coretemp") || c.Contains("zenpower") || c.Contains("cpu"))
        {
            return SensorCategory.Cpu;
        }

        if (c.Contains("amdgpu") || c.Contains("nouveau") || c.Contains("radeon") || c.Contains("nvidia"))
        {
            return SensorCategory.Gpu;
        }

        return c.Contains("nvme") || c.Contains("drivetemp") ? SensorCategory.Storage : SensorCategory.Motherboard;
    }

    private void PublishMisc(PromScrape scrape)
    {
        if (scrape.First("node_time_seconds") is { } time && scrape.First("node_boot_time_seconds") is { } boot)
        {
            Num(S("uptime", "Uptime", SensorCategory.System, Units.Seconds, "System", SensorKinds.Duration, 0), Math.Max(0, time - boot));
        }

        using var _ = Sweep("battery/");
        foreach (var cap in scrape.Named("node_power_supply_capacity"))
        {
            var name = cap.Label("power_supply") ?? "battery";
            Num(S($"battery/{LocalIds.Slug(name)}", $"Battery {name}", SensorCategory.Battery, Units.Percent, name, SensorKinds.Level, 0, 100), cap.Value);
        }
    }

    // ----- server mode -----

    private async Task PollServerAsync(CancellationToken cancellationToken)
    {
        if (_queries.Count == 0)
        {
            throw new SourceUnavailableException("Add at least one PromQL query in the settings.");
        }

        foreach (var row in _queries)
        {
            var uri = NetHttp.Combine(_uri!, "api/v1/query?query=" + Uri.EscapeDataString(row.Expression));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = _auth;
            var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity)
            {
                Warning ??= $"Query '{row.Name}': {JsonRead.Str(response.Json(), "error") ?? "rejected by the server"}";
                continue;
            }

            NetHttp.EnsureSuccess(response, "credentials");
            PublishQueryResult(row, ParseQueryResult(response.Json()));
        }
    }

    /// <summary>Parses an instant query answer into (label text, value) pairs. Scalars and vectors are supported.</summary>
    internal static IReadOnlyList<(string Labels, double? Value)> ParseQueryResult(JsonNode? root)
    {
        if (JsonRead.Str(root, "status") is { } status && status != "success")
        {
            throw new InvalidDataException($"Prometheus answered '{status}': {JsonRead.Str(root, "error")}");
        }

        var data = JsonRead.Get(root, "data");
        var type = JsonRead.Str(data, "resultType");
        var result = JsonRead.Get(data, "result");
        static double? Value(JsonNode? pair) =>
            pair is JsonArray { Count: >= 2 } a && PrometheusText.TryParseValue(JsonValues.ToText(a[1]) ?? "", out var v) && double.IsFinite(v) ? v : null;

        if (type == "scalar")
        {
            return [("", Value(result))];
        }

        var list = new List<(string, double?)>();
        foreach (var item in JsonRead.Items(data, "result"))
        {
            var metric = JsonRead.Get(item, "metric") as JsonObject;
            var labels = string.Join(' ', (metric ?? []).Where(kv => kv.Key != "__name__").Select(kv => JsonValues.ToText(kv.Value)));
            list.Add((labels, Value(JsonRead.Get(item, "value"))));
        }

        return list;
    }

    private void PublishQueryResult(PromRow row, IReadOnlyList<(string Labels, double? Value)> result)
    {
        var baseId = "query/" + row.IdBase;
        using var _ = Sweep(i => i == baseId || i.StartsWith(baseId + "/", StringComparison.Ordinal));
        if (result.Count == 0)
        {
            Warning ??= $"Query '{row.Name}' returned no data.";
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (labels, value) in result)
        {
            var multi = result.Count > 1;
            var suffix = multi ? LocalIds.Unique(LocalIds.Slug(labels, "series"), used) : null;
            var id = "query/" + row.IdBase + (suffix is null ? "" : "/" + suffix);
            var name = multi && labels.Length > 0 ? $"{row.Name} {labels}" : row.Name;
            Num(Info(id, name, SensorCategory.Custom, row.Unit, null, Host), value * row.Multiplier);
        }
    }
}
