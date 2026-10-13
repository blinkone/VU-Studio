using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.HomeAssistant;
using VuStudio.Sources.Mqtt;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Network;

public class MqttTests
{
    [Theory]
    [InlineData("tele/+/SENSOR", "tele/plug1/SENSOR", true)]
    [InlineData("tele/+/SENSOR", "tele/a/b/SENSOR", false)]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt/lamp/state", true)]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt", true)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("#", "$SYS/x", false)]
    public void Topic_filters_match_like_mqtt(string filter, string topic, bool expected) =>
        Assert.Equal(expected, MqttTopics.Matches(filter, topic));

    [Theory]
    [InlineData("21.5", null, false, 21.5)]
    [InlineData("21,5", null, false, 21.5)]
    [InlineData("ON", null, false, 1)]
    [InlineData("offline", null, false, 0)]
    [InlineData("{\"ENERGY\":{\"Power\":45}}", "$.ENERGY.Power", false, 45)]
    [InlineData("{\"temperature\":{\"tC\":42.5}}", "$.temperature.tC", false, 42.5)]
    [InlineData("{\"output\":true}", "$.output", false, 1)]
    [InlineData("{\"a\":[1,2,3]}", "$.a[-1]", false, 3)]
    public void Numbers_are_extracted(string payload, string? path, bool text, double expected) =>
        Assert.Equal(expected, MqttPayload.Extract(payload, path, text).Number);

    [Fact]
    public void Bad_payloads_give_no_value_and_text_mode_returns_text()
    {
        Assert.False(MqttPayload.Extract("hello", null, false).HasValue);
        Assert.False(MqttPayload.Extract("not json", "$.x", false).HasValue);
        Assert.False(MqttPayload.Extract("{\"a\":1}", "$.missing", false).HasValue);
        Assert.Equal("hello", MqttPayload.Extract("hello", null, true).Text);
        Assert.Equal("Online", MqttPayload.Extract("{\"s\":\"Online\"}", "$.s", true).Text);
    }

    private static MqttProvider Provider(string subscriptions, string extra = "", string host = "broker")
    {
        var factory = new MqttProviderFactory();
        return (MqttProvider)factory.Create(TestConfig.For("mqtt", $$"""{"host":"{{host}}","subscriptions":{{subscriptions}}{{extra}}}""", "p"), TestServices.Empty());
    }

    [Fact]
    public async Task Messages_become_sensors_with_wildcards_and_units()
    {
        var h = new Harness(_ => Provider("""
            [{"topic":"tele/+/SENSOR","id":"plug","name":"Plug power","path":"$.ENERGY.Power","unit":"W","category":"power","min":0,"max":3000},
             {"topic":"home/status","name":"Status","text":true}]
            """));
        var p = (MqttProvider)h.Provider;

        p.Apply("tele/kitchen/SENSOR", """{"Time":"x","ENERGY":{"Power":45}}""");
        p.Apply("tele/desk/SENSOR", """{"ENERGY":{"Power":120}}""");
        p.Apply("home/status", "all good");

        Assert.Equal(45, h.Number("plug/tele/kitchen/sensor"));
        Assert.Equal(120, h.Number("plug/tele/desk/sensor"));
        var info = h.Info("plug/tele/kitchen/sensor")!;
        Assert.Equal(Units.Watt, info.Unit);
        Assert.Equal(SensorCategory.Power, info.Category);
        Assert.Equal("broker", info.Host);
        Assert.True(info.StaleAfterSeconds >= 3600);
        Assert.Equal("all good", h.Text("status"));
        h.AssertCatalogIsSound();
        await Task.CompletedTask;
    }

    [Fact]
    public void Home_assistant_discovery_configs_become_rows()
    {
        var row = MqttDiscovery.Parse("homeassistant", "homeassistant/sensor/bedroom/temp/config", """
            {"device_class":"temperature","stat_t":"~/state","~":"home/bedroom","unit_of_meas":"°F","val_tpl":"{{ value_json.temperature }}","uniq_id":"temp01ae","dev":{"name":"Bedroom"}}
            """, 3600)!;
        Assert.Equal("home/bedroom/state", row.Filter);
        Assert.Equal("$.temperature", row.Path);
        Assert.Equal(Units.Celsius, row.Unit);
        Assert.Equal("Bedroom", row.Device);
        Assert.Equal(5d / 9, row.Scale, 6);
        Assert.Null(MqttDiscovery.Parse("homeassistant", "homeassistant/sensor/x/config", "", 3600));
        Assert.Null(MqttDiscovery.Parse("homeassistant", "homeassistant/light/x/config", """{"stat_t":"a"}""", 3600));
    }

    [Fact]
    public async Task Discovery_import_publishes_and_withdraws_sensors()
    {
        var h = new Harness(_ => Provider("[]", ",\"haDiscovery\":true"));
        var p = (MqttProvider)h.Provider;
        await p.HandleAsync(null, null, "homeassistant/sensor/room/hum/config", """{"stat_t":"r/hum","unit_of_meas":"%","dev_cla":"humidity","uniq_id":"hum1","name":"Room humidity"}""", default);
        await p.HandleAsync(null, null, "r/hum", "48.5", default);
        Assert.Equal(48.5, h.Number("ha/hum1"));
        Assert.Equal(SensorKinds.Humidity, h.Info("ha/hum1")!.Kind);
        await p.HandleAsync(null, null, "homeassistant/sensor/room/hum/config", "", default);
        Assert.Null(h.Number("ha/hum1"));
    }

    [Fact]
    public async Task Unreachable_broker_is_degraded_not_a_crash()
    {
        var h = new Harness(_ => Provider("""[{"topic":"a/b"}]""", ",\"port\":1", "127.0.0.1"));
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("Cannot connect", h.Message);

        var none = new Harness(_ => Provider("[]"));
        await none.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, none.Health);
    }
}

public class HomeAssistantTests
{
    [Fact]
    public void Units_and_device_classes_map_to_canonical()
    {
        var f = HaMapping.Map("°F", "temperature", "sensor.living_room_temperature");
        Assert.Equal(Units.Celsius, f.Unit);
        Assert.Equal(20, f.Convert(68)!.Value, 6);
        Assert.Equal(SensorCategory.Home, f.Category);
        Assert.Equal(SensorCategory.Weather, HaMapping.Map("°C", "temperature", "sensor.outdoor_temp").Category);
        Assert.Equal(1500, HaMapping.Map("kW", "power").Convert(1.5));
        Assert.Equal(Units.WattHour, HaMapping.Map("kWh", "energy").Unit);
        Assert.Equal(SensorCategory.Battery, HaMapping.Map("%", "battery").Category);
        Assert.Equal(SensorKinds.Humidity, HaMapping.Map("%", "humidity").Kind);
        Assert.Equal(Units.Celsius, HaMapping.Map(null, "temperature").Unit);
        Assert.Equal("ppm", HaMapping.Map("ppm", "carbon_dioxide").Unit);
    }

    [Fact]
    public void States_become_numbers()
    {
        Assert.Equal(21.4, HaMapping.ToNumber("21.4", false));
        Assert.Null(HaMapping.ToNumber("unavailable", true));
        Assert.Null(HaMapping.ToNumber("on", false));
        Assert.Equal(1, HaMapping.ToNumber("on", true));
        Assert.Equal(0, HaMapping.ToNumber("not_home", true));
    }

    [Fact]
    public void Compressed_websocket_events_are_reduced()
    {
        var store = new HaEntityStore();
        var (changed, _) = store.ApplyCompressed(JsonNode.Parse("""
            {"a":{"sensor.grid_power":{"s":"412","a":{"unit_of_measurement":"W","device_class":"power","old":1},"c":"01J","lc":1791817190.0}}}
            """));
        Assert.Contains("sensor.grid_power", changed);
        store.ApplyCompressed(JsonNode.Parse("""{"c":{"sensor.grid_power":{"+":{"s":"430","a":{"icon":"mdi:flash"}},"-":{"a":["old"]}}}}"""));
        var state = store.States["sensor.grid_power"];
        Assert.Equal("430", state.State);
        Assert.Equal("W", state.Text("unit_of_measurement"));
        Assert.Equal("mdi:flash", state.Text("icon"));
        Assert.Null(state.Text("old"));
        var (_, removed) = store.ApplyCompressed(JsonNode.Parse("""{"r":["sensor.grid_power"]}"""));
        Assert.Contains("sensor.grid_power", removed);
        Assert.Empty(store.States);
    }

    [Fact]
    public void Selectors_parse_attributes()
    {
        Assert.Equal(new HaSelector("climate.living", "current_temperature"), HaSelector.Parse("climate.living#current_temperature"));
        Assert.Null(HaSelector.Parse("nodot"));
    }

    private const string States = """
        [{"entity_id":"sensor.living_room_temperature","state":"21.4","attributes":{"unit_of_measurement":"°C","friendly_name":"Living room temperature","device_class":"temperature","state_class":"measurement"}},
         {"entity_id":"sensor.grid_power","state":"1.5","attributes":{"unit_of_measurement":"kW","device_class":"power","friendly_name":"Grid power"}},
         {"entity_id":"sensor.broken","state":"unavailable","attributes":{"unit_of_measurement":"V","friendly_name":"Broken"}},
         {"entity_id":"sensor.mode","state":"eco","attributes":{"friendly_name":"Mode"}},
         {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk"}},
         {"entity_id":"climate.living","state":"heat","attributes":{"friendly_name":"Living","current_temperature":19.5}}]
        """;

    private static Harness Make(FakeHttp http, string extra = "") =>
        Net.Make(new HomeAssistantProviderFactory(), $$"""{"url":"ha.local:8123","token":"tok","useWebSocket":false{{extra}}}""", http);

    [Fact]
    public async Task Rest_polling_publishes_all_numeric_sensors_when_no_entities_are_given()
    {
        var http = new FakeHttp().Route("/api/states", States);
        var h = Make(http);
        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal("Bearer tok", http.Requests[0].Headers["Authorization"]);
        Assert.Equal(21.4, h.Number("sensor/living_room_temperature"));
        Assert.Equal("Living room temperature", h.Info("sensor/living_room_temperature")!.Name);
        Assert.Equal(1500, h.Number("sensor/grid_power"));
        Assert.Equal(Units.Watt, h.Info("sensor/grid_power")!.Unit);
        Assert.True(h.Has("sensor/broken"));
        Assert.Null(h.Number("sensor/broken"));
        Assert.False(h.Has("sensor/mode"));
        Assert.False(h.Has("light/desk"));
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Listed_entities_cover_switches_text_and_attributes()
    {
        var http = new FakeHttp().Route("/api/states/light.desk", """{"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk"}}""")
            .Route("/api/states/sensor.mode", """{"entity_id":"sensor.mode","state":"eco","attributes":{"friendly_name":"Mode"}}""")
            .Route("/api/states/climate.living", """{"entity_id":"climate.living","state":"heat","attributes":{"current_temperature":19.5,"friendly_name":"Living"}}""");
        var h = Make(http, ",\"entities\":[\"light.desk\",\"sensor.mode\",\"climate.living#current_temperature\",\"sensor.ghost\"]");
        await h.StepAsync();

        Assert.Equal(1, h.Number("light/desk"));
        Assert.Equal("eco", h.Text("sensor/mode"));
        Assert.Equal(19.5, h.Number("climate/living/current-temperature"));
        Assert.Equal(Units.Celsius, h.Info("climate/living/current-temperature")!.Unit);
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("sensor.ghost", h.Message);
    }

    [Fact]
    public async Task Bad_token_is_unavailable()
    {
        var h = Make(new FakeHttp().Route("/api/states", "{}", System.Net.HttpStatusCode.Unauthorized));
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("access token", h.Message);
    }
}
