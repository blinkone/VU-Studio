using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Router;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Network;

public class UpnpParsingTests
{
    [Fact]
    public void Ssdp_replies_are_parsed_and_other_datagrams_ignored()
    {
        var reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nUSN: uuid:abc::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nEXT:\r\nSERVER: Linux/4.9 UPnP/1.1 MiniUPnPd/2.2.1\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";
        var parsed = SsdpResponse.Parse(reply)!;
        Assert.Equal("http://192.168.1.1:5000/rootDesc.xml", parsed.Location.ToString());
        Assert.Contains("InternetGatewayDevice", parsed.SearchTarget);
        Assert.Contains("MiniUPnPd", parsed.Server);

        Assert.NotNull(SsdpResponse.Parse(reply.Replace("LOCATION", "location")));
        Assert.Null(SsdpResponse.Parse("NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nLOCATION: http://x/\r\n\r\n"));
        Assert.Null(SsdpResponse.Parse("HTTP/1.1 200 OK\r\nST: x\r\n\r\n"));
        Assert.Null(SsdpResponse.Parse("HTTP/1.1 200 OK\r\nLOCATION: not a url\r\n\r\n"));
    }

    [Fact]
    public void Search_request_has_the_required_headers()
    {
        var search = SsdpResponse.BuildSearch("upnp:rootdevice");
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", search);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", search);
        Assert.Contains("ST: upnp:rootdevice\r\n", search);
        Assert.EndsWith("\r\n\r\n", search);
    }

    internal const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>
         <device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><friendlyName>ISP router</friendlyName><manufacturer>Sagemcom</manufacturer><modelName>F@ST 5359</modelName>
          <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
           <serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId>
             <SCPDURL>/WANCfg.xml</SCPDURL><controlURL>/ctl/CmnIfCfg</controlURL><eventSubURL>/evt/CmnIfCfg</eventSubURL></service></serviceList>
           <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
             <serviceList><service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><serviceId>urn:upnp-org:serviceId:WANPPPConn1</serviceId>
               <SCPDURL>/WANPPPCn.xml</SCPDURL><controlURL>/ctl/PPPConn</controlURL><eventSubURL>/evt/PPPConn</eventSubURL></service></serviceList>
           </device></deviceList></device></deviceList></device></root>
        """;

    [Fact]
    public void Description_services_are_found_by_prefix_and_urls_resolved()
    {
        var device = UpnpDescription.Parse(Description, new Uri("http://192.168.1.1:5000/rootDesc.xml"));
        Assert.True(device.IsGateway);
        Assert.Equal("Sagemcom F@ST 5359", device.Title);
        Assert.Equal("http://192.168.1.1:5000/ctl/CmnIfCfg", device.CommonInterface!.ControlUrl.ToString());
        var ppp = Assert.Single(device.Connections);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", ppp.ServiceType);
        Assert.Equal("http://192.168.1.1:5000/ctl/PPPConn", ppp.ControlUrl.ToString());
    }

    [Fact]
    public void A_url_base_in_the_description_wins_over_the_location()
    {
        var xml = Description.Replace("<specVersion>", "<URLBase>http://10.0.0.1:49000/</URLBase><specVersion>");
        Assert.Equal("http://10.0.0.1:49000/ctl/CmnIfCfg", UpnpDescription.Parse(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml")).CommonInterface!.ControlUrl.ToString());
    }

    [Fact]
    public void Soap_envelope_matches_miniupnpc_framing()
    {
        var body = UpnpSoap.BuildEnvelope("urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1", "GetTotalBytesReceived");
        Assert.Equal(
            "<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:GetTotalBytesReceived xmlns:u=\"urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1\"></u:GetTotalBytesReceived></s:Body></s:Envelope>\r\n",
            body);
        Assert.Equal(296, body.Length);
    }

    [Fact]
    public void Soap_responses_and_faults_are_parsed_whatever_the_prefixes()
    {
        var ok = UpnpSoap.ParseResponse("""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:GetTotalBytesReceivedResponse xmlns:u="urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1"><NewTotalBytesReceived>4123456789</NewTotalBytesReceived></u:GetTotalBytesReceivedResponse></s:Body></s:Envelope>""");
        Assert.Equal("4123456789", ok["NewTotalBytesReceived"]);
        var other = UpnpSoap.ParseResponse("""<SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body><m:GetStatusInfoResponse xmlns:m="x"><NewConnectionStatus>Connected</NewConnectionStatus><NewUptime>86400</NewUptime></m:GetStatusInfoResponse></SOAP-ENV:Body></SOAP-ENV:Envelope>""");
        Assert.Equal("Connected", other["NewConnectionStatus"]);

        var fault = UpnpSoap.ParseFault("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>401</errorCode><errorDescription>Invalid Action</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""");
        Assert.Equal(401, fault.Code);
        Assert.True(fault.IsUnsupported);
    }

    [Fact]
    public void Xml_with_a_doctype_is_refused()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => UpnpSoap.ParseResponse("""<!DOCTYPE x [<!ENTITY a "b">]><r/>"""));
    }

    [Fact]
    public void Nest_status_is_parsed()
    {
        var status = NestStatus.Parse(JsonNode.Parse("""
            {"software":{"updateStatus":"idle","updateNewVersion":"0.0.0.0"},"system":{"uptime":3600},
             "wan":{"online":true,"ethernetLink":true,"localIpAddress":"192.168.1.23","gatewayIpAddress":"192.168.1.1"}}
            """))!;
        Assert.True(status.WanOnline);
        Assert.Equal(3600, status.UptimeSeconds);
        Assert.Equal("192.168.1.1", status.GatewayAddress);
        Assert.False(status.UpdateAvailable);
        Assert.Null(NestStatus.Parse(JsonNode.Parse("""{"hello":1}""")));
    }
}

public class WanCounterTests
{
    private static TimeSpan T(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void First_sample_is_a_baseline_then_rates_follow()
    {
        var c = new WanCounter();
        Assert.Equal(CounterOutcome.Baseline, c.Update(1000, T(0)).Outcome);
        var (outcome, rate) = c.Update(5000, T(2));
        Assert.Equal(CounterOutcome.Rate, outcome);
        Assert.Equal(2000, rate);
    }

    [Fact]
    public void A_32_bit_counter_wraps_around()
    {
        var c = new WanCounter();
        c.Update(4_294_000_000, T(0));
        var (outcome, rate) = c.Update(1_000_000, T(2));
        Assert.Equal(CounterOutcome.Rate, outcome);
        Assert.Equal((1_000_000d + 4294967296d - 4_294_000_000d) / 2, rate);
    }

    [Fact]
    public void A_64_bit_counter_beyond_32_bits_is_not_treated_as_wrapping()
    {
        var c = new WanCounter();
        c.Update(10_000_000_000, T(0));
        Assert.Equal(12_500, c.Update(10_000_025_000, T(2)).BytesPerSecond);
        Assert.Equal(CounterOutcome.Reset, c.Update(100, T(4)).Outcome);
    }

    [Fact]
    public void A_router_that_saturates_at_ffffffff_is_detected()
    {
        var c = new WanCounter();
        Assert.Equal(CounterOutcome.Saturated, c.Update(0xFFFFFFFF, T(0)).Outcome);
        Assert.Equal(CounterOutcome.Saturated, c.Update(0xFFFFFFFF, T(2)).Outcome);
    }

    [Fact]
    public void An_implausible_wrap_is_a_reboot()
    {
        var c = new WanCounter();
        c.Update(3_000_000_000, T(0));
        // 2.5 GB "in one second" cannot be a wrap on a gigabit link.
        Assert.Equal(CounterOutcome.Reset, c.Update(1_000, T(1), maxPlausibleBytesPerSecond: 125_000_000).Outcome);
        Assert.Equal(CounterOutcome.Rate, c.Update(2_000, T(2)).Outcome);
    }

    [Fact]
    public void Equal_counters_give_zero_and_time_going_backwards_gives_nothing()
    {
        var c = new WanCounter();
        c.Update(500, T(0));
        Assert.Equal(0, c.Update(500, T(2)).BytesPerSecond);
        Assert.Equal(CounterOutcome.Baseline, c.Update(600, T(2)).Outcome);
    }
}

public class UpnpWanProviderTests
{
    private sealed class FakeTransport : IUpnpTransport
    {
        public List<SsdpResponse> Multicast { get; } = [];

        public Dictionary<string, List<SsdpResponse>> Unicast { get; } = [];

        public IPAddress? Gateway { get; set; }

        public IPAddress? Hop2 { get; set; }

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<SsdpResponse>> MulticastSearchAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("multicast");
            return Task.FromResult<IReadOnlyList<SsdpResponse>>(Multicast);
        }

        public Task<IReadOnlyList<SsdpResponse>> UnicastSearchAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("unicast " + address);
            return Task.FromResult<IReadOnlyList<SsdpResponse>>(Unicast.GetValueOrDefault(address.ToString()) ?? []);
        }

        public IPAddress? DefaultGateway() => Gateway;

        public Task<IPAddress?> SecondHopAsync(CancellationToken cancellationToken) => Task.FromResult(Hop2);
    }

    private static SsdpResponse Igd(string ip, int port = 5000) => new(new Uri($"http://{ip}:{port}/rootDesc.xml"), "urn:schemas-upnp-org:device:InternetGatewayDevice:1", null, null);

    private static string Soap(string service, string action, params (string Name, string Value)[] args) =>
        $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:{action}Response xmlns:u="{service}">{string.Concat(args.Select(a => $"<{a.Name}>{a.Value}</{a.Name}>"))}</u:{action}Response></s:Body></s:Envelope>""";

    /// <summary>A fake IGD at <paramref name="ip"/>:5000 whose counters follow <paramref name="rx"/> and <paramref name="tx"/>.</summary>
    private static void AddIgd(FakeHttp http, string ip, Func<string> rx, Func<string> tx)
    {
        const string common = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
        const string ppp = "urn:schemas-upnp-org:service:WANPPPConnection:1";
        http.Route($"{ip}:5000/rootDesc.xml", UpnpParsingTests.Description.Replace("ISP router", "Router " + ip));
        http.Route($"{ip}:5000/ctl/", seen =>
        {
            var action = seen.Headers["SOAPAction"].Trim('"').Split('#')[1];
            var xml = action switch
            {
                "GetTotalBytesReceived" => Soap(common, action, ("NewTotalBytesReceived", rx())),
                "GetTotalBytesSent" => Soap(common, action, ("NewTotalBytesSent", tx())),
                "GetCommonLinkProperties" => Soap(common, action, ("NewWANAccessType", "Ethernet"), ("NewLayer1UpstreamMaxBitRate", "100000000"), ("NewLayer1DownstreamMaxBitRate", "1000000000"), ("NewPhysicalLinkStatus", "Up")),
                "GetStatusInfo" => Soap(ppp, action, ("NewConnectionStatus", "Connected"), ("NewLastConnectionError", "ERROR_NONE"), ("NewUptime", "86400")),
                "GetExternalIPAddress" => Soap(ppp, action, ("NewExternalIPAddress", "100.64.12.34")),
                _ => "<error/>",
            };
            return new FakeHttp.FakeReply(HttpStatusCode.OK, xml);
        });
    }

    private static Harness Make(FakeHttp http, FakeTransport transport, string settings = "{}") =>
        new(time =>
        {
            var factory = new UpnpWanProviderFactory();
            var s = new ProviderSettings(TestConfig.For("upnp-wan", settings).Settings, factory.Descriptor);
            return new UpnpWanProvider("p", s.GetString("mode") == "manual", s.GetString("url"), VuStudio.Sources.Remote.NetHttp.NormalizeBase(s.GetString("nestUrl")), null, TimeSpan.FromSeconds(2), Net.Client(http), transport, time, NullLogger.Instance);
        });

    [Fact]
    public async Task Finds_the_isp_router_behind_a_nest_by_unicast_and_publishes_rates_link_and_nest_status()
    {
        long rx = 1_000_000, tx = 500_000;
        var http = new FakeHttp();
        // The Nest: counters saturated, and it names the ISP router as its gateway.
        AddIgd(http, "192.168.86.1", () => "4294967295", () => "4294967295");
        http.Route("192.168.86.1/api/v1/status", """{"software":{"updateNewVersion":"0.0.0.0"},"system":{"uptime":7200},"wan":{"online":true,"ethernetLink":true,"gatewayIpAddress":"192.168.1.1"}}""");
        AddIgd(http, "192.168.1.1", () => rx.ToString(), () => tx.ToString());
        var transport = new FakeTransport { Gateway = IPAddress.Parse("192.168.86.1") };
        transport.Multicast.Add(Igd("192.168.86.1"));
        transport.Unicast["192.168.1.1"] = [Igd("192.168.1.1")];
        var h = Make(http, transport);

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Contains("unicast 192.168.1.1", transport.Calls);
        Assert.Equal(1, h.Number("nest/online"));
        Assert.Equal(7200, h.Number("nest/uptime"));
        Assert.Equal(1, h.Number("wan/link-up"));
        Assert.Equal(1, h.Number("wan/connected"));
        Assert.Equal(125_000_000, h.Number("wan/max-down"));
        Assert.Equal(12_500_000, h.Number("wan/max-up"));
        Assert.Equal("100.64.12.34", h.Text("wan/external-ip"));
        Assert.Equal(86400, h.Number("wan/uptime"));
        Assert.Equal("Sagemcom F@ST 5359", h.Text("wan/device"));
        Assert.Null(h.Number("wan/rx")); // baseline only

        rx += 250_000;
        tx += 50_000;
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.StepAsync();
        Assert.Equal(125_000, h.Number("wan/rx"));
        Assert.Equal(25_000, h.Number("wan/tx"));
        Assert.Equal(0.1, h.Number("wan/rx-percent")!.Value, 6);

        rx = 100; // router rebooted: no absurd spike
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.StepAsync();
        Assert.Equal(125_000, h.Number("wan/rx")); // unchanged: the reset sample is skipped
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task A_saturated_only_router_is_used_for_link_info_with_a_warning_and_no_rates()
    {
        var http = new FakeHttp();
        AddIgd(http, "192.168.86.1", () => "4294967295", () => "4294967295");
        var transport = new FakeTransport();
        transport.Multicast.Add(Igd("192.168.86.1"));
        var h = Make(http, transport);

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("saturated", h.Message);
        Assert.False(h.Has("wan/rx"));
        Assert.Equal(1, h.Number("wan/link-up"));
    }

    [Fact]
    public async Task No_router_gives_a_helpful_message_and_discovery_is_not_repeated_every_poll()
    {
        var transport = new FakeTransport();
        var h = Make(new FakeHttp(), transport);

        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("UPnP", h.Message);
        var multicasts = transport.Calls.Count(c => c == "multicast");

        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.StepAsync();
        Assert.Equal(multicasts, transport.Calls.Count(c => c == "multicast"));

        h.Time.Advance(TimeSpan.FromSeconds(40));
        await h.StepAsync();
        Assert.Equal(multicasts + 1, transport.Calls.Count(c => c == "multicast"));
    }

    [Fact]
    public async Task Second_hop_is_tried_when_nothing_else_answers()
    {
        long rx = 10, tx = 10;
        var http = new FakeHttp();
        AddIgd(http, "192.168.1.1", () => rx.ToString(), () => tx.ToString());
        var transport = new FakeTransport { Hop2 = IPAddress.Parse("192.168.1.1") };
        var h = Make(http, transport);
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Contains("unicast 192.168.1.1", transport.Calls);
    }

    [Fact]
    public async Task Manual_mode_uses_the_description_url_directly()
    {
        long rx = 0, tx = 0;
        var http = new FakeHttp();
        AddIgd(http, "10.0.0.1", () => rx.ToString(), () => tx.ToString());
        var transport = new FakeTransport();
        var h = Make(http, transport, """{"mode":"manual","url":"http://10.0.0.1:5000/rootDesc.xml"}""");

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.DoesNotContain("multicast", transport.Calls);
        rx = 4_294_967_000;
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.StepAsync();
        rx = 1_000; // 32-bit wrap
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.StepAsync();
        Assert.Equal((1_000d + 4294967296d - 4_294_967_000d) / 2, h.Number("wan/rx"));
    }

    [Fact]
    public async Task Manual_mode_without_an_address_is_a_setup_problem()
    {
        var h = Make(new FakeHttp(), new FakeTransport(), """{"mode":"manual"}""");
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("address", h.Message);
    }
}
