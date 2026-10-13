using System.Globalization;
using System.Text.Json;
using VuStudio.Core.Config;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;
using VuStudio.Core.Profiles;
using VuStudio.Core.Sensors;

namespace VuStudio.Tests.Core;

public class ValueMappingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0.5)]
    [InlineData(100, 1)]
    [InlineData(150, 1)]
    [InlineData(-10, 0)]
    public void Linear_maps_and_clamps(double value, double expected) =>
        Assert.Equal(expected, new ValueMapping(0, 100).ToFraction(value), 6);

    [Fact]
    public void Inverted_mapping_mirrors()
    {
        var m = new ValueMapping(0, 100, invert: true);
        Assert.Equal(1, m.ToFraction(0), 6);
        Assert.Equal(0.25, m.ToFraction(75), 6);
        Assert.Equal(75, m.FromFraction(0.25), 6);
    }

    [Theory]
    [InlineData(ScaleType.Linear)]
    [InlineData(ScaleType.Logarithmic)]
    [InlineData(ScaleType.SquareRoot)]
    public void FromFraction_is_inverse_of_ToFraction(ScaleType scale)
    {
        var m = new ValueMapping(0, 1000, scale);
        foreach (var v in new[] { 0.0, 1, 10, 99, 500, 1000 })
        {
            Assert.Equal(v, m.FromFraction(m.ToFraction(v)), 6);
        }
    }

    [Fact]
    public void Logarithmic_expands_low_end()
    {
        var m = new ValueMapping(0, 1000, ScaleType.Logarithmic);
        Assert.True(m.ToFraction(10) > 0.3);
        Assert.Equal(1, m.ToFraction(1000), 6);
    }

    [Fact]
    public void Custom_points_are_piecewise_linear()
    {
        var m = new ValueMapping(0, 0, ScaleType.Custom, [new ScalePoint(0, 0), new ScalePoint(60, 0.5), new ScalePoint(100, 1)]);
        Assert.Equal(0.25, m.ToFraction(30), 6);
        Assert.Equal(0.75, m.ToFraction(80), 6);
        Assert.Equal(80, m.FromFraction(0.75), 6);
    }

    [Fact]
    public void Nice_labels_for_linear_scale()
    {
        Assert.Equal([0, 25, 50, 75, 100], new ValueMapping(0, 100).SuggestLabelValues(5));
        Assert.Equal([0, 2000, 4000, 6000], new ValueMapping(0, 6000).SuggestLabelValues(4));
    }
}

public class UnitConverterTests
{
    [Fact]
    public void Converts_temperatures() =>
        Assert.Equal(212, UnitConverter.Convert(100, "°C", "°F"), 6);

    [Fact]
    public void Converts_byte_rates_to_bits() =>
        Assert.Equal(8, UnitConverter.Convert(1_000_000, "B/s", "Mbit/s"), 6);

    [Fact]
    public void Picks_auto_unit()
    {
        Assert.Equal("MB/s", UnitConverter.PickAutoUnit(5 * 1024 * 1024, "B/s"));
        Assert.Equal("GHz", UnitConverter.PickAutoUnit(4500, "MHz"));
        Assert.Equal("%", UnitConverter.PickAutoUnit(42, "%"));
    }

    [Fact]
    public void Formats_with_auto_precision() =>
        Assert.Equal(("3.50", "GB"), UnitConverter.Format(3.5 * 1024 * 1024 * 1024, "B"));
}

public class SmootherTests
{
    [Fact]
    public void Zero_time_constants_follow_instantly()
    {
        var s = new Smoother();
        Assert.Equal(10, s.Update(10, 50));
        Assert.Equal(90, s.Update(90, 50));
    }

    [Fact]
    public void Attack_and_release_are_asymmetric()
    {
        var s = new Smoother { AttackMs = 10, ReleaseMs = 1000 };
        s.Update(0, 0);
        var up = s.Update(100, 50);
        Assert.True(up > 99);
        var down = s.Update(0, 50);
        Assert.True(down > 90);
    }

    [Fact]
    public void Hold_keeps_peak()
    {
        var s = new Smoother { HoldMs = 200 };
        s.Update(80, 10);
        Assert.Equal(80, s.Update(10, 100));
        Assert.Equal(10, s.Update(10, 150));
    }
}

public class SensorHubTests
{
    [Fact]
    public void Sink_prefixes_ids_and_stores_values()
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("demo");
        sink.Define(new SensorInfo { Id = "cpu/load", Name = "CPU", Unit = Units.Percent });
        sink.Publish("cpu/load", 42);

        Assert.Equal(42, hub.GetNumber("demo/cpu/load"));
        Assert.Equal("demo", hub.Get("demo/cpu/load")!.Info.ProviderId);
    }

    [Fact]
    public void Changed_since_reports_only_new_updates()
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("p");
        sink.Publish("a", 1);
        sink.Publish("b", 2);
        _ = hub.GetChangedSince(0, out var v);
        sink.Publish("b", 3);
        var changed = hub.GetChangedSince(v, out _);
        Assert.Single(changed);
        Assert.Equal("p/b", changed[0].Id);
    }

    [Fact]
    public void Stale_values_are_hidden()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var hub = new SensorHub(time) { DefaultStaleAfter = TimeSpan.FromSeconds(5) };
        hub.CreateSink("p").Publish("x", 7);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(hub.GetNumber("p/x"));
        Assert.True(hub.Get("p/x")!.IsStale);
    }

    [Fact]
    public void Remove_provider_drops_its_sensors()
    {
        var hub = new SensorHub();
        hub.CreateSink("a").Publish("x", 1);
        hub.CreateSink("b").Publish("x", 1);
        hub.RemoveProvider("a");
        Assert.Null(hub.Get("a/x"));
        Assert.NotNull(hub.Get("b/x"));
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public class TextTemplateTests
{
    private static FaceRenderContext Context(SensorHub? hub = null) => new()
    {
        Title = "CPU",
        Unit = "°C",
        Value = 63.456,
        Mapping = new ValueMapping(20, 100),
        Culture = CultureInfo.InvariantCulture,
        Now = new DateTimeOffset(2026, 10, 12, 9, 5, 0, TimeSpan.Zero),
        Sensors = id => hub?.Get(id),
    };

    [Theory]
    [InlineData("{title}", "CPU")]
    [InlineData("{value}{unit}", "63.5°C")]
    [InlineData("{value|0}", "63")]
    [InlineData("{min}-{max}", "20-100")]
    [InlineData("{time:HH:mm}", "09:05")]
    [InlineData("{{literal}}", "{literal}")]
    [InlineData("{unknown}", "{unknown}")]
    public void Renders_placeholders(string template, string expected) =>
        Assert.Equal(expected, TextTemplate.Render(template, Context()));

    [Fact]
    public void Renders_other_sensors_with_auto_units()
    {
        var hub = new SensorHub();
        var sink = hub.CreateSink("net");
        sink.Define(new SensorInfo { Id = "down", Name = "Down", Unit = Units.BytesPerSecond });
        sink.Publish("down", 3 * 1024 * 1024);
        Assert.Equal("3.00 MB/s", TextTemplate.Render("{sensor:net/down}", Context(hub)));
        Assert.Equal(["net/down"], TextTemplate.ReferencedSensors("x {sensor.value:net/down} y"));
    }
}

public class BacklightTests
{
    private static BacklightInputs Inputs(double? value, double fraction, DateTimeOffset now) =>
        new(value, fraction, now, _ => null, _ => null, null, null);

    [Fact]
    public void Gradient_interpolates()
    {
        var config = new BacklightConfig
        {
            Mode = BacklightMode.Gradient,
            Brightness = 100,
            Gradient = [new(0, new RgbwColor(0, 0, 0)), new(1, new RgbwColor(255, 0, 0))],
        };
        var output = BacklightEngine.Compute(config, new BacklightState(), Inputs(50, 0.5, DateTimeOffset.UnixEpoch));
        Assert.Equal(new LedLevels(50, 0, 0, 0), output.Levels);
    }

    [Fact]
    public void Alarm_fires_after_hold_time_and_clears_with_hysteresis()
    {
        var alarm = new AlarmRule { Threshold = 80, ForSeconds = 2, Hysteresis = 5, Color = RgbwColor.Red, Effect = LightEffect.None, Notify = true };
        var config = new BacklightConfig { Mode = BacklightMode.Fixed, Color = RgbwColor.Blue, Alarms = [alarm] };
        var state = new BacklightState();
        var t0 = DateTimeOffset.UnixEpoch;

        Assert.Null(BacklightEngine.Compute(config, state, Inputs(85, 0.9, t0)).ActiveAlarm);
        var fired = BacklightEngine.Compute(config, state, Inputs(85, 0.9, t0.AddSeconds(2.1)));
        Assert.Same(alarm, fired.ActiveAlarm);
        Assert.Same(alarm, fired.RaisedAlarm);
        Assert.NotNull(BacklightEngine.Compute(config, state, Inputs(78, 0.8, t0.AddSeconds(3))).ActiveAlarm);
        Assert.Null(BacklightEngine.Compute(config, state, Inputs(74, 0.7, t0.AddSeconds(4))).ActiveAlarm);
    }

    [Fact]
    public void Colors_round_trip_through_json()
    {
        var json = JsonSerializer.Serialize(new RgbwColor(255, 128, 0, 32), Json.Options);
        Assert.Equal("\"#FF8000+20\"", json);
        Assert.Equal(new RgbwColor(255, 128, 0, 32), JsonSerializer.Deserialize<RgbwColor>(json, Json.Options));
    }

    [Fact]
    public void Easing_for_transition() =>
        Assert.Equal(new Easing(4, 20), Easing.ForTransition(500));
}

public class ConfigJsonTests
{
    [Fact]
    public void Polymorphic_layers_and_conditions_round_trip()
    {
        var config = new AppConfig
        {
            Profiles =
            [
                new Profile
                {
                    Name = "Gaming",
                    Conditions = [new ProcessRunningCondition { Processes = ["eldenring.exe"] }],
                    Dials =
                    [
                        new DialAssignment
                        {
                            Slot = 1,
                            Pages = [new DialPage { Face = new FaceConfig { Custom = new FaceDocument { Layers = [new TextLayer { Text = "{value}" }, new ScaleLayer()] } } }],
                        },
                    ],
                },
            ],
        };

        var json = JsonSerializer.Serialize(config, Json.Options);
        var back = JsonSerializer.Deserialize<AppConfig>(json, Json.Options)!;
        var layers = back.Profiles[0].Dials[0].Pages[0].Face.Custom!.Layers;
        Assert.IsType<TextLayer>(layers[0]);
        Assert.IsType<ScaleLayer>(layers[1]);
        Assert.IsType<ProcessRunningCondition>(back.Profiles[0].Conditions[0]);
        Assert.Contains("\"type\": \"text\"", json);
    }
}
