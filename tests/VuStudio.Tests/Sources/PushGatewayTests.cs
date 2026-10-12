using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Push;

namespace VuStudio.Tests.Sources;

public class PushGatewayTests
{
    private static (PushGateway Gateway, SensorHub Hub, ISensorSink Sink) Attached(double stale = 120, int max = 500, TimeProvider? time = null)
    {
        var hub = new SensorHub(time);
        var sink = hub.CreateSink("push");
        var gateway = new PushGateway(time);
        gateway.Attach(sink, stale, max);
        return (gateway, hub, sink);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0abc")]
    [InlineData("cpu/load")]
    [InlineData("ha/living.room_temp-1")]
    [InlineData("a/b/c/d")]
    public void Accepts_well_formed_ids(string id)
    {
        var (gateway, hub, _) = Attached();
        gateway.Push(id, 1);
        Assert.Equal(1, hub.GetNumber("push/" + id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("_leading")]
    [InlineData(".leading")]
    [InlineData("/leading")]
    [InlineData("bad!")]
    [InlineData("café")]
    [InlineData("new\nline")]
    [InlineData("trailing\n")]
    [InlineData("push/")]
    [InlineData("a\\b")]
    public void Rejects_malformed_ids_with_a_clear_message(string id)
    {
        var (gateway, hub, _) = Attached();
        var error = Assert.Throws<ArgumentException>(() => gateway.Push(id, 1));
        Assert.Contains("Invalid sensor id", error.Message);
        Assert.Contains("[a-z0-9][a-z0-9/_.-]*", error.Message);
        Assert.Equal(0, hub.Count);
        Assert.Equal(0, gateway.Count);
    }

    [Fact]
    public void Rejects_null_and_overlong_ids()
    {
        var (gateway, _, _) = Attached();
        Assert.Throws<ArgumentException>(() => gateway.Push(null!, 1));
        Assert.Throws<ArgumentException>(() => gateway.Push(new string('a', 129), 1));
        gateway.Push(new string('a', 128), 1);
        Assert.Equal(1, gateway.Count);
    }

    [Fact]
    public void Remove_rejects_malformed_ids_too()
    {
        var (gateway, _, _) = Attached();
        Assert.Throws<ArgumentException>(() => gateway.Remove("Not Valid"));
    }

    [Fact]
    public void A_pushed_number_creates_a_sensor_with_the_given_metadata()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push(
            "house/power",
            1234.5,
            meta: new PushSensorMeta("House power", Units.Watt, SensorCategory.Power, 0, 9000, "flash"));

        var snapshot = hub.Get("push/house/power")!;
        Assert.Equal(1234.5, snapshot.Value);
        Assert.Equal("House power", snapshot.Info.Name);
        Assert.Equal(Units.Watt, snapshot.Info.Unit);
        Assert.Equal(SensorCategory.Power, snapshot.Info.Category);
        Assert.Equal(0, snapshot.Info.Min);
        Assert.Equal(9000, snapshot.Info.Max);
        Assert.Equal("flash", snapshot.Info.Icon);
        Assert.Equal(SensorKinds.Power, snapshot.Info.Kind);
        Assert.Equal(SensorValueKind.Number, snapshot.Info.ValueKind);
        Assert.Equal("push", snapshot.Info.ProviderId);
    }

    [Fact]
    public void Without_metadata_the_sensor_gets_sensible_defaults()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("x", 5);

        var info = hub.Get("push/x")!.Info;
        Assert.Equal("x", info.Name);
        Assert.Equal("Push", info.Device);
        Assert.Equal(SensorCategory.Custom, info.Category);
        Assert.Equal(Units.None, info.Unit);
        Assert.False(string.IsNullOrWhiteSpace(info.Icon));
        Assert.Equal(120, info.StaleAfterSeconds);
    }

    [Fact]
    public void Later_pushes_update_the_value_and_only_replace_the_metadata_that_is_given()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("t", 20, meta: new PushSensorMeta("Room", Units.Celsius, SensorCategory.Home, 0, 40));
        gateway.Push("t", 21);
        Assert.Equal(21, hub.GetNumber("push/t"));
        Assert.Equal("Room", hub.Get("push/t")!.Info.Name);

        gateway.Push("t", 22, meta: new PushSensorMeta(Max: 50) { Device = "Living room" });
        var info = hub.Get("push/t")!.Info;
        Assert.Equal(22, hub.GetNumber("push/t"));
        Assert.Equal("Room", info.Name);
        Assert.Equal(Units.Celsius, info.Unit);
        Assert.Equal(0, info.Min);
        Assert.Equal(50, info.Max);
        Assert.Equal("Living room", info.Device);
    }

    [Fact]
    public void Metadata_updates_do_not_redefine_unchanged_sensors()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("x", 1, meta: new PushSensorMeta("X"));
        var catalogVersion = hub.CatalogVersion;
        gateway.Push("x", 2, meta: new PushSensorMeta("X"));
        gateway.Push("x", 3);
        Assert.Equal(catalogVersion, hub.CatalogVersion);
    }

    [Fact]
    public void A_per_sensor_validity_overrides_the_default()
    {
        var (gateway, hub, _) = Attached(stale: 120);
        gateway.Push("slow", 1, meta: new PushSensorMeta { StaleAfterSeconds = 3600 });
        gateway.Push("normal", 1);
        Assert.Equal(3600, hub.Get("push/slow")!.Info.StaleAfterSeconds);
        Assert.Equal(120, hub.Get("push/normal")!.Info.StaleAfterSeconds);
    }

    [Fact]
    public void A_pushed_text_creates_a_text_sensor()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("status", null, "all good", new PushSensorMeta("Status", Unit: "ignored"));

        var snapshot = hub.Get("push/status")!;
        Assert.Equal("all good", snapshot.Text);
        Assert.Null(snapshot.Value);
        Assert.Equal(SensorValueKind.Text, snapshot.Info.ValueKind);
        Assert.Equal(SensorKinds.Text, snapshot.Info.Kind);
        Assert.Equal(Units.None, snapshot.Info.Unit);
    }

    [Theory]
    [InlineData("#ff8000", "#FF8000")]
    [InlineData("#00AAFF", "#00AAFF")]
    public void A_hex_color_text_creates_a_color_sensor(string pushed, string stored)
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("accent", null, pushed);

        var snapshot = hub.Get("push/accent")!;
        Assert.Equal(stored, snapshot.Text);
        Assert.Equal(SensorValueKind.Color, snapshot.Info.ValueKind);
        Assert.Equal(SensorKinds.Color, snapshot.Info.Kind);
    }

    [Fact]
    public void Other_hash_text_is_just_text()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("t", null, "#nope");
        Assert.Equal(SensorValueKind.Text, hub.Get("push/t")!.Info.ValueKind);
    }

    [Fact]
    public void A_sensor_can_change_from_number_to_text_and_back()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("x", 5, meta: new PushSensorMeta(Unit: Units.Percent, Max: 100));
        gateway.Push("x", null, "n/a");
        var asText = hub.Get("push/x")!;
        Assert.Equal(SensorValueKind.Text, asText.Info.ValueKind);
        Assert.Equal("n/a", asText.Text);
        Assert.Null(asText.Info.Max);

        gateway.Push("x", 7);
        var asNumber = hub.Get("push/x")!;
        Assert.Equal(SensorValueKind.Number, asNumber.Info.ValueKind);
        Assert.Equal(7, asNumber.Value);
        Assert.Equal(Units.Percent, asNumber.Info.Unit); // the unit the client chose earlier is remembered
        Assert.Equal(100, asNumber.Info.Max);
    }

    [Fact]
    public void A_number_wins_when_both_are_given()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("x", 3, "three");
        var snapshot = hub.Get("push/x")!;
        Assert.Equal(3, snapshot.Value);
        Assert.Null(snapshot.Text);
        Assert.Equal(SensorValueKind.Number, snapshot.Info.ValueKind);
    }

    [Fact]
    public void Requires_a_value_or_a_text()
    {
        var (gateway, _, _) = Attached();
        Assert.Throws<ArgumentException>(() => gateway.Push("x", null, null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Rejects_values_that_are_not_finite(double value)
    {
        var (gateway, _, _) = Attached();
        Assert.Throws<ArgumentException>(() => gateway.Push("x", value));
    }

    [Fact]
    public void Rejects_overlong_text_and_unknown_categories()
    {
        var (gateway, _, _) = Attached();
        Assert.Throws<ArgumentException>(() => gateway.Push("x", null, new string('a', PushGateway.MaxTextLength + 1)));
        gateway.Push("x", null, new string('a', PushGateway.MaxTextLength));
        Assert.Throws<ArgumentException>(() => gateway.Push("y", 1, meta: new PushSensorMeta(Category: (SensorCategory)999)));
    }

    [Fact]
    public void Overlong_metadata_is_trimmed_and_non_finite_bounds_are_dropped()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("x", 1, meta: new PushSensorMeta(new string('n', 500), new string('u', 100), Min: double.NaN, Max: double.PositiveInfinity));

        var info = hub.Get("push/x")!.Info;
        Assert.Equal(100, info.Name.Length);
        Assert.Equal(16, info.Unit.Length);
        Assert.Null(info.Min);
        Assert.Null(info.Max);
    }

    [Fact]
    public void The_provider_prefix_is_accepted_but_not_doubled()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("push/x", 1);
        gateway.Push("x", 2);
        Assert.Equal(["x"], gateway.Ids);
        Assert.Equal(2, hub.GetNumber("push/x"));
        Assert.Equal(1, hub.Count);
    }

    [Fact]
    public void Remove_forgets_the_sensor_and_removes_it_from_the_hub()
    {
        var (gateway, hub, _) = Attached();
        gateway.Push("a", 1);
        gateway.Push("b", 2);

        Assert.True(gateway.Remove("a"));
        Assert.False(gateway.Remove("a"));
        Assert.False(gateway.Remove("never-existed"));
        Assert.Null(hub.Get("push/a"));
        Assert.NotNull(hub.Get("push/b"));
        Assert.Equal(["b"], gateway.Ids);
    }

    [Fact]
    public void The_number_of_sensors_is_limited_but_existing_ones_keep_updating()
    {
        var (gateway, hub, _) = Attached(max: 3);
        gateway.Push("a", 1);
        gateway.Push("b", 1);
        gateway.Push("c", 1);

        var error = Assert.Throws<InvalidOperationException>(() => gateway.Push("d", 1));
        Assert.Contains("limit 3", error.Message);
        Assert.Null(hub.Get("push/d"));

        gateway.Push("a", 2);
        Assert.Equal(2, hub.GetNumber("push/a"));

        gateway.Remove("b");
        gateway.Push("d", 1);
        Assert.Equal(3, gateway.Count);
    }

    [Fact]
    public void Pushes_without_a_running_provider_are_kept_and_replayed_on_attach()
    {
        var gateway = new PushGateway();
        Assert.False(gateway.IsAttached);
        gateway.Push("early", 42, meta: new PushSensorMeta("Early bird", Unit: Units.Watt));
        gateway.Push("note", null, "hello");

        var hub = new SensorHub();
        gateway.Attach(hub.CreateSink("push"), 120, 500);

        Assert.True(gateway.IsAttached);
        Assert.Equal(42, hub.GetNumber("push/early"));
        Assert.Equal("Early bird", hub.Get("push/early")!.Info.Name);
        Assert.Equal("hello", hub.GetText("push/note"));
    }

    [Fact]
    public void Values_that_went_stale_while_detached_are_defined_but_not_replayed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var gateway = new PushGateway(time);
        gateway.Push("old", 1);
        time.Advance(TimeSpan.FromSeconds(200));
        gateway.Push("fresh", 2);
        time.Advance(TimeSpan.FromSeconds(10));

        var hub = new SensorHub(time);
        gateway.Attach(hub.CreateSink("push"), 120, 500);

        Assert.NotNull(hub.Get("push/old"));
        Assert.Null(hub.Get("push/old")!.Value);
        Assert.Equal(2, hub.GetNumber("push/fresh"));
    }

    [Fact]
    public void Detaching_stops_publishing_but_keeps_the_state()
    {
        var (gateway, hub, sink) = Attached();
        gateway.Push("x", 1);

        gateway.Detach(sink);
        Assert.False(gateway.IsAttached);
        gateway.Push("x", 2);
        Assert.Equal(1, hub.GetNumber("push/x"));
        Assert.Equal(["x"], gateway.Ids);
    }

    [Fact]
    public void Detaching_a_sink_that_is_not_attached_is_ignored()
    {
        var (gateway, hub, _) = Attached();
        var stranger = new SensorHub().CreateSink("push");
        gateway.Detach(stranger);
        Assert.True(gateway.IsAttached);

        gateway.Push("x", 1);
        Assert.Equal(1, hub.GetNumber("push/x"));
    }

    [Fact]
    public void A_restarted_provider_gets_everything_back_on_its_new_hub()
    {
        var (gateway, firstHub, firstSink) = Attached();
        gateway.Push("x", 1, meta: new PushSensorMeta("X", Unit: Units.Percent));
        gateway.Detach(firstSink);
        firstHub.RemoveProvider("push"); // what ProviderManager does when it stops an instance

        var secondHub = new SensorHub();
        gateway.Attach(secondHub.CreateSink("push"), 120, 500);

        Assert.Equal(1, secondHub.GetNumber("push/x"));
        Assert.Equal("X", secondHub.Get("push/x")!.Info.Name);
    }

    [Fact]
    public async Task Concurrent_pushes_are_safe()
    {
        var (gateway, hub, _) = Attached();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                gateway.Push($"s/{i % 50}", worker * 1000 + i, meta: new PushSensorMeta($"Sensor {i % 50}"));
            }
        })));

        Assert.Equal(50, gateway.Count);
        Assert.Equal(50, hub.Count);
        Assert.All(gateway.Ids, id => Assert.NotNull(hub.GetNumber("push/" + id)));
    }
}

public class PushProviderTests
{
    [Fact]
    public async Task The_provider_attaches_while_running_and_detaches_when_cancelled()
    {
        var gateway = new PushGateway();
        var services = TestServices.With(s => s.AddSingleton(gateway));
        var provider = new PushProviderFactory().Create(TestConfig.For("push"), services);
        var hub = new SensorHub();
        var status = new RecordingStatus();

        await ProviderRunner.RunUntilAsync(
            provider,
            hub,
            () =>
            {
                if (gateway.IsAttached)
                {
                    gateway.Push("answer", 42);
                }

                return hub.GetNumber("push/answer") == 42;
            },
            status: status);

        Assert.False(gateway.IsAttached);
        Assert.Contains(status.Reports, r => r.Health == ProviderHealth.Ok);
        Assert.Equal(["answer"], gateway.Ids); // the state survives the provider
    }

    [Fact]
    public void Without_a_registered_gateway_the_shared_one_is_used()
    {
        var provider = (PushProvider)new PushProviderFactory().Create(TestConfig.For("push"), TestServices.Empty());
        Assert.Same(PushGateway.Shared, provider.Gateway);
    }

    [Fact]
    public void A_registered_gateway_is_preferred()
    {
        var gateway = new PushGateway();
        var provider = (PushProvider)new PushProviderFactory().Create(TestConfig.For("push"), TestServices.With(s => s.AddSingleton(gateway)));
        Assert.Same(gateway, provider.Gateway);
    }

    [Fact]
    public async Task Settings_reach_the_gateway()
    {
        var gateway = new PushGateway();
        var provider = new PushProviderFactory().Create(TestConfig.For("push", """{"staleAfter":300,"maxSensors":2}"""), TestServices.With(s => s.AddSingleton(gateway)));
        var hub = new SensorHub();

        Exception? limitError = null;
        await ProviderRunner.RunUntilAsync(
            provider,
            hub,
            () =>
            {
                if (!gateway.IsAttached)
                {
                    return false;
                }

                gateway.Push("a", 1);
                gateway.Push("b", 1);
                limitError = Record.Exception(() => gateway.Push("c", 1));
                return true;
            });

        Assert.Equal(300, hub.Get("push/a")!.Info.StaleAfterSeconds);
        Assert.IsType<InvalidOperationException>(limitError);
    }
}
