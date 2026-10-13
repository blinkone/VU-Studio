using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Router;

/// <summary>Creates <c>upnp-wan</c> providers: internet throughput and link state from a router's UPnP counters (and Google Nest Wifi status).</summary>
public sealed class UpnpWanProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "upnp-wan",
        Name = L("Router internet traffic (UPnP)", "Traffico internet del router (UPnP)"),
        Description = L(
            "Download and upload rate, link state and public address of your internet router, read from its UPnP counters. Finds the router by itself, also behind a Google Nest Wifi (double NAT). The router must have UPnP enabled; some routers ship with it off.",
            "Velocità di download e upload, stato del collegamento e indirizzo pubblico del router internet, letti dai suoi contatori UPnP. Trova il router da solo, anche dietro a un Google Nest Wifi (doppio NAT). Sul router deve essere attivo UPnP; alcuni router lo hanno disattivato di fabbrica."),
        Icon = "router-wireless",
        Group = ProviderGroup.Network,
        Settings =
        [
            Setting.Select("mode", L("Router", "Router"), "auto",
            [
                Setting.Option("auto", "Find automatically", "Trova automaticamente"),
                Setting.Option("manual", "Enter the address", "Inserisci l'indirizzo"),
            ]),
            Setting.Url("url", L("Router address", "Indirizzo del router"), help: L("Manual mode: the router's IP address (for example 192.168.1.1) or the full URL of its UPnP description (http://192.168.1.1:5000/rootDesc.xml).", "Modalità manuale: l'indirizzo IP del router (ad esempio 192.168.1.1) o l'URL completo della sua descrizione UPnP (http://192.168.1.1:5000/rootDesc.xml)."), placeholder: "192.168.1.1"),
            Setting.Url("nestUrl", L("Google Nest Wifi address", "Indirizzo del Google Nest Wifi"), help: L("Optional. Leave empty to look for a Nest Wifi at 192.168.86.1 / testwifi.here. Its status adds 'internet online' and uptime, and tells us where the upstream router is.", "Facoltativo. Lascia vuoto per cercare un Nest Wifi a 192.168.86.1 / testwifi.here. Il suo stato aggiunge 'internet online' e uptime, e indica dove si trova il router a monte."), placeholder: "http://192.168.86.1"),
            Setting.Interval(2, min: 1, max: 30, help: L("Seconds between reads. Keep it under 30: the 32-bit counters of many routers wrap quickly.", "Secondi tra una lettura e l'altra. Tienilo sotto 30: i contatori a 32 bit di molti router si azzerano in fretta.")),
            Setting.Text("name", L("Display name", "Nome visualizzato"), help: L("Host label of the sensors. Default: the router's model.", "Etichetta host dei sensori. Predefinito: il modello del router.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        return new UpnpWanProvider(
            config.Id,
            s.GetString("mode") == "manual",
            s.GetString("url"),
            NetHttp.NormalizeBase(s.GetString("nestUrl")),
            s.GetString("name") ?? (string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim()),
            s.GetSeconds("interval", 2),
            ProviderServices.Http(services),
            new SystemUpnpTransport(),
            ProviderServices.Time(services),
            ProviderServices.Logger<UpnpWanProvider>(services));
    }
}

/// <summary>Reads WAN counters of an Internet Gateway Device. See docs/integrations/network-sources.md section 7.</summary>
internal sealed class UpnpWanProvider : NetworkPollingProvider
{
    private static readonly TimeSpan SoapTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RediscoverEvery = TimeSpan.FromSeconds(30);
    private static readonly string[] CommonDescriptionPaths = [":5000/rootDesc.xml", ":49000/igddesc.xml", ":52869/picsdesc.xml", ":37215/rootDesc.xml", ":1900/igd.xml"];
    private static readonly string[] DefaultNestAddresses = ["http://192.168.86.1", "http://testwifi.here"];

    private readonly bool _manual;
    private readonly string? _manualTarget;
    private readonly Uri? _nestConfigured;
    private readonly string? _nameOverride;
    private readonly HttpClient _client;
    private readonly IUpnpTransport _transport;
    private readonly WanCounter _rx = new();
    private readonly WanCounter _tx = new();
    private readonly Dictionary<string, TimeSpan> _lastRun = new(StringComparer.Ordinal);

    private UpnpDevice? _device;
    private UpnpService? _connection;
    private bool _countersOk;
    private TimeSpan? _lastDiscovery;
    private string? _discoveryError;
    private int _soapFailures;
    private double? _maxDownBytes;
    private double? _maxUpBytes;
    private Uri? _nestBase;
    private bool _nestDefaultsTried;
    private int _nestFailures;

    public UpnpWanProvider(string id, bool manual, string? manualTarget, Uri? nestUri, string? nameOverride, TimeSpan interval, HttpClient client, IUpnpTransport transport, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger)
    {
        _manual = manual;
        _manualTarget = manualTarget;
        _nestConfigured = nestUri;
        _nameOverride = nameOverride;
        _client = client;
        _transport = transport;
    }

    private string Host => _nameOverride ?? _device?.Title ?? "Router";

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }

        await PollCountersAsync(cancellationToken).ConfigureAwait(false);
        if (Due("slow", 30))
        {
            await PollLinkAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_nestBase is not null && Due("nest", 15))
        {
            await PollNestAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool Due(string key, double seconds)
    {
        var now = Clock;
        if (_lastRun.TryGetValue(key, out var last) && (now - last).TotalSeconds < seconds)
        {
            return false;
        }

        _lastRun[key] = now;
        return true;
    }

    // ----- discovery -----

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_lastDiscovery is { } last && Clock - last < RediscoverEvery)
        {
            throw new SourceUnavailableException(_discoveryError ?? "No router found yet.");
        }

        _lastDiscovery = Clock;
        try
        {
            var chosen = await FindAndChooseAsync(cancellationToken).ConfigureAwait(false);
            _discoveryError = null;
            _ = chosen;
        }
        catch (SourceUnavailableException ex)
        {
            _discoveryError = ex.Message;
            throw;
        }
    }

    private async Task<bool> FindAndChooseAsync(CancellationToken cancellationToken)
    {
        var nest = await ProbeNestAsync(cancellationToken).ConfigureAwait(false);
        var stage1 = new List<Uri>();
        if (_manual)
        {
            stage1.AddRange(await ManualCandidatesAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            if (nest?.GatewayAddress is { } gw && IPAddress.TryParse(gw, out var upstream))
            {
                stage1.AddRange((await _transport.UnicastSearchAsync(upstream, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false)).Select(r => r.Location));
            }

            var multicast = _transport.MulticastSearchAsync(TimeSpan.FromSeconds(3), cancellationToken);
            var gateway = _transport.DefaultGateway();
            var unicast = gateway is null ? Task.FromResult<IReadOnlyList<SsdpResponse>>([]) : _transport.UnicastSearchAsync(gateway, TimeSpan.FromSeconds(2.5), cancellationToken);
            stage1.AddRange((await multicast.ConfigureAwait(false)).Concat(await unicast.ConfigureAwait(false)).Select(r => r.Location));
        }

        if (await ChooseAsync(stage1, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!_manual && await _transport.SecondHopAsync(cancellationToken).ConfigureAwait(false) is { } hop)
        {
            var stage2 = (await _transport.UnicastSearchAsync(hop, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false)).Select(r => r.Location).ToList();
            stage2.AddRange(CommonDescriptionPaths.Select(p => new Uri($"http://{hop}{p}")));
            if (await ChooseAsync(stage2, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw new SourceUnavailableException(_manual
            ? $"No UPnP internet gateway answered at '{_manualTarget}'. Check the address and enable UPnP in the router."
            : "No router with UPnP found. Enable UPnP in your router (TIM, Vodafone and Fastweb ship it off), or choose manual mode and enter its address.");
    }

    private async Task<List<Uri>> ManualCandidatesAsync(CancellationToken cancellationToken)
    {
        var result = new List<Uri>();
        if (string.IsNullOrWhiteSpace(_manualTarget))
        {
            throw new SourceUnavailableException("Enter the router address in the settings, or switch to automatic mode.");
        }

        var uri = NetHttp.NormalizeBase(_manualTarget);
        if (uri is null)
        {
            throw new SourceUnavailableException($"'{_manualTarget}' is not a valid address.");
        }

        if (uri.AbsolutePath.Length > 1)
        {
            result.Add(uri); // a full description URL
            return result;
        }

        IPAddress? ip = IPAddress.TryParse(uri.Host, out var parsed) ? parsed : null;
        if (ip is null)
        {
            try
            {
                ip = (await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        if (ip is not null)
        {
            result.AddRange((await _transport.UnicastSearchAsync(ip, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false)).Select(r => r.Location));
            result.AddRange(CommonDescriptionPaths.Select(p => new Uri($"{uri.Scheme}://{ip}{p}")));
        }

        return result;
    }

    /// <summary>Takes the first gateway whose byte counters work; falls back to the first gateway at all.</summary>
    private async Task<bool> ChooseAsync(IEnumerable<Uri> candidates, CancellationToken cancellationToken)
    {
        UpnpDevice? fallback = null;
        string? fallbackNote = null;
        foreach (var location in candidates.DistinctBy(u => u.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            UpnpDevice device;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, location);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
                var response = await NetHttp.SendAsync(_client, request, SoapTimeout, cancellationToken, maxBytes: 1_000_000).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    continue;
                }

                device = UpnpDescription.Parse(response.Text, location);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Xml.XmlException or InvalidDataException)
            {
                continue;
            }

            if (!device.IsGateway)
            {
                continue;
            }

            string? problem = null;
            try
            {
                var reply = await UpnpSoap.CallAsync(_client, device.CommonInterface!, "GetTotalBytesReceived", SoapTimeout, cancellationToken).ConfigureAwait(false);
                if (!TryCounter(reply, "NewTotalBytesReceived", out var value))
                {
                    problem = "returns no byte counter";
                }
                else if (value == WanCounter.Saturated32)
                {
                    problem = "reports a saturated byte counter (4294967295)";
                }
            }
            catch (UpnpFaultException ex)
            {
                problem = $"does not provide byte counters ({ex.Message})";
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Xml.XmlException)
            {
                continue;
            }

            if (problem is null)
            {
                await SelectAsync(device, countersOk: true, cancellationToken).ConfigureAwait(false);
                return true;
            }

            fallback ??= device;
            fallbackNote ??= $"{device.Title} at {device.Location.Host} {problem}.";
        }

        if (fallback is null)
        {
            return false;
        }

        await SelectAsync(fallback, countersOk: false, cancellationToken).ConfigureAwait(false);
        Warning = fallbackNote + " Traffic rates are unavailable; try another router address (the one upstream) or the ping/network providers.";
        return true;
    }

    private async Task SelectAsync(UpnpDevice device, bool countersOk, CancellationToken cancellationToken)
    {
        _device = device;
        _countersOk = countersOk;
        _soapFailures = 0;
        _rx.Reset();
        _tx.Reset();
        _lastRun.Remove("slow");
        _connection = device.Connections.FirstOrDefault();
        foreach (var service in device.Connections)
        {
            try
            {
                var status = await UpnpSoap.CallAsync(_client, service, "GetStatusInfo", SoapTimeout, cancellationToken).ConfigureAwait(false);
                if (status.GetValueOrDefault("NewConnectionStatus") == "Connected")
                {
                    _connection = service;
                    break;
                }
            }
            catch (Exception ex) when (ex is UpnpFaultException or IOException or TimeoutException or System.Xml.XmlException)
            {
            }
        }

        Logger.LogInformation("Using {Device} at {Location} for WAN counters", device.Title, device.Location);
    }

    private static bool TryCounter(Dictionary<string, string> reply, string key, out ulong value)
    {
        value = 0;
        return reply.TryGetValue(key, out var text) && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // ----- polling the router -----

    private async Task<Dictionary<string, string>> CallAsync(UpnpService service, string action, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await UpnpSoap.CallAsync(_client, service, action, SoapTimeout, cancellationToken).ConfigureAwait(false);
            _soapFailures = 0;
            return reply;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Xml.XmlException)
        {
            if (++_soapFailures >= 5)
            {
                _device = null; // look for the router again, its address may have changed
                _soapFailures = 0;
            }

            throw;
        }
    }

    private async Task PollCountersAsync(CancellationToken cancellationToken)
    {
        if (!_countersOk)
        {
            Warning ??= "This router does not provide usable WAN byte counters.";
            return;
        }

        var common = _device!.CommonInterface!;
        var rxReply = await CallAsync(common, "GetTotalBytesReceived", cancellationToken).ConfigureAwait(false);
        var txReply = await CallAsync(common, "GetTotalBytesSent", cancellationToken).ConfigureAwait(false);
        var now = Clock;
        if (!TryCounter(rxReply, "NewTotalBytesReceived", out var rx) || !TryCounter(txReply, "NewTotalBytesSent", out var tx))
        {
            throw new InvalidDataException("The router returned no byte counters.");
        }

        var rxResult = _rx.Update(rx, now, MaxPlausible(_maxDownBytes));
        var txResult = _tx.Update(tx, now, MaxPlausible(_maxUpBytes));
        if (rxResult.Outcome == CounterOutcome.Saturated || txResult.Outcome == CounterOutcome.Saturated)
        {
            _countersOk = false;
            Warning = "The router reports a saturated byte counter (4294967295): it does not implement traffic counters.";
            return;
        }

        var host = Host;
        if (rxResult.BytesPerSecond is { } down)
        {
            Num(Info("wan/rx", "Internet download", SensorCategory.Network, Units.BytesPerSecond, "Internet", host, SensorKinds.Throughput, 0, icon: "download-network"), down);
            if (_maxDownBytes is > 0)
            {
                Num(Info("wan/rx-percent", "Internet download (% of link)", SensorCategory.Network, Units.Percent, "Internet", host, SensorKinds.Percent, 0, 100, "download-network"), Math.Min(100, 100 * down / _maxDownBytes.Value));
            }
        }

        if (txResult.BytesPerSecond is { } up)
        {
            Num(Info("wan/tx", "Internet upload", SensorCategory.Network, Units.BytesPerSecond, "Internet", host, SensorKinds.Throughput, 0, icon: "upload-network"), up);
            if (_maxUpBytes is > 0)
            {
                Num(Info("wan/tx-percent", "Internet upload (% of link)", SensorCategory.Network, Units.Percent, "Internet", host, SensorKinds.Percent, 0, 100, "upload-network"), Math.Min(100, 100 * up / _maxUpBytes.Value));
            }
        }
    }

    private static double MaxPlausible(double? linkBytes) => linkBytes is > 0 ? Math.Max(linkBytes.Value * 2, 12.5e6) : 1.25e9;

    private async Task PollLinkAsync(CancellationToken cancellationToken)
    {
        var host = Host;
        var common = _device!.CommonInterface!;
        try
        {
            var link = await CallAsync(common, "GetCommonLinkProperties", cancellationToken).ConfigureAwait(false);
            var status = link.GetValueOrDefault("NewPhysicalLinkStatus");
            Num(Info("wan/link-up", "Internet link up", SensorCategory.Network, Units.None, "Internet", host, SensorKinds.State, 0, 1, "wifi"), status is null ? null : status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            _maxDownBytes = BitRate(link, "NewLayer1DownstreamMaxBitRate");
            _maxUpBytes = BitRate(link, "NewLayer1UpstreamMaxBitRate");
            Num(Info("wan/max-down", "Link speed down", SensorCategory.Network, Units.BytesPerSecond, "Internet", host, SensorKinds.Throughput, 0), _maxDownBytes);
            Num(Info("wan/max-up", "Link speed up", SensorCategory.Network, Units.BytesPerSecond, "Internet", host, SensorKinds.Throughput, 0), _maxUpBytes);
        }
        catch (UpnpFaultException)
        {
            // optional action
        }

        if (_connection is null)
        {
            return;
        }

        try
        {
            var info = await CallAsync(_connection, "GetStatusInfo", cancellationToken).ConfigureAwait(false);
            var connection = info.GetValueOrDefault("NewConnectionStatus");
            Num(Info("wan/connected", "Internet connected", SensorCategory.Network, Units.None, "Internet", host, SensorKinds.State, 0, 1, "earth"), connection is null ? null : connection.Equals("Connected", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if (info.TryGetValue("NewUptime", out var uptime) && double.TryParse(uptime, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                Num(Info("wan/uptime", "Internet connection uptime", SensorCategory.Network, Units.Seconds, "Internet", host, SensorKinds.Duration, 0), seconds);
            }
        }
        catch (UpnpFaultException)
        {
        }

        try
        {
            var ip = (await CallAsync(_connection, "GetExternalIPAddress", cancellationToken).ConfigureAwait(false)).GetValueOrDefault("NewExternalIPAddress");
            Txt(Info("wan/external-ip", "External IP address", SensorCategory.Network, Units.None, "Internet", host, SensorKinds.Text, icon: "ip-network", text: true), string.IsNullOrEmpty(ip) ? null : ip);
        }
        catch (UpnpFaultException)
        {
        }

        Txt(Info("wan/device", "Router", SensorCategory.Network, Units.None, "Internet", host, SensorKinds.Text, icon: "router-network", text: true), _device.Title);
    }

    private static double? BitRate(Dictionary<string, string> reply, string key) =>
        TryCounter(reply, key, out var bits) && bits is > 0 and not WanCounter.Saturated32 ? bits / 8d : null;

    // ----- Google Nest Wifi -----

    private async Task<NestStatus?> ProbeNestAsync(CancellationToken cancellationToken)
    {
        IEnumerable<Uri> candidates;
        if (_nestConfigured is not null)
        {
            candidates = [_nestConfigured];
        }
        else if (!_manual && !_nestDefaultsTried)
        {
            _nestDefaultsTried = true;
            candidates = DefaultNestAddresses.Select(a => new Uri(a));
        }
        else
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var status = await FetchNestAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (status is not null)
                {
                    _nestBase = candidate;
                    _nestFailures = 0;
                    PublishNest(status);
                    return status;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or SourceUnavailableException or System.Text.Json.JsonException)
            {
            }
        }

        return null;
    }

    private async Task<NestStatus?> FetchNestAsync(Uri nestBase, CancellationToken cancellationToken)
    {
        var root = await NetHttp.GetJsonAsync(_client, NetHttp.Combine(nestBase, "api/v1/status"), TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
        return NestStatus.Parse(root);
    }

    private async Task PollNestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await FetchNestAsync(_nestBase!, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                throw new InvalidDataException("not a Nest Wifi answer");
            }

            _nestFailures = 0;
            PublishNest(status);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or SourceUnavailableException or System.Text.Json.JsonException)
        {
            Warning ??= $"Nest Wifi status is not reachable ({ex.Message}).";
            if (++_nestFailures >= 3)
            {
                foreach (var id in new[] { "nest/online", "nest/uptime", "nest/link", "nest/update-available" })
                {
                    SetNone(id);
                }
            }
        }
    }

    private void PublishNest(NestStatus status)
    {
        const string Host = "Google Nest Wifi";
        Num(Info("nest/online", "Nest Wifi internet online", SensorCategory.Network, Units.None, "Nest Wifi", Host, SensorKinds.State, 0, 1, "earth"), status.WanOnline is { } online ? (online ? 1 : 0) : null);
        Num(Info("nest/link", "Nest Wifi WAN cable", SensorCategory.Network, Units.None, "Nest Wifi", Host, SensorKinds.State, 0, 1, "ethernet"), status.EthernetLink is { } link ? (link ? 1 : 0) : null);
        Num(Info("nest/uptime", "Nest Wifi uptime", SensorCategory.Network, Units.Seconds, "Nest Wifi", Host, SensorKinds.Duration, 0, icon: "router-wireless"), status.UptimeSeconds);
        Num(Info("nest/update-available", "Nest Wifi update available", SensorCategory.Network, Units.None, "Nest Wifi", Host, SensorKinds.State, 0, 1, "update"), status.UpdateAvailable ? 1 : 0);
    }
}
