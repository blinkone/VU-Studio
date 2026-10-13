using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Remote;

namespace VuStudio.Tests.Network;

public class LhmRemoteTests
{
    // Shape of HttpServer.cs v0.9.6 (windows-sources.md section 5), values in the Italian culture of the host.
    private const string DataJson = """
        {"id":0,"Text":"Sensor","Min":"Min","Value":"Value","Max":"Max","ImageURL":"","Children":[
         {"id":1,"Text":"GAMING-PC","Min":"","Value":"","Max":"","ImageURL":"images_icon/computer.png","Children":[
          {"id":2,"Text":"AMD Ryzen 9 7900X","Min":"","Value":"","Max":"","HardwareId":"/amdcpu/0","ImageURL":"images_icon/cpu.png","Children":[
           {"id":3,"Text":"Temperatures","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":4,"Text":"Core (Tctl/Tdie)","Min":"36,1 °C","Value":"52,3 °C","Max":"81,4 °C","SensorId":"/amdcpu/0/temperature/2","Type":"Temperature","RawMin":"36,1 °C","RawValue":"52,3 °C","RawMax":"81,4 °C","ImageURL":"","Children":[]}]},
           {"id":5,"Text":"Load","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":6,"Text":"CPU Total","Min":"1,0 %","Value":"13,5 %","Max":"100,0 %","SensorId":"/amdcpu/0/load/0","Type":"Load","RawValue":"13,5 %","ImageURL":"","Children":[]}]},
           {"id":7,"Text":"Clocks","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":8,"Text":"Core #1","Min":"","Value":"4,5 GHz","Max":"","SensorId":"/amdcpu/0/clock/1","Type":"Clock","RawValue":"4500,0 MHz","ImageURL":"","Children":[]}]}]},
          {"id":10,"Text":"NVIDIA GeForce RTX 4080","Min":"","Value":"","Max":"","HardwareId":"/gpu-nvidia/0","ImageURL":"","Children":[
           {"id":11,"Text":"Fans","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":12,"Text":"GPU Fan","Min":"","Value":"1.234 RPM","Max":"","SensorId":"/gpu-nvidia/0/fan/1","Type":"Fan","RawValue":1234.0,"ImageURL":"","Children":[]}]},
           {"id":13,"Text":"Data","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":14,"Text":"GPU Memory Used","Min":"","Value":"2,5 GB","Max":"","SensorId":"/gpu-nvidia/0/smalldata/1","Type":"SmallData","RawValue":"2560,0 MB","ImageURL":"","Children":[]},
            {"id":15,"Text":"Missing","Min":"","Value":"-","Max":"","SensorId":"/gpu-nvidia/0/smalldata/9","Type":"SmallData","RawValue":"-","ImageURL":"","Children":[]}]}]},
          {"id":20,"Text":"Ethernet","Min":"","Value":"","Max":"","HardwareId":"/nic/{ABC}","ImageURL":"","Children":[
           {"id":21,"Text":"Throughput","Min":"","Value":"","Max":"","ImageURL":"","Children":[
            {"id":22,"Text":"Download Speed","Min":"","Value":"1,5 MB/s","Max":"","SensorId":"/nic/{ABC}/throughput/8","Type":"Throughput","RawValue":"1,5 MB/s","ImageURL":"","Children":[]}]}]}]}]}
        """;

    [Fact]
    public void Tree_parser_handles_culture_formatted_values_and_units()
    {
        var (computer, sensors) = LhmTree.Parse(JsonNode.Parse(DataJson));
        Assert.Equal("GAMING-PC", computer);
        Assert.Equal(7, sensors.Count);
        Assert.Equal(52.3, sensors.Single(s => s.SensorId.EndsWith("temperature/2")).Value);
        Assert.Equal(13.5, sensors.Single(s => s.Name == "CPU Total").Value);
        Assert.Equal(4500, sensors.Single(s => s.Name == "Core #1").Value); // RawValue wins over "4,5 GHz"
        Assert.Equal(1234, sensors.Single(s => s.Name == "GPU Fan").Value); // JSON number
        Assert.Equal(2560d * 1024 * 1024, sensors.Single(s => s.Name == "GPU Memory Used").Value);
        Assert.Null(sensors.Single(s => s.Name == "Missing").Value);
        Assert.Equal(1.5 * 1024 * 1024, sensors.Single(s => s.Name == "Download Speed").Value);
        Assert.Equal("AMD Ryzen 9 7900X", sensors.First().Hardware);
    }

    [Theory]
    [InlineData("Temperature", "125 °F", 51.666666)]
    [InlineData("Temperature", "52,3 °C", 52.3)]
    [InlineData("Throughput", "8 Mbps", 1_000_000)]
    [InlineData("Throughput", "512 KB/s", 524288)]
    [InlineData("Energy", "1500 mWh", 1.5)]
    [InlineData("Power", "45,2 W", 45.2)]
    [InlineData("Voltage", "1,25 V", 1.25)]
    public void Units_are_converted_to_canonical(string type, string text, double expected) =>
        Assert.Equal(expected, LhmTree.Convert(type, JsonValue.Create(text)).Value!.Value, 4);

    [Fact]
    public void Timespans_become_seconds() =>
        Assert.Equal(3725, LhmTree.Convert("TimeSpan", JsonValue.Create("1:02:05")).Value);

    [Fact]
    public async Task Provider_publishes_sensors_with_host_category_and_stable_ids()
    {
        var http = new FakeHttp().Route("/data.json", DataJson);
        var h = Net.Make(new LhmRemoteProviderFactory(), """{"url":"pc.local:8085","username":"u","password":"p"}""", http);

        await h.StepAsync();

        Assert.Equal("http://pc.local:8085/data.json", http.Requests[0].Uri.ToString());
        Assert.StartsWith("Basic ", http.Requests[0].Headers["Authorization"]);
        Assert.Equal(52.3, h.Number("amdcpu/0/temperature/2"));
        var info = h.Info("amdcpu/0/temperature/2")!;
        Assert.Equal("GAMING-PC", info.Host);
        Assert.Equal("AMD Ryzen 9 7900X", info.Device);
        Assert.Equal(SensorCategory.Cpu, info.Category);
        Assert.Equal(Units.Celsius, info.Unit);
        Assert.Equal(SensorCategory.Gpu, h.Info("gpu-nvidia/0/fan/1")!.Category);
        Assert.Equal(SensorCategory.Network, h.Info("nic/abc/throughput/8")!.Category);
        Assert.Null(h.Number("gpu-nvidia/0/smalldata/9"));
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task A_page_that_is_not_lhm_is_reported_clearly()
    {
        var h = Net.Make(new LhmRemoteProviderFactory(), """{"url":"http://pc:8085/data.json"}""", new FakeHttp().Route("data.json", """{"hello":"world"}"""));
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("LibreHardwareMonitor", h.Message);
    }
}
