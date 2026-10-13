using System.Net;
using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Remote;

namespace VuStudio.Tests.Network;

public class PrometheusParserTests
{
    [Fact]
    public void Research_test_vectors_parse()
    {
        var scrape = PrometheusText.Parse("""
            # HELP node_cpu_seconds_total Seconds the CPUs spent in each mode.
            # TYPE node_cpu_seconds_total counter
            node_cpu_seconds_total{cpu="0",mode="idle"} 1.234e+06
            msdos_file_access_time_seconds{path="C:\\DIR\\FILE.TXT",error="Cannot find file:\n\"FILE.TXT\""} 1.458255915e9
            something_weird{problem="division by zero"} +Inf -3982045
            x{a="b,}c d",} 5
            empty{} 7
            lhm_cpu_load_percent {"sensorName"="CPU Total", "hostName"="PC-1"} 13.5
            bare_metric 3
            """);

        Assert.Equal(1234000, scrape.First("node_cpu_seconds_total"));
        var msdos = scrape.Named("msdos_file_access_time_seconds").Single();
        Assert.Equal(@"C:\DIR\FILE.TXT", msdos.Label("path"));
        Assert.Equal("Cannot find file:\n\"FILE.TXT\"", msdos.Label("error"));
        Assert.Equal(1.458255915e9, msdos.Value);
        Assert.True(double.IsPositiveInfinity(scrape.Named("something_weird").Single().Value));
        Assert.Equal("b,}c d", scrape.Named("x").Single().Label("a"));
        Assert.Equal(7, scrape.First("empty"));
        var lhm = scrape.Named("lhm_cpu_load_percent").Single();
        Assert.Equal("CPU Total", lhm.Label("sensorName"));
        Assert.Equal(13.5, lhm.Value);
        Assert.Equal(3, scrape.First("bare_metric"));
        Assert.Equal("counter", scrape.Types["node_cpu_seconds_total"]);
        Assert.True(scrape.IsCounter("node_cpu_seconds_total"));
    }

    [Fact]
    public void Nan_values_blank_lines_crlf_and_eof_are_handled()
    {
        var scrape = PrometheusText.Parse("a 1\r\n\r\nb NaN\r\n# TYPE c counter\r\nc_total 4\r\n# EOF\r\nd 9\r\n");
        Assert.Equal(1, scrape.First("a"));
        Assert.Null(scrape.First("b"));
        Assert.Equal(4, scrape.First("c_total"));
        Assert.True(scrape.IsCounter("c_total"));
        Assert.False(scrape.Has("d"));
    }

    [Fact]
    public void Name_filter_skips_unwanted_metrics_early()
    {
        var scrape = PrometheusText.Parse("go_goroutines 9\nnode_load1 0.5\n", name => name.StartsWith("node_"));
        Assert.Single(scrape.Samples);
    }

    [Fact]
    public void Label_matchers_support_equality_negation_and_regex()
    {
        var sample = PrometheusText.Parse("m{cpu=\"3\",mode=\"idle\",device=\"eth0\"} 1").Samples.Single();
        Assert.True(LabelMatcher.Parse("mode=\"idle\"").Matches(sample));
        Assert.True(LabelMatcher.Parse("mode=idle, cpu=~\"[0-3]\"").Matches(sample));
        Assert.False(LabelMatcher.Parse("mode!=\"idle\"").Matches(sample));
        Assert.False(LabelMatcher.Parse("device!~\"eth.*\"").Matches(sample));
        Assert.True(LabelMatcher.Parse("").Matches(sample));
    }

    [Fact]
    public void Counter_store_computes_deltas_ignores_short_windows_and_handles_resets()
    {
        var store = new CounterStore();
        Assert.Null(store.Rate("k", 100, 0));
        Assert.Equal(50, store.Rate("k", 200, 2));
        Assert.Null(store.Rate("k", 210, 2.2)); // window too short: baseline kept
        Assert.Equal(40, store.Rate("k", 280, 4));
        Assert.Equal(5, store.Rate("k", 10, 6)); // reset: counted from zero
    }
}

public class PrometheusProviderTests
{
    private static string NodeExporter(double cpuIdle, double cpuBusy, double rx, double tx, double read) => $$"""
        # TYPE node_cpu_seconds_total counter
        node_cpu_seconds_total{cpu="0",mode="idle"} {{cpuIdle}}
        node_cpu_seconds_total{cpu="0",mode="user"} {{cpuBusy}}
        node_cpu_seconds_total{cpu="1",mode="idle"} {{cpuIdle}}
        node_cpu_seconds_total{cpu="1",mode="iowait"} 0
        node_cpu_seconds_total{cpu="1",mode="system"} {{cpuBusy}}
        node_load1 0.5
        node_load5 0.6
        node_load15 0.7
        node_memory_MemTotal_bytes 8000000000
        node_memory_MemAvailable_bytes 2000000000
        node_memory_SwapTotal_bytes 1000
        node_memory_SwapFree_bytes 750
        node_filesystem_size_bytes{device="/dev/sda1",fstype="ext4",mountpoint="/"} 1000000
        node_filesystem_avail_bytes{device="/dev/sda1",fstype="ext4",mountpoint="/"} 250000
        node_filesystem_size_bytes{device="tmpfs",fstype="tmpfs",mountpoint="/run"} 1000
        node_filesystem_avail_bytes{device="tmpfs",fstype="tmpfs",mountpoint="/run"} 900
        node_network_receive_bytes_total{device="eth0"} {{rx}}
        node_network_transmit_bytes_total{device="eth0"} {{tx}}
        node_network_receive_bytes_total{device="lo"} 5
        node_network_transmit_bytes_total{device="lo"} 5
        node_disk_read_bytes_total{device="sda"} {{read}}
        node_disk_written_bytes_total{device="sda"} 0
        node_disk_io_time_seconds_total{device="sda"} 0
        node_hwmon_chip_names{chip="pci0000:00_0000:00:18_3",chip_name="k10temp"} 1
        node_hwmon_sensor_label{chip="pci0000:00_0000:00:18_3",sensor="temp1",label="Tctl"} 1
        node_hwmon_temp_celsius{chip="pci0000:00_0000:00:18_3",sensor="temp1"} 61.5
        node_hwmon_fan_rpm{chip="platform_nct6775_656",sensor="fan1"} 900
        node_thermal_zone_temp{zone="0",type="cpu-thermal"} 48
        node_power_supply_capacity{power_supply="BAT0"} 80
        node_boot_time_seconds 1000
        node_time_seconds 91000
        node_uname_info{nodename="nas",sysname="Linux"} 1
        """;

    private static Harness Make(FakeHttp http, string extra = "") =>
        Net.Make(new PrometheusProviderFactory(), $$"""{"url":"http://nas:9100"{{extra}}}""", http);

    [Fact]
    public async Task Node_exporter_presets_publish_gauges_at_once_and_rates_from_the_second_scrape()
    {
        var body = NodeExporter(1000, 100, 1_000_000, 500_000, 0);
        var http = new FakeHttp().Route("/metrics", _ => new FakeHttp.FakeReply(HttpStatusCode.OK, body));
        var h = Make(http);

        await h.StepAsync();
        Assert.Equal("http://nas:9100/metrics", http.Requests[0].Uri.ToString());
        Assert.Equal(0.7, h.Number("load/15"));
        Assert.Equal(75, h.Number("memory/percent")!.Value, 6);
        Assert.Equal(25, h.Number("swap/percent"));
        Assert.Equal(75, h.Number("fs/root/percent"));
        Assert.False(h.Has("fs/run/percent"));
        Assert.Equal(61.5, h.Number("hwmon/temp/k10temp/tctl"));
        Assert.Equal(SensorCategory.Cpu, h.Info("hwmon/temp/k10temp/tctl")!.Category);
        Assert.Equal(900, h.Number("hwmon/fan/platform-nct6775-656/fan1"));
        Assert.Equal(48, h.Number("hwmon/temp/thermal/cpu-thermal"));
        Assert.Equal(80, h.Number("battery/bat0"));
        Assert.Equal(90000, h.Number("uptime"));
        Assert.Equal("nas", h.Info("memory/percent")!.Host);
        Assert.Null(h.Number("net/eth0/rx")); // first sample: no rate
        Assert.Null(h.Number("cpu/total"));
        Assert.False(h.Has("net/lo/rx"));

        h.Time.Advance(TimeSpan.FromSeconds(2));
        body = NodeExporter(1100, 120, 1_250_000, 600_000, 8192);
        await h.StepAsync();

        Assert.Equal(125_000, h.Number("net/eth0/rx"));
        Assert.Equal(50_000, h.Number("net/eth0/tx"));
        Assert.Equal(4096, h.Number("disk/sda/read"));
        // 2 cpus: idle +100 each, busy +20 each => busy share = 40 / 240
        Assert.Equal(100 * 40d / 240, h.Number("cpu/total")!.Value, 6);
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task A_counter_reset_is_counted_from_zero()
    {
        var body = NodeExporter(1000, 100, 1_000_000, 0, 0);
        var http = new FakeHttp().Route("/metrics", _ => new FakeHttp.FakeReply(HttpStatusCode.OK, body));
        var h = Make(http);
        await h.StepAsync();
        h.Time.Advance(TimeSpan.FromSeconds(2));
        body = NodeExporter(1100, 120, 4000, 0, 0); // exporter restarted
        await h.StepAsync();
        Assert.Equal(2000, h.Number("net/eth0/rx"));
    }

    [Fact]
    public async Task Custom_metrics_filter_by_labels_publish_rates_and_split_series()
    {
        var text = """
            custom_temp{room="a"} 20
            custom_temp{room="b"} 22
            requests_total{code="200"} 100
            requests_total{code="500"} 1
            """;
        var http = new FakeHttp().Route("/metrics", _ => new FakeHttp.FakeReply(HttpStatusCode.OK, text));
        var h = Make(http, """
            ,"presets":false,"metrics":[
              {"metric":"custom_temp","name":"Room","unit":"°C"},
              {"metric":"requests_total","labels":"code=\"200\"","rate":true,"name":"OK requests","unit":"","multiplier":60,"id":"requests"}]
            """);

        await h.StepAsync();
        Assert.Equal(20, h.Number("custom/room/a"));
        Assert.Equal(22, h.Number("custom/room/b"));
        Assert.Equal(Units.Celsius, h.Info("custom/room/a")!.Unit);
        Assert.Null(h.Number("custom/requests"));

        h.Time.Advance(TimeSpan.FromSeconds(10));
        text = text.Replace("100", "200");
        await h.StepAsync();
        Assert.Equal(600, h.Number("custom/requests")); // 10 per second times 60
    }

    [Fact]
    public async Task Presets_on_a_foreign_exporter_warn_but_custom_metrics_silence_the_warning()
    {
        var http = new FakeHttp().Route("/metrics", "up 1\n");
        var h = Make(http);
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("node_exporter", h.Message);
    }

    [Fact]
    public async Task Server_mode_runs_promql_and_splits_vectors()
    {
        var http = new FakeHttp()
            .Route("query=up", """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"__name__":"up","instance":"nas:9100"},"value":[1.7e9,"1"]},{"metric":{"instance":"pi:9100"},"value":[1.7e9,"0"]}]}}""")
            .Route("query=scalar", """{"status":"success","data":{"resultType":"scalar","result":[1.7e9,"42.5"]}}""")
            .Route("query=broken", """{"status":"error","errorType":"bad_data","error":"parse error"}""", HttpStatusCode.BadRequest);
        var h = Net.Make(new PrometheusProviderFactory(), """
            {"mode":"server","url":"http://prom:9090","token":"t","queries":[
              {"query":"up","name":"Up","id":"up"},
              {"query":"scalar","name":"Answer","id":"answer"},
              {"query":"broken","name":"Broken"}]}
            """, http);

        await h.StepAsync();

        Assert.Equal(1, h.Number("query/up/nas-9100"));
        Assert.Equal(0, h.Number("query/up/pi-9100"));
        Assert.Equal(42.5, h.Number("query/answer"));
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("parse error", h.Message);
        Assert.Equal("Bearer t", http.Requests[0].Headers["Authorization"]);
        Assert.Contains("/api/v1/query?query=up", http.Requests[0].Uri.PathAndQuery);
    }

    [Fact]
    public void Query_results_with_nan_and_inf_become_no_value()
    {
        var parsed = PrometheusProvider.ParseQueryResult(JsonNode.Parse("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{"a":"x"},"value":[1,"NaN"]},{"metric":{"a":"y"},"value":[1,"+Inf"]}]}}"""));
        Assert.All(parsed, p => Assert.Null(p.Value));
    }

    [Fact]
    public async Task Server_mode_without_queries_is_a_setup_problem()
    {
        var h = Net.Make(new PrometheusProviderFactory(), """{"mode":"server","url":"http://prom:9090"}""", new FakeHttp());
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
    }
}
