using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Demo;

namespace VuStudio.Tests.Sources;

public class SplitMix64Tests
{
    [Fact]
    public void Sequence_is_fixed_by_the_algorithm()
    {
        // Reference values of SplitMix64 seeded with 0 (from the published reference implementation).
        var random = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, random.NextUInt64());
    }

    [Fact]
    public void Same_seed_gives_the_same_sequence_and_different_seeds_differ()
    {
        var a = new SplitMix64(7);
        var b = new SplitMix64(7);
        var c = new SplitMix64(8);
        var first = Enumerable.Range(0, 20).Select(_ => a.NextDouble()).ToArray();
        Assert.Equal(first, Enumerable.Range(0, 20).Select(_ => b.NextDouble()).ToArray());
        Assert.NotEqual(first, Enumerable.Range(0, 20).Select(_ => c.NextDouble()).ToArray());
    }

    [Fact]
    public void Doubles_are_in_the_unit_interval_and_ranges_are_respected()
    {
        var random = new SplitMix64(123);
        for (var i = 0; i < 10_000; i++)
        {
            var d = random.NextDouble();
            Assert.InRange(d, 0, 0.9999999999999999);
            Assert.InRange(random.NextRange(-5, 5), -5, 5);
        }
    }

    [Fact]
    public void Gaussian_has_roughly_zero_mean_and_unit_deviation()
    {
        var random = new SplitMix64(99);
        var samples = Enumerable.Range(0, 20_000).Select(_ => random.NextGaussian()).ToArray();
        var mean = samples.Average();
        var deviation = Math.Sqrt(samples.Sum(s => (s - mean) * (s - mean)) / samples.Length);
        Assert.InRange(mean, -0.05, 0.05);
        Assert.InRange(deviation, 0.95, 1.05);
    }
}

public class DemoSignalsTests
{
    private static List<DemoFrame> Simulate(int seed, double period, int steps, double dt = 0.1)
    {
        var signals = new DemoSignals(seed, period);
        return Enumerable.Range(0, steps).Select(_ => signals.Step(dt)).ToList();
    }

    [Fact]
    public void Same_seed_is_deterministic_and_different_seeds_differ()
    {
        Assert.Equal(Simulate(1, 10, 300), Simulate(1, 10, 300));
        Assert.NotEqual(Simulate(1, 10, 300), Simulate(2, 10, 300));
    }

    [Fact]
    public void Every_signal_stays_in_its_range()
    {
        foreach (var frame in Simulate(5, 7, 20_000))
        {
            Assert.InRange(frame.Sine, 0, 100);
            Assert.InRange(frame.Sawtooth, 0, 100);
            Assert.True(frame.Square is 0 or 100, "square must be 0 or 100");
            Assert.InRange(frame.Temperature, DemoSignals.TemperatureMin, DemoSignals.TemperatureMax);
            Assert.InRange(frame.NetworkRate, 0, DemoSignals.NetworkMax);
            Assert.InRange(frame.Audio, 0, 100);
        }
    }

    [Fact]
    public void Waves_follow_the_period()
    {
        var signals = new DemoSignals(1, 10);
        var start = signals.Step(0);
        Assert.Equal(50, start.Sine, 6);
        Assert.Equal(0, start.Sawtooth, 6);
        Assert.Equal(100, start.Square);

        DemoFrame frame = default;
        for (var i = 0; i < 25; i++)
        {
            frame = signals.Step(0.1);
        }

        // A quarter of the way through the period.
        Assert.Equal(100, frame.Sine, 4);
        Assert.Equal(25, frame.Sawtooth, 4);
        Assert.Equal(100, frame.Square);

        for (var i = 0; i < 50; i++)
        {
            frame = signals.Step(0.1);
        }

        // Three quarters.
        Assert.Equal(0, frame.Sine, 4);
        Assert.Equal(75, frame.Sawtooth, 4);
        Assert.Equal(0, frame.Square);

        for (var i = 0; i < 50; i++)
        {
            frame = signals.Step(0.1);
        }

        // A full period later it is a quarter of the way through again. (Not checked exactly at the end of
        // the period: the sawtooth jumps there, and floating-point error decides on which side it lands.)
        Assert.Equal(100, frame.Sine, 4);
        Assert.Equal(25, frame.Sawtooth, 4);
        Assert.Equal(100, frame.Square);
    }

    [Fact]
    public void Period_setting_scales_the_waves()
    {
        var fast = new DemoSignals(1, 2);
        fast.Step(0);
        DemoFrame frame = default;
        for (var i = 0; i < 5; i++)
        {
            frame = fast.Step(0.1);
        }

        Assert.Equal(100, frame.Sine, 4); // a quarter of a 2 s period is 0.5 s
    }

    [Fact]
    public void A_bad_period_falls_back_to_a_sane_one()
    {
        foreach (var period in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            var frame = new DemoSignals(1, period).Step(0.1);
            Assert.True(double.IsFinite(frame.Sine));
        }
    }

    [Fact]
    public void Long_pauses_are_clamped_to_one_second()
    {
        var a = new DemoSignals(3, 10);
        var b = new DemoSignals(3, 10);
        Assert.Equal(a.Step(1), b.Step(3600));
    }

    [Fact]
    public void Temperature_is_slow()
    {
        var frames = Simulate(11, 10, 6_000);
        var largestStep = 0.0;
        for (var i = 1; i < frames.Count; i++)
        {
            largestStep = Math.Max(largestStep, Math.Abs(frames[i].Temperature - frames[i - 1].Temperature));
        }

        Assert.True(largestStep < 1.5, $"temperature moved {largestStep:0.00} °C in 100 ms");

        // ...but it does wander across a good part of the range.
        Assert.True(frames.Max(f => f.Temperature) - frames.Min(f => f.Temperature) > 15);
    }

    [Fact]
    public void Network_is_mostly_quiet_with_bursts()
    {
        var rates = Simulate(21, 10, 6_000).Select(f => f.NetworkRate).Order().ToArray();
        var median = rates[rates.Length / 2];
        Assert.True(median < 200_000, $"median {median:0} B/s should be background chatter");
        Assert.True(rates[^1] > 1_000_000, $"peak {rates[^1]:0} B/s should reach megabytes per second");
    }

    [Fact]
    public void Audio_moves_fast_and_has_peaks()
    {
        var levels = Simulate(31, 10, 1_200).Select(f => f.Audio).ToArray();
        Assert.True(levels.Max() > 75, "should peak");
        Assert.True(levels.Min() < 45, "should fall back between peaks");
        var jumps = levels.Zip(levels.Skip(1), (a, b) => Math.Abs(b - a)).Count(d => d > 5);
        Assert.True(jumps > 50, "a level that changes by more than 5 points between 100 ms ticks is 'fast'");
    }
}

public class DemoProviderTests
{
    [Fact]
    public async Task Publishes_all_six_signals_with_metadata()
    {
        var hub = new SensorHub();
        var provider = new DemoProviderFactory().Create(TestConfig.For("demo", """{"interval":0.05,"seed":7}"""), TestServices.Empty());
        string[] ids = ["sine", "sawtooth", "square", "temperature", "network", "audio"];

        await ProviderRunner.RunUntilAsync(provider, hub, () => ids.All(id => hub.GetNumber("demo/" + id) is not null));

        var temperature = hub.Get("demo/temperature")!.Info;
        Assert.Equal(Units.Celsius, temperature.Unit);
        Assert.Equal(SensorKinds.Temperature, temperature.Kind);
        Assert.Equal(30, temperature.Min);
        Assert.Equal(95, temperature.Max);

        var network = hub.Get("demo/network")!.Info;
        Assert.Equal(Units.BytesPerSecond, network.Unit);
        Assert.Equal(SensorKinds.Throughput, network.Kind);

        foreach (var id in ids)
        {
            var info = hub.Get("demo/" + id)!.Info;
            Assert.Equal("Demo", info.Device);
            Assert.Equal(SensorCategory.Custom, info.Category);
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.Icon));
            Assert.NotNull(info.Min);
            Assert.NotNull(info.Max);
        }
    }

    [Fact]
    public async Task Values_keep_changing()
    {
        var hub = new SensorHub();
        var provider = new DemoProviderFactory().Create(TestConfig.For("demo", """{"interval":0.05}"""), TestServices.Empty());
        var seen = new HashSet<double>();

        await ProviderRunner.RunUntilAsync(provider, hub, () =>
        {
            if (hub.GetNumber("demo/audio") is { } audio)
            {
                seen.Add(audio);
            }

            return seen.Count >= 5;
        });

        Assert.True(seen.Count >= 5);
    }

    [Fact]
    public async Task The_wave_period_follows_real_elapsed_time()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var hub = new SensorHub();
        var sink = hub.CreateSink("demo");
        var provider = new DemoProviderFactory().Create(
            TestConfig.For("demo", """{"period":10}"""),
            TestServices.With(s => s.AddSingleton<TimeProvider>(time)));

        await ProviderDriver.InitializeAsync(provider, sink);
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(50, hub.GetNumber("demo/sine")!.Value, 4);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(0.5));
            await ProviderDriver.PollAsync(provider, sink);
        }

        // 2.5 s of elapsed time, however irregular the real ticks were.
        Assert.Equal(100, hub.GetNumber("demo/sine")!.Value, 4);
        Assert.Equal(25, hub.GetNumber("demo/sawtooth")!.Value, 4);
    }
}
