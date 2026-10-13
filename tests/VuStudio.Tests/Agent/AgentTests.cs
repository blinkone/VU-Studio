using System.Text.Json;
using System.Text.Json.Nodes;
using Makaretu.Dns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Agent;
using VuStudio.Agent.Unraid;
using VuStudio.Core.Config;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using VuStudio.Sources.SystemMetrics;
using VuStudio.Sources.SystemMetrics.Linux;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Agent;

public class AgentApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static SensorHub HubWithSensors()
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("system");
        sink.Define(new SensorInfo { Id = "cpu/load", Name = "CPU total", Device = "CPU", Category = SensorCategory.Cpu, Kind = SensorKinds.Load, Unit = Units.Percent, Min = 0, Max = 100, Icon = "cpu-64-bit" });
        sink.Define(new SensorInfo { Id = "os", Name = "OS", Category = SensorCategory.System, ValueKind = SensorValueKind.Text });
        sink.Define(new SensorInfo { Id = "disk/root/temp", Name = "Temp", Category = SensorCategory.Storage, Kind = SensorKinds.Temperature, Unit = Units.Celsius });
        sink.Publish("cpu/load", 42.5);
        sink.PublishText("os", "Steam OS");
        return hub;
    }

    [Fact]
    public void Sensors_response_has_the_documented_shape()
    {
        var response = AgentApi.BuildSensors(HubWithSensors(), "deck", Now);
        var json = JsonNode.Parse(JsonSerializer.Serialize(response, Json.Compact))!.AsObject();

        Assert.Equal("deck", (string?)json["name"]);
        Assert.NotNull((string?)json["version"]);
        Assert.Equal(Now, json["time"]!.GetValue<DateTimeOffset>());
        Assert.False(string.IsNullOrEmpty((string?)json["catalogVersion"]));
        Assert.Equal(3, json["sensors"]!.AsArray().Count);
        var values = json["values"]!.AsObject();
        Assert.Equal(42.5, (double?)values["system/cpu/load"]);
        Assert.Equal("Steam OS", (string?)values["system/os"]);
        Assert.True(values.ContainsKey("system/disk/root/temp"));
        Assert.Null(values["system/disk/root/temp"]);
    }

    [Fact]
    public void Response_round_trips_through_the_agent_client_parser()
    {
        var response = AgentApi.BuildSensors(HubWithSensors(), "deck", Now);
        var node = JsonNode.Parse(JsonSerializer.Serialize(response, Json.Compact));

        var snapshot = AgentSnapshot.Parse(node, host: null);

        Assert.Equal("deck", snapshot.Name);
        Assert.Equal(response.CatalogVersion, snapshot.CatalogVersion);
        Assert.Equal(["system/cpu/load", "system/disk/root/temp", "system/os"], snapshot.Sensors.Select(s => s.RemoteId));
        var cpu = snapshot.Sensors.Single(s => s.RemoteId == "system/cpu/load").Info;
        Assert.Equal(SensorCategory.Cpu, cpu.Category);
        Assert.Equal(SensorKinds.Load, cpu.Kind);
        Assert.Equal(Units.Percent, cpu.Unit);
        Assert.Equal(100, cpu.Max);
        Assert.Equal("cpu-64-bit", cpu.Icon);
        Assert.Equal(SensorValueKind.Text, snapshot.Sensors.Single(s => s.RemoteId == "system/os").Info.ValueKind);
        Assert.Equal(42.5, snapshot.Values["system/cpu/load"]!.GetValue<double>());
    }

    [Fact]
    public void Stale_sensors_have_null_values()
    {
        var time = new ManualTimeProvider(Now);
        var hub = new SensorHub(time);
        var sink = hub.CreateSink("system");
        sink.Define(new SensorInfo { Id = "x", Name = "X", StaleAfterSeconds = 5 });
        sink.Publish("x", 1);
        Assert.Equal(1.0, AgentApi.BuildSensors(hub, "n", Now).Values["system/x"]);

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(AgentApi.BuildSensors(hub, "n", Now).Values["system/x"]);
    }

    [Fact]
    public void Info_response_has_the_documented_fields()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(AgentApi.BuildInfo("deck"), Json.Compact))!.AsObject();

        Assert.Equal(["name", "version", "os", "hostname", "uptimeSeconds"], json.Select(p => p.Key));
        Assert.Equal("deck", (string?)json["name"]);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "anything", true)]
    [InlineData("secret", null, false)]
    [InlineData("secret", "Bearer wrong", false)]
    [InlineData("secret", "Basic secret", false)]
    [InlineData("secret", "Bearer secret", true)]
    [InlineData("secret", "bearer secret", true)]
    public void Bearer_token(string? token, string? header, bool expected) =>
        Assert.Equal(expected, AgentApi.IsAuthorized(token, header));
}

public class AgentOptionsTests
{
    private static AgentOptions From(params (string Key, string Value)[] values) =>
        AgentOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void Defaults()
    {
        var o = From();

        Assert.Equal(5342, o.Port);
        Assert.Null(o.Token);
        Assert.True(o.Mdns);
        Assert.Equal("/var/local/emhttp", o.UnraidPath);
        Assert.Equal(Environment.MachineName, o.Name);
    }

    [Fact]
    public void Reads_values_and_clamps()
    {
        var o = From(("port", "8741"), ("token", " abc "), ("name", "Tower"), ("mdns", "off"), ("interval", "0.01"), ("PER_CORE", "false"));

        Assert.Equal(8741, o.Port);
        Assert.Equal("abc", o.Token);
        Assert.Equal("Tower", o.Name);
        Assert.False(o.Mdns);
        Assert.Equal(0.25, o.IntervalSeconds);
        Assert.False(o.PerCore);
        Assert.Equal(65535, From(("port", "99999999")).Port);
    }

    [Fact]
    public void Token_can_come_from_a_file()
    {
        using var dir = new TempDirectory("agent-token");
        var path = dir.Write("token", "from-file\n");

        Assert.Equal("from-file", From(("token-file", path)).Token);
        Assert.Null(From(("token-file", dir.Combine("missing"))).Token);
    }

    [Fact]
    public void Providers_are_system_plus_unraid_when_detected()
    {
        var options = new AgentOptions { IntervalSeconds = 2, PerCore = false };

        Assert.Equal(["system"], SensorHost.BuildConfigs(options, false).Select(c => c.Id));
        var withUnraid = SensorHost.BuildConfigs(options, true);
        Assert.Equal(["system", "unraid"], withUnraid.Select(c => c.Id));
        Assert.False((bool)withUnraid[0].Settings["perCore"]!);
    }
}

public class AgentMdnsTests
{
    [Fact]
    public void Profile_matches_what_the_client_browses_for()
    {
        var profile = MdnsAdvertiser.CreateProfile(new AgentOptions { Name = "Steam Deck", Port = 5342 });

        Assert.Equal(AgentRecordCollector.ServiceType, MdnsAdvertiser.ServiceType);
        Assert.StartsWith("Steam", profile.InstanceName.ToString());
        var srv = profile.Resources.OfType<SRVRecord>().Single();
        Assert.Equal(5342, srv.Port);
        var txt = AgentRecordCollector.ParseTxt(profile.Resources.OfType<TXTRecord>().SelectMany(t => t.Strings));
        Assert.Equal("agent", txt["role"]);
        Assert.Equal("1", txt["v"]);
        Assert.Equal("Steam Deck", txt["name"]);
    }

    [Fact]
    public void Instance_label_is_a_valid_dns_label()
    {
        Assert.Equal("my-pc-lan", MdnsAdvertiser.InstanceLabel("my.pc.lan"));
        Assert.Equal("vustudio-agent", MdnsAdvertiser.InstanceLabel("  "));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(MdnsAdvertiser.InstanceLabel(new string('é', 80))) <= 63);
    }
}

public class AgentLinuxFixtureTests
{
    [Fact]
    public void Fixture_proc_and_sys_become_agent_sensors_with_canonical_units()
    {
        using var fixture = new CollectorFixture();
        fixture.Root.Write("proc/stat", "cpu  100 0 100 800 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0\n");
        fixture.Root.Write("proc/cpuinfo", "model name : Test CPU\n");
        fixture.Root.Write("proc/loadavg", "0.50 0.40 0.30 1/100 5\n");
        fixture.Root.Write("proc/uptime", "3600.5 100.0\n");
        fixture.Root.Write("proc/meminfo", "MemTotal: 8000 kB\nMemAvailable: 2000 kB\nSwapTotal: 1000 kB\nSwapFree: 500 kB\n");
        fixture.Root.Write("sys/class/hwmon/hwmon0/name", "amdgpu\n");
        fixture.Root.Write("sys/class/hwmon/hwmon0/temp1_input", "61000\n");
        fixture.Root.Write("sys/class/power_supply/BAT1/type", "Battery\n");
        fixture.Root.Write("sys/class/power_supply/BAT1/capacity", "77\n");
        using var collector = new LinuxCollector(fixture.Root.Path, perCore: true);

        fixture.Collect(collector, 1);
        var response = AgentApi.BuildSensors(fixture.Hub, "deck", DateTimeOffset.UtcNow);

        Assert.Equal(6000d * 1024,response.Values["system/mem/used"]);
        Assert.Equal(3600.5, response.Values["system/uptime"]);
        Assert.Equal(61.0, response.Values["system/hwmon/amdgpu/temp1"]);
        Assert.Equal(77.0, response.Values["system/battery/bat1/capacity"]);
        var temp = response.Sensors.Single(s => s.Id == "system/hwmon/amdgpu/temp1");
        Assert.Equal(Units.Celsius, temp.Unit);
        Assert.Equal(SensorKinds.Temperature, temp.Kind);
        Assert.All(response.Sensors, s => Assert.True(LocalIds.IsValid(s.Id), s.Id));
    }
}

public class UnraidProviderTests
{
    private const string VarIni = """
        version="7.1.0"
        NAME="Tower"
        fsState="Started"
        mdState="STARTED"
        mdNumDisabled="0"
        mdNumInvalid="1"
        mdNumMissing="0"
        mdResyncPos="250"
        mdResyncSize="1000"
        mdResyncDt="10"
        mdResyncDb="500"
        sbSyncErrs="3"
        """;

    private const string DisksIni = """
        ["parity"]
        idx="0"
        name="parity"
        device="sdb"
        type="Parity"
        status="DISK_OK"
        temp="31"
        spundown="0"
        numErrors="0"
        ["disk1"]
        idx="1"
        name="disk1"
        device="sdc"
        type="Data"
        status="DISK_OK"
        temp="*"
        spundown="1"
        numErrors="2"
        fsSize="1000"
        fsUsed="250"
        fsFree="750"
        ["disk2"]
        idx="2"
        name="disk2"
        device="sdd"
        type="Data"
        status="DISK_OK"
        temp="38"
        spundown="0"
        numErrors="0"
        fsSize="3000"
        fsUsed="1250"
        fsFree="1750"
        ["disk3"]
        idx="3"
        name="disk3"
        device=""
        type="Data"
        status="DISK_NP"
        ["flash"]
        idx="54"
        name="flash"
        device="sda"
        type="Flash"
        status="DISK_OK"
        temp="*"
        """;

    [Fact]
    public void Ini_parser_handles_quoted_sections_and_values()
    {
        var ini = EmhttpIni.Parse(VarIni + "\n" + DisksIni);

        Assert.Equal("Tower", ini[""]["NAME"]);
        Assert.Equal("sdc", ini["disk1"]["device"]);
        Assert.Equal(1000, ini["disk1"].Number("fsSize"));
        Assert.Null(ini["disk1"].Number("temp"));
        Assert.Empty(EmhttpIni.Parse(null));
    }

    [Fact]
    public async Task Publishes_array_parity_and_disks()
    {
        using var dir = new TempDirectory("emhttp");
        dir.Write("var.ini", VarIni);
        dir.Write("disks.ini", DisksIni);
        Assert.True(UnraidProvider.IsPresent(dir.Path));
        var hub = new SensorHub();
        var sink = hub.CreateSink("unraid");
        await using var provider = new UnraidProvider(dir.Path, TimeSpan.FromSeconds(5), NullLogger.Instance);

        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal("STARTED", hub.GetText("unraid/array/state"));
        Assert.Equal(1, hub.GetNumber("unraid/array/started"));
        Assert.Equal(1, hub.GetNumber("unraid/array/problem-disks"));
        Assert.Equal(4000d * 1024, hub.GetNumber("unraid/array/total"));
        Assert.Equal(1500d * 1024, hub.GetNumber("unraid/array/used"));
        Assert.Equal(37.5, hub.GetNumber("unraid/array/usage"));
        Assert.Equal("running", hub.GetText("unraid/parity/state"));
        Assert.Equal(25, hub.GetNumber("unraid/parity/progress"));
        Assert.Equal(50d * 1024, hub.GetNumber("unraid/parity/speed"));
        Assert.Equal(750d * 1024 / (50d * 1024), hub.GetNumber("unraid/parity/eta"));
        Assert.Equal(3, hub.GetNumber("unraid/parity/errors"));
        Assert.Equal(31, hub.GetNumber("unraid/disk/parity/temp"));
        Assert.Equal(38, hub.GetNumber("unraid/disk/disk2/temp"));
        Assert.Equal(1, hub.GetNumber("unraid/disk/disk1/spun-down"));
        Assert.Equal(2, hub.GetNumber("unraid/disk/disk1/errors"));
        Assert.Equal(25, hub.GetNumber("unraid/disk/disk1/usage"));
        Assert.Null(hub.Get("unraid/disk/disk3/temp"));
        Assert.Equal(SensorKinds.Temperature, hub.Get("unraid/disk/disk2/temp")!.Info.Kind);
    }

    [Fact]
    public async Task Standby_disks_keep_their_last_temperature_and_idle_parity_has_no_eta()
    {
        using var dir = new TempDirectory("emhttp");
        dir.Write("var.ini", VarIni);
        dir.Write("disks.ini", DisksIni.Replace("temp=\"*\"\nspundown=\"1\"", "temp=\"29\"\nspundown=\"0\""));
        var hub = new SensorHub();
        var sink = hub.CreateSink("unraid");
        await using var provider = new UnraidProvider(dir.Path, TimeSpan.FromSeconds(5), NullLogger.Instance);
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(29, hub.GetNumber("unraid/disk/disk1/temp"));

        dir.Write("disks.ini", DisksIni);
        dir.Write("var.ini", VarIni.Replace("mdResyncPos=\"250\"", "mdResyncPos=\"0\""));
        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal(29, hub.GetNumber("unraid/disk/disk1/temp"));
        Assert.Equal(1, hub.GetNumber("unraid/disk/disk1/spun-down"));
        Assert.Equal("idle", hub.GetText("unraid/parity/state"));
        Assert.Null(hub.GetNumber("unraid/parity/eta"));
    }

    [Fact]
    public void Absent_state_files_mean_not_present()
    {
        using var dir = new TempDirectory("emhttp");

        Assert.False(UnraidProvider.IsPresent(dir.Path));
        Assert.False(UnraidProvider.IsPresent(dir.Combine("nope")));
    }
}
