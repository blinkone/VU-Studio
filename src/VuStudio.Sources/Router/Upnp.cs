using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using VuStudio.Sources.Remote;

namespace VuStudio.Sources.Router;

/// <summary>An answer to an SSDP <c>M-SEARCH</c>.</summary>
internal sealed record SsdpResponse(Uri Location, string? SearchTarget, string? Usn, string? Server)
{
    /// <summary>Parses the HTTP-over-UDP reply; returns <c>null</c> when it has no usable <c>LOCATION</c>.</summary>
    public static SsdpResponse? Parse(string datagram)
    {
        string? location = null, st = null, usn = null, server = null;
        using var reader = new StringReader(datagram);
        var first = reader.ReadLine();
        if (first is null || !first.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || !first.Contains(" 200", StringComparison.Ordinal))
        {
            return null; // NOTIFY or M-SEARCH from another control point, not a reply
        }

        while (reader.ReadLine() is { } line)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (name.ToUpperInvariant())
            {
                case "LOCATION": location = value; break;
                case "ST": st = value; break;
                case "USN": usn = value; break;
                case "SERVER": server = value; break;
            }
        }

        return Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? new SsdpResponse(uri, st, usn, server) : null;
    }

    public static string BuildSearch(string searchTarget, int maxWaitSeconds = 2) =>
        $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: {searchTarget}\r\nMAN: \"ssdp:discover\"\r\nMX: {maxWaitSeconds.ToString(CultureInfo.InvariantCulture)}\r\n\r\n";

    public static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "upnp:rootdevice",
    ];
}

internal sealed record UpnpService(string ServiceType, Uri ControlUrl);

/// <summary>What the device description says about an Internet Gateway Device.</summary>
internal sealed record UpnpDevice(Uri Location, string? FriendlyName, string? Manufacturer, string? ModelName, UpnpService? CommonInterface, IReadOnlyList<UpnpService> Connections)
{
    public bool IsGateway => CommonInterface is not null;

    public string Title => string.Join(' ', new[] { Manufacturer, ModelName ?? FriendlyName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } t ? t : Location.Host;
}

internal static class UpnpDescription
{
    public const string CommonInterfacePrefix = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:";

    private static readonly string[] ConnectionPrefixes = ["urn:schemas-upnp-org:service:WANIPConnection:", "urn:schemas-upnp-org:service:WANPPPConnection:"];

    /// <summary>Parses a UPnP device description. Services are found by prefix match of the service type (versions 1 and 2) anywhere in the device tree.</summary>
    public static UpnpDevice Parse(string xml, Uri location)
    {
        var document = LoadXml(xml);
        var root = document.Root ?? throw new InvalidDataException("Empty device description.");
        var baseUri = location;
        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim() is { Length: > 0 } urlBase && Uri.TryCreate(urlBase, UriKind.Absolute, out var parsedBase))
        {
            baseUri = parsedBase;
        }

        var device = root.Elements().FirstOrDefault(e => e.Name.LocalName == "device");
        string? Text(string name) => device?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        UpnpService? common = null;
        var connections = new List<UpnpService>();
        foreach (var service in root.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim();
            var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();
            if (type is null || control is null || !Uri.TryCreate(baseUri, control, out var controlUrl))
            {
                continue;
            }

            if (type.StartsWith(CommonInterfacePrefix, StringComparison.Ordinal))
            {
                common ??= new UpnpService(type, controlUrl);
            }
            else if (ConnectionPrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal)))
            {
                connections.Add(new UpnpService(type, controlUrl));
            }
        }

        return new UpnpDevice(location, Text("friendlyName"), Text("manufacturer"), Text("modelName"), common, connections);
    }

    internal static XDocument LoadXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\r', '\n', '\t')), settings);
        return XDocument.Load(reader);
    }
}

/// <summary>A UPnP SOAP fault (HTTP 500 with a <c>UPnPError</c>).</summary>
internal sealed class UpnpFaultException(int code, string? description) : Exception($"UPnP error {code}: {description}")
{
    public int Code { get; } = code;

    /// <summary>The action is not implemented or not allowed: do not ask again.</summary>
    public bool IsUnsupported => Code is 401 or 606 or 501 or 402;
}

internal static class UpnpSoap
{
    /// <summary>The request body exactly as miniupnpc sends it (some routers are picky).</summary>
    public static string BuildEnvelope(string serviceType, string action) =>
        "<?xml version=\"1.0\"?>\r\n" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        $"<s:Body><u:{action} xmlns:u=\"{serviceType}\"></u:{action}></s:Body></s:Envelope>\r\n";

    /// <summary>Reads the out arguments of a response (namespace-agnostic, routers vary their prefixes).</summary>
    public static Dictionary<string, string> ParseResponse(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = UpnpDescription.LoadXml(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (body?.Elements().FirstOrDefault() is { } response)
        {
            if (response.Name.LocalName == "Fault")
            {
                throw ParseFault(xml);
            }

            foreach (var argument in response.Elements())
            {
                result[argument.Name.LocalName] = argument.Value.Trim();
            }
        }

        return result;
    }

    public static UpnpFaultException ParseFault(string xml)
    {
        try
        {
            var error = UpnpDescription.LoadXml(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            var code = error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var description = error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            return new UpnpFaultException(int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 500, description);
        }
        catch (XmlException)
        {
            return new UpnpFaultException(500, "unreadable SOAP fault");
        }
    }

    /// <summary>Calls a read-only action. Write actions (port mappings…) are never issued by VU Studio.</summary>
    public static async Task<Dictionary<string, string>> CallAsync(HttpClient client, UpnpService service, string action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl);
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + service.ServiceType + "#" + action + "\"");
        request.Headers.ConnectionClose = true;
        request.Content = new StringContent(BuildEnvelope(service.ServiceType, action), new UTF8Encoding(false), "text/xml");
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");

        var response = await NetHttp.SendAsync(client, request, timeout, cancellationToken, maxBytes: 1_000_000).ConfigureAwait(false);
        var text = response.Text;
        if (response.IsSuccess)
        {
            return ParseResponse(text);
        }

        if (response.Status == HttpStatusCode.InternalServerError && text.Contains("UPnPError", StringComparison.Ordinal))
        {
            throw ParseFault(text);
        }

        throw new IOException($"{service.ControlUrl.Authority} answered HTTP {(int)response.Status} to {action}.");
    }
}

internal enum CounterOutcome
{
    /// <summary>First sample: remembered, no rate yet.</summary>
    Baseline,

    /// <summary>A rate is available.</summary>
    Rate,

    /// <summary>The device reports 4294967295 (Nest Wifi): the counter is not implemented.</summary>
    Saturated,

    /// <summary>The counter went backwards implausibly (device reboot): the new value is the baseline.</summary>
    Reset,
}

/// <summary>
/// Turns a WAN byte counter (32-bit by specification, 64-bit on many miniupnpd builds) into bytes per second:
/// 32-bit wraparound, saturation at <c>0xFFFFFFFF</c> and device resets.
/// </summary>
internal sealed class WanCounter
{
    public const ulong Saturated32 = 0xFFFFFFFF;
    private const double Wrap32 = 4294967296d;

    private ulong? _previous;
    private TimeSpan _previousTime;

    /// <param name="value">The counter as read.</param>
    /// <param name="now">Monotonic time of the reading.</param>
    /// <param name="maxPlausibleBytesPerSecond">A rate above this after a "wrap" is taken for a reset instead.</param>
    public (CounterOutcome Outcome, double? BytesPerSecond) Update(ulong value, TimeSpan now, double maxPlausibleBytesPerSecond = 1.25e9)
    {
        if (value == Saturated32)
        {
            return (CounterOutcome.Saturated, null);
        }

        if (_previous is not { } previous)
        {
            _previous = value;
            _previousTime = now;
            return (CounterOutcome.Baseline, null);
        }

        var seconds = (now - _previousTime).TotalSeconds;
        if (seconds <= 0)
        {
            return (CounterOutcome.Baseline, null);
        }

        _previous = value;
        _previousTime = now;
        double delta;
        if (value >= previous)
        {
            delta = value - previous;
        }
        else if (previous <= Saturated32)
        {
            delta = value + Wrap32 - previous; // 32-bit wrap
        }
        else
        {
            return (CounterOutcome.Reset, null); // a 64-bit counter went backwards
        }

        var rate = delta / seconds;
        if (value < previous && rate > maxPlausibleBytesPerSecond)
        {
            return (CounterOutcome.Reset, null); // "wrapped" faster than the link can go: the router rebooted
        }

        return (CounterOutcome.Rate, rate);
    }

    public void Reset() => _previous = null;
}

/// <summary>What the Google Nest Wifi / Google Wifi router reports at <c>/api/v1/status</c>.</summary>
internal sealed record NestStatus(bool? WanOnline, bool? EthernetLink, double? UptimeSeconds, string? GatewayAddress, string? LocalAddress, string? UpdateStatus, bool UpdateAvailable)
{
    public static NestStatus? Parse(System.Text.Json.Nodes.JsonNode? root)
    {
        var wan = JsonRead.Get(root, "wan");
        if (wan is null && JsonRead.Get(root, "system") is null)
        {
            return null; // not a Nest/Google Wifi answer
        }

        var newVersion = JsonRead.Str(JsonRead.Get(root, "software"), "updateNewVersion");
        var status = JsonRead.Str(JsonRead.Get(root, "software"), "updateStatus");
        return new NestStatus(
            JsonRead.Bool(wan, "online"),
            JsonRead.Bool(wan, "ethernetLink"),
            JsonRead.Num(JsonRead.Get(root, "system"), "uptime"),
            JsonRead.Str(wan, "gatewayIpAddress"),
            JsonRead.Str(wan, "localIpAddress"),
            status,
            newVersion is { Length: > 0 } && newVersion != "0.0.0.0");
    }
}
