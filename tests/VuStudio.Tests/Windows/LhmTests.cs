using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.Lhm;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Windows;

public class LhmMappingTests
{
    [Theory]
    [InlineData(SensorType.Voltage, "voltage", "V", 1)]
    [InlineData(SensorType.Current, "current", "A", 1)]
    [InlineData(SensorType.Power, "power", "W", 1)]
    [InlineData(SensorType.Clock, "clock", "MHz", 1)]
    [InlineData(SensorType.Temperature, "temperature", "°C", 1)]
    [InlineData(SensorType.Load, "load", "%", 1)]
    [InlineData(SensorType.Frequency, "frequency", "Hz", 1)]
    [InlineData(SensorType.Fan, "fan", "RPM", 1)]
    [InlineData(SensorType.Flow, "flow", "L/h", 1)]
    [InlineData(SensorType.Control, "level", "%", 1)]
    [InlineData(SensorType.Level, "level", "%", 1)]
    [InlineData(SensorType.Factor, "factor", "", 1)]
    [InlineData(SensorType.Data, "data", "B", 1073741824d)]
    [InlineData(SensorType.SmallData, "data", "B", 1048576d)]
    [InlineData(SensorType.Throughput, "throughput", "B/s", 1)]
    [InlineData(SensorType.TimeSpan, "duration", "s", 1)]
    [InlineData(SensorType.Timing, "latency", "ms", 1e-6)]
    [InlineData(SensorType.Energy, "energy", "Wh", 1e-3)]
    [InlineData(SensorType.Noise, "noise", "dB", 1)]
    [InlineData(SensorType.Humidity, "humidity", "%", 1)]
    public void Maps_sensor_types_to_kind_unit_and_scale(SensorType type, string kind, string unit, double factor)
    {
        var spec = LhmMapping.Describe(type)!;

        Assert.Equal(kind, spec.Kind);
        Assert.Equal(unit, spec.Unit);
        Assert.Equal(factor, spec.Factor);
    }

    [Fact]
    public void Converts_binary_data_energy_and_timing()
    {
        Assert.Equal(16d * 1024 * 1024 * 1024, LhmMapping.Describe(SensorType.Data)!.Convert(16));
        Assert.Equal(8d * 1024 * 1024, LhmMapping.Describe(SensorType.SmallData)!.Convert(8));
        Assert.Equal(45.5, LhmMapping.Describe(SensorType.Energy)!.Convert(45_500), 9);
        Assert.Equal(0.0004, LhmMapping.Describe(SensorType.Timing)!.Convert(400), 9);
    }

    [Fact]
    public void Every_sensor_type_is_mapped_or_deliberately_skipped()
    {
        var unmapped = Enum.GetValues<SensorType>().Where(t => LhmMapping.Describe(t) is null).ToList();

        Assert.Equal([SensorType.Conductivity], unmapped); // no canonical unit exists for µS/cm
    }

    [Fact]
    public void Mapped_units_are_canonical()
    {
        string[] canonical = ["%", "°C", "MHz", "Hz", "W", "V", "A", "RPM", "B", "B/s", "ms", "s", "Wh", "L/h", "dB", ""];

        foreach (var type in Enum.GetValues<SensorType>())
        {
            if (LhmMapping.Describe(type) is { } spec)
            {
                Assert.Contains(spec.Unit, canonical);
            }
        }
    }

    [Theory]
    [InlineData(HardwareType.Cpu, SensorCategory.Cpu)]
    [InlineData(HardwareType.GpuNvidia, SensorCategory.Gpu)]
    [InlineData(HardwareType.GpuAmd, SensorCategory.Gpu)]
    [InlineData(HardwareType.GpuIntel, SensorCategory.Gpu)]
    [InlineData(HardwareType.Memory, SensorCategory.Memory)]
    [InlineData(HardwareType.Motherboard, SensorCategory.Motherboard)]
    [InlineData(HardwareType.SuperIO, SensorCategory.Motherboard)]
    [InlineData(HardwareType.Storage, SensorCategory.Storage)]
    [InlineData(HardwareType.Network, SensorCategory.Network)]
    [InlineData(HardwareType.Cooler, SensorCategory.Cooling)]
    [InlineData(HardwareType.EmbeddedController, SensorCategory.Cooling)]
    [InlineData(HardwareType.Psu, SensorCategory.Power)]
    [InlineData(HardwareType.PowerMonitor, SensorCategory.Power)]
    [InlineData(HardwareType.Battery, SensorCategory.Battery)]
    public void Maps_hardware_types_to_categories(HardwareType type, SensorCategory expected) =>
        Assert.Equal(expected, LhmMapping.Category(type));

    [Fact]
    public void Local_id_drops_the_leading_slash() =>
        Assert.Equal("amdcpu/0/temperature/2", LhmMapping.LocalId("/amdcpu/0/temperature/2"));

    [Theory]
    [InlineData("load", SensorCategory.Cpu, 0, 100)]
    [InlineData("temperature", SensorCategory.Cpu, 20, 100)]
    [InlineData("temperature", SensorCategory.Gpu, 20, 100)]
    [InlineData("temperature", SensorCategory.Storage, 20, 70)]
    [InlineData("temperature", SensorCategory.Motherboard, 0, 100)]
    [InlineData("clock", SensorCategory.Gpu, 0, 6000)]
    [InlineData("power", SensorCategory.Cpu, 0, 250)]
    [InlineData("power", SensorCategory.Gpu, 0, 450)]
    [InlineData("fan", SensorCategory.Cooling, 0, 3000)]
    public void Default_ranges_follow_the_spec(string kind, SensorCategory category, double min, double max) =>
        Assert.Equal((min, max), Common(kind, category));

    [Theory]
    [InlineData("power", SensorCategory.Motherboard)]
    [InlineData("voltage", SensorCategory.Cpu)]
    [InlineData("current", SensorCategory.Battery)]
    [InlineData("flow", SensorCategory.Cooling)]
    [InlineData("data", SensorCategory.Memory)]
    public void No_default_range_for_other_kinds(string kind, SensorCategory category) =>
        Assert.Equal((null, null), Common(kind, category));

    private static (double?, double?) Common(string kind, SensorCategory category) =>
        VuStudio.Sources.Windows.Common.SensorPresentation.Range(kind, category);

    [Fact]
    public void Every_icon_the_providers_can_return_is_in_the_icon_set()
    {
        foreach (var icon in VuStudio.Sources.Windows.Common.SensorPresentation.AllIcons())
        {
            Assert.True(IconSet.Contains(icon), $"unknown icon '{icon}'");
        }
    }
}

public class LhmPolicyTests
{
    private static readonly LhmTimings T = new();

    [Theory]
    [InlineData(LhmPhase.Updating, 5, WatchdogLevel.Ok)]
    [InlineData(LhmPhase.Updating, 10, WatchdogLevel.Degraded)]
    [InlineData(LhmPhase.Updating, 29, WatchdogLevel.Degraded)]
    [InlineData(LhmPhase.Updating, 30, WatchdogLevel.Fail)]
    [InlineData(LhmPhase.Opening, 29, WatchdogLevel.Ok)]
    [InlineData(LhmPhase.Opening, 31, WatchdogLevel.Degraded)]
    [InlineData(LhmPhase.Opening, 121, WatchdogLevel.Fail)]
    [InlineData(LhmPhase.Resetting, 31, WatchdogLevel.Degraded)]
    [InlineData(LhmPhase.Idle, 500, WatchdogLevel.Ok)]
    [InlineData(LhmPhase.Stopped, 500, WatchdogLevel.Ok)]
    public void Watchdog_levels(LhmPhase phase, int seconds, WatchdogLevel expected) =>
        Assert.Equal(expected, LhmHealth.Watchdog(phase, TimeSpan.FromSeconds(seconds), T));

    [Theory]
    [InlineData(1, 29, false)]
    [InlineData(1, 31, true)]
    [InlineData(10, 31, false)]
    [InlineData(10, 101, true)]
    public void Detects_a_pause_that_looks_like_sleep(double intervalSeconds, int gapSeconds, bool expected) =>
        Assert.Equal(expected, LhmHealth.ShouldReset(TimeSpan.FromSeconds(gapSeconds), TimeSpan.FromSeconds(intervalSeconds)));

    private static LhmStatus Eval(
        WatchdogLevel level = WatchdogLevel.Ok, bool first = true, bool elevated = true, int misses = 0,
        bool pawn = false, int failed = 0, string? error = null) =>
        LhmHealth.Evaluate(level, TimeSpan.FromSeconds(12), first, elevated, misses, pawn, failed, error, T);

    [Fact]
    public void Healthy_is_ok_and_first_pass_pending_is_starting()
    {
        Assert.Equal(new LhmStatus(ProviderHealth.Ok, null), Eval());
        Assert.Equal(new LhmStatus(ProviderHealth.Starting, null), Eval(first: false));
    }

    [Fact]
    public void Not_elevated_is_degraded_with_a_system_hint()
    {
        var status = Eval(elevated: false);

        Assert.Equal(ProviderHealth.Degraded, status.Health);
        Assert.Contains("SYSTEM", status.Message);
        Assert.Contains("administrator", status.Message);
    }

    [Fact]
    public void Cpu_temperatures_null_for_ten_polls_point_to_pawnio()
    {
        Assert.Equal(ProviderHealth.Ok, Eval(misses: 9).Health);

        var status = Eval(misses: 10);

        Assert.Equal(ProviderHealth.Degraded, status.Health);
        Assert.Contains("PawnIO", status.Message);
        Assert.Contains("https://github.com/namazso/PawnIO.Setup", status.Message);
        Assert.Contains("PawnIO", Eval(misses: 10, pawn: true).Message);
        Assert.DoesNotContain("is required", Eval(misses: 10, pawn: true).Message);
    }

    [Fact]
    public void A_slow_update_and_failures_are_reported_together()
    {
        var status = Eval(WatchdogLevel.Degraded, failed: 2, error: "boom");

        Assert.Equal(ProviderHealth.Degraded, status.Health);
        Assert.Contains("12 s", status.Message);
        Assert.Contains("2 hardware component(s) failed to update: boom", status.Message);
    }

    [Fact]
    public void Settings_use_defaults_and_clamp_the_interval()
    {
        var defaults = LhmSettings.From(new ProviderSettings(new JsonObject(), new LhmProviderFactory().Descriptor));

        Assert.Equal(TimeSpan.FromSeconds(1), defaults.Interval);
        Assert.True(defaults.Cpu && defaults.Gpu && defaults.Memory && defaults.Motherboard && defaults.Storage
            && defaults.Network && defaults.Psu && defaults.Battery);
        Assert.False(defaults.Controllers);
        Assert.Empty(defaults.SensorTypes);

        var custom = LhmSettings.From(new ProviderSettings(new JsonObject
        {
            ["interval"] = 0.01,
            ["controllers"] = true,
            ["gpu"] = false,
            ["sensorTypes"] = new JsonArray("Temperature", "FAN"),
        }, new LhmProviderFactory().Descriptor));

        Assert.Equal(TimeSpan.FromSeconds(0.25), custom.Interval);
        Assert.True(custom.Controllers);
        Assert.False(custom.Gpu);
        Assert.True(custom.Accepts(SensorType.Temperature, "temperature"));
        Assert.True(custom.Accepts(SensorType.Fan, "fan"));
        Assert.False(custom.Accepts(SensorType.Load, "load"));
        Assert.True(new LhmSettings().Accepts(SensorType.Load, "load"));
    }
}

public class LhmProviderTests
{
    private static readonly LhmTimings Fast = new()
    {
        WatchTick = TimeSpan.FromMilliseconds(20),
        DegradedAfter = TimeSpan.FromMilliseconds(250),
        FailAfter = TimeSpan.FromMilliseconds(700),
        DisposeWait = TimeSpan.FromMilliseconds(300),
        NullCpuTemperaturePolls = 3,
    };

    private static readonly LhmSettings FastSettings = new() { Interval = TimeSpan.FromMilliseconds(20) };

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Rig(FakeSession session, LhmSettings? settings = null, bool elevated = true, bool pawnIo = false)
        {
            Session = session;
            Provider = new LhmProvider("lhm", settings ?? FastSettings, Fast, new LhmEnvironment(() => elevated, () => pawnIo),
                () => session, NullLogger.Instance);
        }

        public FakeSession Session { get; }

        public LhmProvider Provider { get; }

        public SensorHub Hub { get; } = new();

        public RecordingStatus Status { get; } = new();

        public Task? Run { get; private set; }

        public Rig Start()
        {
            Run = Provider.RunAsync(Hub.CreateSink("lhm"), Status, _cts.Token);
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (Run is not null)
            {
                try
                {
                    await Run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // the tests that expect a fault assert it themselves
                }
            }

            await Provider.DisposeAsync();
            Session.Release();
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what}");
    }

    private static FakeHardware Cpu(params FakeSensor[] sensors) =>
        new("AMD Ryzen 7 7800X3D", HardwareType.Cpu, new Identifier("amdcpu", "0"), sensors);

    [Fact]
    public async Task Publishes_sensors_with_metadata_converted_values_and_stable_ids()
    {
        var cpu = Cpu(
            new FakeSensor("CPU Total", SensorType.Load, "load", "0", 42.5f),
            new FakeSensor("Core (Tctl/Tdie)", SensorType.Temperature, "temperature", "2", 61f));
        var gpu = new FakeHardware("NVIDIA GeForce RTX 4090", HardwareType.GpuNvidia, new Identifier("gpu-nvidia", "0"),
            new FakeSensor("GPU Core", SensorType.Load, "load", "0", 7f),
            new FakeSensor("GPU Memory Used", SensorType.SmallData, "smalldata", "1", 2048f));
        var mem = new FakeHardware("Total Memory", HardwareType.Memory, new Identifier("ram"),
            new FakeSensor("Memory Used", SensorType.Data, "data", "0", 12.5f));
        var battery = new FakeHardware("Battery", HardwareType.Battery, new Identifier("battery", "main"),
            new FakeSensor("Remaining Capacity", SensorType.Energy, "energy", "0", 40_000f));
        await using var rig = new Rig(new FakeSession(cpu, gpu, mem, battery)).Start();

        await Eventually(() => rig.Hub.GetNumber("lhm/ram/data/0") is not null, "first pass");

        var load = rig.Hub.Get("lhm/amdcpu/0/load/0")!;
        Assert.Equal(42.5, load.Value);
        Assert.Equal("CPU Total", load.Info.Name);
        Assert.Equal("AMD Ryzen 7 7800X3D", load.Info.Device);
        Assert.Equal(SensorCategory.Cpu, load.Info.Category);
        Assert.Equal(SensorKinds.Load, load.Info.Kind);
        Assert.Equal((0, 100), (load.Info.Min, load.Info.Max));
        Assert.Equal("cpu-64-bit", load.Info.Icon);
        Assert.Equal("lhm", load.Info.ProviderId);
        var temp = rig.Hub.Get("lhm/amdcpu/0/temperature/2")!;
        Assert.Equal((20, 100), (temp.Info.Min, temp.Info.Max));
        Assert.Equal(SensorCategory.Gpu, rig.Hub.Get("lhm/gpu-nvidia/0/load/0")!.Info.Category);
        Assert.Equal(2048d * 1024 * 1024, rig.Hub.GetNumber("lhm/gpu-nvidia/0/smalldata/1"));
        Assert.Equal(12.5 * 1024 * 1024 * 1024, rig.Hub.GetNumber("lhm/ram/data/0"));
        Assert.Equal(40, rig.Hub.GetNumber("lhm/battery/main/energy/0"));
        Assert.Equal(SensorCategory.Battery, rig.Hub.Get("lhm/battery/main/energy/0")!.Info.Category);
        Assert.True(rig.Session.Opened);
        await Eventually(() => rig.Status.Last is { Health: ProviderHealth.Ok }, "ok report");
    }

    [Fact]
    public async Task Keeps_lhm_history_off_for_every_sensor_including_unpublished_ones()
    {
        var sensors = new[]
        {
            new FakeSensor("CPU Total", SensorType.Load, "load", "0", 1f),
            new FakeSensor("Coolant conductivity", SensorType.Conductivity, "conductivity", "0", 500f),
        };
        await using var rig = new Rig(new FakeSession(Cpu(sensors))).Start();

        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") is not null, "first pass");

        Assert.All(sensors, s => Assert.Equal(TimeSpan.Zero, s.ValuesTimeWindow));
        Assert.Null(rig.Hub.Get("lhm/amdcpu/0/conductivity/0"));
    }

    [Fact]
    public async Task Honours_the_sensor_type_filter()
    {
        var cpu = Cpu(
            new FakeSensor("CPU Total", SensorType.Load, "load", "0", 1f),
            new FakeSensor("Core", SensorType.Temperature, "temperature", "0", 50f));
        var settings = FastSettings with { SensorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "temperature" } };
        await using var rig = new Rig(new FakeSession(cpu), settings).Start();

        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/temperature/0") is not null, "first pass");

        Assert.Null(rig.Hub.Get("lhm/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task Null_readings_invalidate_and_recover()
    {
        var load = new FakeSensor("CPU Total", SensorType.Load, "load", "0", 10f);
        await using var rig = new Rig(new FakeSession(Cpu(load))).Start();
        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") == 10, "value");

        load.Value = null;
        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") is null, "invalidation");
        load.Value = 11f;

        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") == 11, "recovery");
    }

    [Fact]
    public async Task Removes_sensors_that_stay_gone_for_several_passes()
    {
        var a = new FakeSensor("A", SensorType.Load, "load", "0", 1f);
        var b = new FakeSensor("B", SensorType.Load, "load", "1", 2f);
        var cpu = Cpu(a, b);
        await using var rig = new Rig(new FakeSession(cpu)).Start();
        await Eventually(() => rig.Hub.Get("lhm/amdcpu/0/load/1") is not null, "both sensors");

        cpu.Sensors = [a];

        await Eventually(() => rig.Hub.Get("lhm/amdcpu/0/load/1") is null, "removal");
        Assert.NotNull(rig.Hub.Get("lhm/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task One_failing_hardware_does_not_stop_the_others()
    {
        var bad = new FakeHardware("Broken", HardwareType.Storage, new Identifier("nvme", "0"),
            new FakeSensor("Temperature", SensorType.Temperature, "temperature", "0", 30f)) { FailUpdate = true };
        var good = Cpu(new FakeSensor("CPU Total", SensorType.Load, "load", "0", 3f));
        await using var rig = new Rig(new FakeSession(bad, good)).Start();

        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") == 3, "good hardware");
        await Eventually(() => rig.Status.Last is { Health: ProviderHealth.Degraded }, "degraded report");

        Assert.Contains("1 hardware component(s) failed to update", rig.Status.Last!.Value.Message);
        Assert.Null(rig.Hub.GetNumber("lhm/nvme/0/temperature/0"));
    }

    [Fact]
    public async Task Reports_degraded_when_not_elevated()
    {
        await using var rig = new Rig(new FakeSession(Cpu(new FakeSensor("CPU Total", SensorType.Load, "load", "0", 1f))), elevated: false).Start();

        await Eventually(() => rig.Status.Last is { Health: ProviderHealth.Degraded }, "degraded");

        Assert.Contains("SYSTEM", rig.Status.Last!.Value.Message);
        Assert.NotNull(rig.Hub.GetNumber("lhm/amdcpu/0/load/0")); // still publishes
    }

    [Fact]
    public async Task Reports_pawnio_when_cpu_temperatures_stay_null()
    {
        var cpu = Cpu(
            new FakeSensor("CPU Package", SensorType.Temperature, "temperature", "0", null),
            new FakeSensor("CPU Total", SensorType.Load, "load", "0", 5f));
        await using var rig = new Rig(new FakeSession(cpu), pawnIo: false);

        rig.Start();
        await Eventually(() => rig.Status.Last is { Health: ProviderHealth.Degraded }, "pawnio report");

        Assert.Contains("PawnIO driver is required", rig.Status.Last!.Value.Message);
    }

    [Fact]
    public async Task A_stuck_update_goes_degraded_then_throws_and_is_abandoned_cleanly()
    {
        var session = new FakeSession(Cpu(new FakeSensor("CPU Total", SensorType.Load, "load", "0", 1f)));
        await using var rig = new Rig(session).Start();
        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") is not null, "first pass");

        session.Block();

        await Eventually(() => rig.Status.Reports.Any(r => r.Health == ProviderHealth.Degraded && r.Message!.Contains("not responded")), "degraded");
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => rig.Run!);
        Assert.Contains("did not respond", ex.Message);

        session.Release();
        await rig.Provider.DisposeAsync();
        await Eventually(() => session.Closed, "close on the worker thread");
    }

    [Fact]
    public async Task A_fault_while_opening_is_rethrown_so_the_manager_restarts()
    {
        var session = new FakeSession { OpenException = new InvalidOperationException("driver exploded") };
        await using var rig = new Rig(session).Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Run!);

        Assert.Equal("driver exploded", ex.Message);
    }

    [Fact]
    public async Task Dispose_closes_the_computer_on_the_worker_thread()
    {
        var session = new FakeSession(Cpu(new FakeSensor("CPU Total", SensorType.Load, "load", "0", 1f)));
        var rig = new Rig(session).Start();
        await Eventually(() => rig.Hub.GetNumber("lhm/amdcpu/0/load/0") is not null, "first pass");
        var workerThread = session.UpdateThreadId;

        await rig.DisposeAsync();

        Assert.True(session.Closed);
        Assert.Equal(workerThread, session.CloseThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, workerThread);
    }
}

internal sealed class FakeSession : ILhmSession
{
    private readonly List<IHardware> _hardware;
    private readonly ManualResetEventSlim _gate = new(true);

    public FakeSession(params IHardware[] hardware) => _hardware = [.. hardware];

    public Exception? OpenException { get; init; }

    public bool Opened { get; private set; }

    public volatile bool Closed;

    public int UpdateThreadId { get; private set; }

    public int CloseThreadId { get; private set; }

    public void Block() => _gate.Reset();

    public void Release() => _gate.Set();

    public void Open()
    {
        if (OpenException is not null)
        {
            throw OpenException;
        }

        Opened = true;
    }

    public void Accept(IVisitor visitor)
    {
        _gate.Wait();
        UpdateThreadId = Environment.CurrentManagedThreadId;
        foreach (var hardware in _hardware.ToArray())
        {
            hardware.Accept(visitor);
        }
    }

    public void Reset()
    {
    }

    public void Close()
    {
        CloseThreadId = Environment.CurrentManagedThreadId;
        Closed = true;
    }
}

internal sealed class FakeHardware : IHardware
{
    public FakeHardware(string name, HardwareType type, Identifier identifier, params FakeSensor[] sensors)
    {
        Name = name;
        HardwareType = type;
        Identifier = identifier;
        Sensors = sensors;
        foreach (var s in sensors)
        {
            s.Owner = this;
        }
    }

    public bool FailUpdate { get; init; }

    public string Name { get; set; }

    public HardwareType HardwareType { get; }

    public Identifier Identifier { get; }

    public IHardware? Parent => null;

    public ISensor[] Sensors { get; set; }

    public IHardware[] SubHardware { get; set; } = [];

    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

#pragma warning disable CS0067
    public event SensorEventHandler? SensorAdded;

    public event SensorEventHandler? SensorRemoved;
#pragma warning restore CS0067

    public void Update()
    {
        if (FailUpdate)
        {
            throw new InvalidOperationException("update failed");
        }
    }

    public string GetReport() => "";

    public void Accept(IVisitor visitor) => visitor.VisitHardware(this);

    public void Traverse(IVisitor visitor)
    {
        foreach (var sub in SubHardware)
        {
            sub.Accept(visitor);
        }
    }
}

internal sealed class FakeSensor : ISensor
{
    public FakeSensor(string name, SensorType type, string typeSegment, string index, float? value)
    {
        Name = name;
        SensorType = type;
        TypeSegment = typeSegment;
        IndexSegment = index;
        Value = value;
        ValuesTimeWindow = TimeSpan.FromDays(1); // LHM's default
    }

    public FakeHardware? Owner { get; set; }

    private string TypeSegment { get; }

    private string IndexSegment { get; }

    public IHardware Hardware => Owner!;

    public Identifier Identifier => new(Owner!.Identifier, TypeSegment, IndexSegment);

    public string Name { get; set; }

    public SensorType SensorType { get; }

    public float? Value { get; set; }

    public float? Min => Value;

    public float? Max => Value;

    public int Index => int.TryParse(IndexSegment, out var i) ? i : 0;

    public bool IsDefaultHidden => false;

    public IControl? Control => null;

    public IReadOnlyList<IParameter> Parameters => [];

    public IEnumerable<SensorValue> Values => [];

    public TimeSpan ValuesTimeWindow { get; set; }

    public void ResetMin()
    {
    }

    public void ResetMax()
    {
    }

    public void ClearValues()
    {
    }

    public void Accept(IVisitor visitor) => visitor.VisitSensor(this);

    public void Traverse(IVisitor visitor)
    {
    }
}
