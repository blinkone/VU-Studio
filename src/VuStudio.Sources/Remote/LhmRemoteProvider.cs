using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Remote;

/// <summary>Creates <c>lhm-remote</c> providers: the sensors of a PC running LibreHardwareMonitor's remote web server.</summary>
public sealed class LhmRemoteProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "lhm-remote",
        Name = L("LibreHardwareMonitor (remote)", "LibreHardwareMonitor (remoto)"),
        Description = L(
            "Reads the sensors of another Windows PC through LibreHardwareMonitor's Remote Web Server (Options > Remote Web Server).",
            "Legge i sensori di un altro PC Windows tramite il Remote Web Server di LibreHardwareMonitor (Opzioni > Remote Web Server)."),
        Icon = "desktop-tower-monitor",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Url("url", L("Server address", "Indirizzo del server"), help: L("Address and port of the web server (default port 8085).", "Indirizzo e porta del server web (porta predefinita 8085)."), placeholder: "http://pc.local:8085", required: true),
            Setting.Text("username", L("User name", "Nome utente"), help: L("Only if authentication is enabled in LibreHardwareMonitor.", "Solo se l'autenticazione è attiva in LibreHardwareMonitor.")),
            Setting.Secret("password", L("Password", "Password")),
            Setting.Interval(2, min: 0.5, max: 3600),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the computer name.", "Etichetta host dei sensori. Predefinito: il nome del computer.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var uri = NetHttp.NormalizeBase(s.GetString("url"));
        if (uri is not null && uri.AbsolutePath is "" or "/")
        {
            uri = NetHttp.Combine(uri, "data.json");
        }

        return new LhmRemoteProvider(
            config.Id,
            uri,
            NetHttp.Basic(s.GetString("username"), s.GetString("password")),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 2),
            ProviderServices.Http(services),
            ProviderServices.Time(services),
            ProviderServices.Logger<LhmRemoteProvider>(services));
    }
}

/// <summary>A sensor found in the <c>data.json</c> tree, converted to canonical units.</summary>
internal sealed record LhmSensor(string SensorId, string Name, string? Hardware, string? HardwareId, string Type, double? Value, string Unit);

/// <summary>Parser of LibreHardwareMonitor's <c>data.json</c> tree. See docs/integrations/windows-sources.md section 5.</summary>
internal static partial class LhmTree
{
    [GeneratedRegex(@"^\s*([-+]?(?:\d[\d.,]*|[.,]\d+)(?:[eE][-+]?\d+)?)\s*(.*?)\s*$")]
    private static partial Regex NumberWithSuffix();

    /// <summary>Walks the tree; returns the computer name and every node that has a <c>SensorId</c>.</summary>
    public static (string? Computer, List<LhmSensor> Sensors) Parse(JsonNode? root)
    {
        var sensors = new List<LhmSensor>();
        var children = JsonRead.Items(root, "Children").ToList();
        // Root > computer > hardware…; tolerate trees that start directly at the computer or the hardware.
        var computerNode = children.Count == 1 && JsonRead.Get(children[0], "HardwareId") is null && JsonRead.Get(children[0], "SensorId") is null ? children[0] : null;
        string? computer = computerNode is null ? null : JsonRead.Str(computerNode, "Text");
        Walk(computerNode ?? root, null, null, sensors);
        return (computer, sensors);
    }

    private static void Walk(JsonNode? node, string? hardware, string? hardwareId, List<LhmSensor> sensors)
    {
        foreach (var child in JsonRead.Items(node, "Children"))
        {
            var text = JsonRead.Str(child, "Text") ?? "";
            if (JsonRead.Str(child, "SensorId") is { Length: > 0 } sensorId && JsonRead.Str(child, "Type") is { } type)
            {
                var raw = JsonRead.Get(child, "RawValue") ?? JsonRead.Get(child, "Value");
                var (value, unit) = Convert(type, raw is null || JsonValues.ToText(raw) is "" or "-" ? JsonRead.Get(child, "Value") : raw);
                sensors.Add(new LhmSensor(sensorId, text, hardware, hardwareId, type, value, unit));
            }
            else if (JsonRead.Str(child, "HardwareId") is { Length: > 0 } id)
            {
                Walk(child, text, id, sensors); // sub-hardware replaces the device name
            }
            else
            {
                Walk(child, hardware, hardwareId, sensors); // "Temperatures", "Load"… group nodes
            }
        }
    }

    /// <summary>Converts a displayed value ("52,3 °C", "1.2 MB/s", 4.5) to a canonical number and unit for the given LHM sensor type.</summary>
    internal static (double? Value, string Unit) Convert(string type, JsonNode? node)
    {
        var unit = UnitOf(type);
        if (type == "TimeSpan")
        {
            return (TimeSpan.TryParse(JsonValues.ToText(node), System.Globalization.CultureInfo.InvariantCulture, out var span) ? span.TotalSeconds : null, Units.Seconds);
        }

        double number;
        string suffix = "";
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            if (!JsonValues.TryGetNumber(node, out number))
            {
                return (null, unit);
            }
        }
        else
        {
            var match = NumberWithSuffix().Match(JsonValues.ToText(node) ?? "");
            if (!match.Success || !NumberParser.TryParse(match.Groups[1].Value, out number))
            {
                return (null, unit);
            }

            suffix = match.Groups[2].Value;
        }

        return (Scale(type, number, suffix), unit);
    }

    private static double Scale(string type, double n, string suffix)
    {
        var s = suffix.Trim();
        switch (type)
        {
            case "Temperature":
                return s.Contains('F') ? (n - 32) * 5 / 9 : n;
            case "Clock":
                return s.StartsWith("GHz", StringComparison.OrdinalIgnoreCase) ? n * 1000 : n;
            case "Frequency":
                return s.StartsWith("kHz", StringComparison.OrdinalIgnoreCase) ? n * 1000 : s.StartsWith("MHz", StringComparison.OrdinalIgnoreCase) ? n * 1e6 : s.StartsWith("GHz", StringComparison.OrdinalIgnoreCase) ? n * 1e9 : n;
            case "Energy":
                return s.StartsWith("Wh", StringComparison.Ordinal) ? n : s.StartsWith("kWh", StringComparison.Ordinal) ? n * 1000 : n / 1000; // LHM energy sensors are mWh
            case "Data":
                return n * ByteMultiplier(s, 1024d * 1024 * 1024);
            case "SmallData":
                return n * ByteMultiplier(s, 1024d * 1024);
            case "Throughput":
                return n * ThroughputMultiplier(s);
            case "Power":
                return s.StartsWith("mW", StringComparison.Ordinal) ? n / 1000 : s.StartsWith("kW", StringComparison.Ordinal) ? n * 1000 : n;
            case "Voltage":
                return s.StartsWith("mV", StringComparison.Ordinal) ? n / 1000 : n;
            case "Current":
                return s.StartsWith("mA", StringComparison.Ordinal) ? n / 1000 : n;
            default:
                return n;
        }
    }

    private static double ByteMultiplier(string suffix, double fallback) => suffix.ToUpperInvariant() switch
    {
        "B" => 1,
        "KB" or "KIB" => 1024,
        "MB" or "MIB" => 1024d * 1024,
        "GB" or "GIB" => 1024d * 1024 * 1024,
        "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
        _ => fallback,
    };

    private static double ThroughputMultiplier(string suffix) => suffix switch
    {
        "B/s" => 1,
        "KB/s" => 1024,
        "MB/s" => 1024d * 1024,
        "GB/s" => 1024d * 1024 * 1024,
        "bps" => 1d / 8,
        "Kbps" or "kbps" => 1000d / 8,
        "Mbps" => 1_000_000d / 8,
        "Gbps" => 1_000_000_000d / 8,
        _ => 1, // LHM's raw throughput is bytes per second
    };

    public static string UnitOf(string type) => type switch
    {
        "Voltage" => Units.Volt,
        "Current" => Units.Ampere,
        "Power" => Units.Watt,
        "Clock" => Units.MHz,
        "Frequency" => Units.Hz,
        "Temperature" => Units.Celsius,
        "Load" or "Control" or "Level" or "Humidity" => Units.Percent,
        "Fan" => Units.Rpm,
        "Flow" => Units.LitersPerHour,
        "Data" or "SmallData" => Units.Bytes,
        "Throughput" => Units.BytesPerSecond,
        "Energy" => Units.WattHour,
        "Noise" => Units.Decibel,
        "TimeSpan" => Units.Seconds,
        _ => Units.None,
    };

    public static string? KindOf(string type) => type switch
    {
        "Voltage" => SensorKinds.Voltage,
        "Current" => SensorKinds.Current,
        "Power" => SensorKinds.Power,
        "Clock" or "Frequency" => SensorKinds.Clock,
        "Temperature" => SensorKinds.Temperature,
        "Load" => SensorKinds.Load,
        "Control" or "Level" => SensorKinds.Level,
        "Humidity" => SensorKinds.Humidity,
        "Fan" => SensorKinds.Fan,
        "Flow" => SensorKinds.Flow,
        "Data" or "SmallData" => SensorKinds.Data,
        "Throughput" => SensorKinds.Throughput,
        "Energy" => SensorKinds.Energy,
        "Noise" => SensorKinds.Level,
        "TimeSpan" => SensorKinds.Duration,
        _ => null,
    };

    public static SensorCategory CategoryOf(string? hardwareId, string type)
    {
        var first = (hardwareId ?? "").Trim('/').Split('/')[0].ToLowerInvariant();
        var fromHardware = first switch
        {
            "amdcpu" or "intelcpu" or "cpu" => SensorCategory.Cpu,
            var g when g.StartsWith("gpu", StringComparison.Ordinal) => SensorCategory.Gpu,
            "ram" or "vram" or "memory" => SensorCategory.Memory,
            "hdd" or "ssd" or "nvme" or "storage" => SensorCategory.Storage,
            "lpc" or "motherboard" or "superio" or "embeddedcontroller" => SensorCategory.Motherboard,
            "nic" or "network" => SensorCategory.Network,
            "battery" => SensorCategory.Battery,
            "psu" => SensorCategory.Power,
            "cooler" or "fancontroller" or "aquacomputer" or "heatmaster" or "kraken" => SensorCategory.Cooling,
            _ => SensorCategory.Custom,
        };
        if (fromHardware != SensorCategory.Custom)
        {
            return fromHardware;
        }

        return type switch
        {
            "Fan" or "Flow" => SensorCategory.Cooling,
            "Power" or "Voltage" or "Current" or "Energy" => SensorCategory.Power,
            "Data" or "SmallData" => SensorCategory.Storage,
            "Throughput" => SensorCategory.Network,
            _ => SensorCategory.System,
        };
    }
}

/// <summary>Polls <c>data.json</c> of LibreHardwareMonitor's web server.</summary>
internal sealed class LhmRemoteProvider : NetworkPollingProvider
{
    private readonly Uri? _uri;
    private readonly System.Net.Http.Headers.AuthenticationHeaderValue? _auth;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;

    public LhmRemoteProvider(string id, Uri? uri, System.Net.Http.Headers.AuthenticationHeaderValue? auth, string? nameOverride, TimeSpan interval, HttpClient client, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger)
    {
        _uri = uri;
        _auth = auth;
        _nameOverride = nameOverride;
        _client = client;
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_uri is null)
        {
            throw new SourceUnavailableException("Enter the address of the LibreHardwareMonitor web server in the settings.");
        }

        var root = await NetHttp.GetJsonAsync(_client, _uri, TimeSpan.FromSeconds(8), cancellationToken, r => r.Headers.Authorization = _auth, "user name or password").ConfigureAwait(false);
        var (computer, sensors) = LhmTree.Parse(root);
        if (sensors.Count == 0)
        {
            throw new InvalidDataException("The answer contains no sensors. Is this the data.json of LibreHardwareMonitor?");
        }

        var host = _nameOverride ?? computer ?? _uri.Host;
        using var _ = Sweep(static _ => true);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sensors)
        {
            var local = LocalIds.Unique(LocalIds.SlugPath(s.SensorId, "sensor"), used);
            var type = s.Type;
            var percent = s.Unit == Units.Percent;
            var info = Info(
                local,
                s.Name,
                LhmTree.CategoryOf(s.HardwareId, type),
                s.Unit,
                s.Hardware,
                host,
                LhmTree.KindOf(type),
                min: percent || s.Unit is Units.Celsius or Units.Rpm or Units.Watt or Units.BytesPerSecond or Units.Bytes ? 0 : null,
                max: percent ? 100 : s.Unit == Units.Celsius ? 100 : null);
            Num(info, s.Value);
        }
    }
}
