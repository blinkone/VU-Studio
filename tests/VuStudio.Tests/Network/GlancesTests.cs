using System.Net;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Remote;

namespace VuStudio.Tests.Network;

public class GlancesTests
{
    private static FakeHttp V4() => new FakeHttp()
        .Route("/api/4/status$", """{"version":"4.5.8"}""")
        .Route("/api/4/system$", """{"hostname":"tower","os_name":"Linux"}""")
        .Route("/api/4/quicklook$", """{"cpu":6.0,"mem":54.4,"swap":0.0,"load":12.5,"cpu_hz_current":3600000000}""")
        .Route("/api/4/cpu$", """{"total":6.0,"user":3.1,"system":3.1,"idle":93.4,"iowait":0.4,"steal":0.0,"cpucore":16}""")
        .Route("/api/4/percpu$", """[{"cpu_number":0,"total":10.5},{"cpu_number":1,"total":2.5}]""")
        .Route("/api/4/load$", """{"min1":0.5,"min5":0.7,"min15":0.9,"cpucore":16}""")
        .Route("/api/4/mem$", """{"total":15880060928,"available":7234739384,"used":8645321544,"percent":54.4}""")
        .Route("/api/4/memswap$", """{"total":2147483648,"used":1073741824,"free":1073741824,"percent":50.0}""")
        .Route("/api/4/network$", """
            [{"interface_name":"eth0","bytes_recv":1000,"bytes_recv_rate_per_sec":125000,"bytes_recv_gauge":1325174313,"bytes_sent_rate_per_sec":2500,"speed":1000000000,"time_since_update":2.0},
             {"interface_name":"lo","bytes_recv_rate_per_sec":5,"bytes_sent_rate_per_sec":5},
             {"interface_name":"veth12ab","bytes_recv_rate_per_sec":5,"bytes_sent_rate_per_sec":5}]
            """)
        .Route("/api/4/diskio$", """[{"disk_name":"sda","read_bytes_rate_per_sec":4096,"write_bytes_rate_per_sec":8192},{"disk_name":"loop0","read_bytes_rate_per_sec":1}]""")
        .Route("/api/4/fs$", """
            [{"mnt_point":"/","device_name":"/dev/mapper/vg-lv","fs_type":"ext4","size":1003736440832,"used":502802178048,"free":449871757312,"percent":52.8},
             {"mnt_point":"/mnt/disk1","fs_type":"xfs","size":8000000000000,"used":2000000000000,"free":6000000000000,"percent":25.0},
             {"mnt_point":"/run","fs_type":"tmpfs","size":1000,"used":1,"free":999,"percent":0.1}]
            """)
        .Route("/api/4/sensors$", """
            [{"label":"Package id 0","value":40,"unit":"C","type":"temperature_core"},
             {"label":"Composite","value":35,"unit":"C","type":"temperature_hdd"},
             {"label":"CPU Fan","value":2100,"unit":"R","type":"fan_speed"},
             {"label":"BAT BAT0","value":87,"unit":"%","type":"battery"}]
            """)
        .Route("/api/4/gpu$", """[{"gpu_id":"nvidia0","name":"RTX 3060","proc":12,"mem":30,"temperature":55,"fan_speed":1200}]""")
        .Route("/api/4/containers$", """[{"name":"plex","status":"running"},{"name":"old","status":"exited"},{"name":"db","status":"running"}]""")
        .Route("/api/4/uptime$", "\"1 day, 7:49:45\"");

    private static Harness Make(FakeHttp http, string extra = "") =>
        Net.Make(new GlancesProviderFactory(), $$"""{"url":"http://nas:61208"{{extra}}}""", http);

    [Fact]
    public async Task V4_publishes_cpu_memory_disks_network_sensors_gpu_containers_and_uptime()
    {
        var h = Make(V4());
        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(6.0, h.Number("cpu/total"));
        Assert.Equal(3600, h.Number("cpu/freq"));
        Assert.Equal(0.9, h.Number("load/15"));
        Assert.Equal(12.5, h.Number("load/percent"));
        Assert.Equal(54.4, h.Number("memory/percent"));
        Assert.Equal(50, h.Number("swap/percent"));
        Assert.Equal(125000, h.Number("net/eth0/rx"));
        Assert.Equal(2500, h.Number("net/eth0/tx"));
        Assert.False(h.Has("net/lo/rx"));
        Assert.False(h.Has("net/veth12ab/rx"));
        Assert.Equal(4096, h.Number("disk/sda/read"));
        Assert.False(h.Has("disk/loop0/read"));
        Assert.Equal(52.8, h.Number("fs/root/percent"));
        Assert.Equal(449871757312, h.Number("fs/root/free"));
        Assert.Equal(25, h.Number("fs/mnt-disk1/percent"));
        Assert.False(h.Has("fs/run/percent"));
        Assert.Equal(40, h.Number("sensors/temp/package-id-0"));
        Assert.Equal(SensorCategory.Cpu, h.Info("sensors/temp/package-id-0")!.Category);
        Assert.Equal(SensorCategory.Storage, h.Info("sensors/temp/composite")!.Category);
        Assert.Equal(2100, h.Number("sensors/fan/cpu-fan"));
        Assert.Equal(87, h.Number("sensors/battery/bat-bat0"));
        Assert.Equal(55, h.Number("gpu/nvidia0/temp"));
        Assert.Equal(1200, h.Number("gpu/nvidia0/fan"));
        Assert.Equal(2, h.Number("containers/running"));
        Assert.Equal(3, h.Number("containers/total"));
        Assert.Equal(114585, h.Number("uptime"));
        Assert.Equal("tower", h.Info("cpu/total")!.Host);
        Assert.False(h.Has("cpu/core/0"));
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Per_core_option_adds_one_sensor_per_core()
    {
        var h = Make(V4(), ",\"perCore\":true");
        await h.StepAsync();
        Assert.Equal(10.5, h.Number("cpu/core/0"));
        Assert.Equal(2.5, h.Number("cpu/core/1"));
    }

    [Fact]
    public async Task V3_is_used_when_v4_is_missing_and_rates_come_from_deltas()
    {
        var http = new FakeHttp()
            .Route("/api/3/status$", """{"version":"3.4.0.5"}""")
            .Route("/api/3/system$", """{"hostname":"pi"}""")
            .Route("/api/3/quicklook$", """{"cpu":10.0,"mem":40.0,"swap":0.0,"load":5.0}""")
            .Route("/api/3/cpu$", """{"total":10.0,"user":6.0,"system":4.0,"iowait":0.0}""")
            .Route("/api/3/mem$", """{"total":1000,"used":400,"available":600,"percent":40.0}""")
            .Route("/api/3/network$", """[{"interface_name":"eth0","rx":4000,"tx":1000,"cumulative_rx":99999,"time_since_update":2.0}]""")
            .Route("/api/3/diskio$", """[{"disk_name":"mmcblk0","read_bytes":20000,"write_bytes":0,"time_since_update":4.0}]""")
            .Route("/api/3/uptime$", """{"uptime":"3:00:00"}""")
            .Route("/api/3/docker$", """{"containers":[{"Id":"a","Status":"running"}],"version":{}}""");
        var h = Make(http);

        await h.StepAsync();

        Assert.True(http.Count("/api/4/status") == 1);
        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(10, h.Number("cpu/total"));
        Assert.Equal(2000, h.Number("net/eth0/rx"));
        Assert.Equal(500, h.Number("net/eth0/tx"));
        Assert.Equal(5000, h.Number("disk/mmcblk0/read"));
        Assert.Equal(10800, h.Number("uptime"));
        Assert.Equal(1, h.Number("containers/running"));
        Assert.Equal("pi", h.Info("cpu/total")!.Host);
    }

    [Fact]
    public async Task Basic_authentication_is_sent_and_a_wrong_password_is_reported()
    {
        var http = V4();
        var h = Make(http, ",\"username\":\"glances\",\"password\":\"pw\"");
        await h.StepAsync();
        Assert.Equal("Basic Z2xhbmNlczpwdw==", http.Requests[0].Headers["Authorization"]);

        var denied = new FakeHttp().Route("/status", "{}", HttpStatusCode.Unauthorized);
        var h2 = Make(denied);
        await h2.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h2.Health);
        Assert.Contains("user name or password", h2.Message);
    }

    [Fact]
    public async Task Not_a_glances_server_is_unavailable_and_missing_plugins_do_not_degrade_the_provider()
    {
        var h = Make(new FakeHttp());
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("No Glances API", h.Message);

        var minimal = new FakeHttp()
            .Route("/api/4/status$", """{"version":"4.5.8"}""")
            .Route("/api/4/quicklook$", """{"cpu":1.0,"mem":2.0,"swap":0.0,"load":1.0}""");
        var h2 = Make(minimal);
        await h2.StepAsync();
        Assert.Equal(ProviderHealth.Ok, h2.Health);
        Assert.Equal(1, h2.Number("cpu/total"));
        Assert.Equal(2, h2.Number("memory/percent"));
    }

    [Theory]
    [InlineData("1 day, 7:49:45", 114585)]
    [InlineData("7:49:45", 28185)]
    [InlineData("12 days, 0:00:03", 1036803)]
    [InlineData("1 day, 0:00:00.5", 86400)]
    public void Uptime_strings_of_python_timedeltas_are_parsed(string text, double seconds) =>
        Assert.Equal(seconds, GlancesProvider.ParseUptime(text));

    [Fact]
    public void Garbage_uptime_is_ignored() => Assert.Null(GlancesProvider.ParseUptime("soon"));

    [Fact]
    public async Task Sensors_that_disappear_are_invalidated_not_removed()
    {
        var http = V4();
        var h = Make(http);
        await h.StepAsync();
        Assert.Equal(125000, h.Number("net/eth0/rx"));

        http.Route("/api/4/network$", "[]");
        await h.StepAsync();
        Assert.True(h.Has("net/eth0/rx"));
        Assert.Null(h.Number("net/eth0/rx"));
    }
}
