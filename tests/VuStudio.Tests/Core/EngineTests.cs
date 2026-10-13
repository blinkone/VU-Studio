using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Tests.Core;

public sealed class EngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vustudio-tests", Guid.NewGuid().ToString("n"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly SensorHub _hub;
    private readonly FakeOutput _output = new();
    private readonly FakeRenderer _renderer = new();
    private readonly ISensorSink _sink;

    public EngineTests()
    {
        _hub = new SensorHub(_time);
        _sink = _hub.CreateSink("p");
        _sink.Define(new SensorInfo { Id = "cpu", Name = "CPU load", Unit = Units.Percent, Min = 0, Max = 100, Category = SensorCategory.Cpu });
        _output.DialList = [new DialInfo("AAA", 0, true)];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private ConfigStore Store(Action<AppConfig> setup)
    {
        var config = new AppConfig();
        config.Hardware.Dials.Add(new DialSettings { Uid = "AAA", Slot = 1 });
        config.Profiles.Add(new Profile
        {
            Id = "default",
            Name = "Default",
            Activation = ProfileActivation.Always,
            Dials = [new DialAssignment { Slot = 1, Pages = [Page("cpu-page", "p/cpu")] }],
        });
        setup(config);
        return new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger<ConfigStore>.Instance, () => config);
    }

    private static DialPage Page(string id, string sensor, int minRefresh = 60) => new()
    {
        Id = id,
        Value = new ValueBinding { SensorId = sensor, AttackMs = 0, ReleaseMs = 0 },
        Light = new BacklightConfig { Mode = BacklightMode.Fixed, Color = RgbwColor.Red, Brightness = 100 },
        Face = new FaceConfig { MinRefreshSeconds = minRefresh },
    };

    private DialEngine Engine(ConfigStore store) =>
        new(store, _hub, _output, _renderer, NullLogger<DialEngine>.Instance, time: _time);

    private void Step(DialEngine engine, int seconds = 0, int ticks = 1)
    {
        for (var i = 0; i < ticks; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(seconds * 1000.0 / ticks));
            engine.Tick();
        }
    }

    [Fact]
    public void Needle_and_light_follow_the_bound_sensor()
    {
        var engine = Engine(Store(_ => { }));
        _sink.Publish("cpu", 42);
        Step(engine);

        Assert.Equal(42, _output.Needles["AAA"], 3);
        Assert.Equal(new LedLevels(100, 0, 0, 0), _output.Lights["AAA"]);
        var state = Assert.Single(engine.Snapshot.Dials);
        Assert.Equal("CPU load", state.Title);
        Assert.Equal(42, state.Value);
    }

    [Fact]
    public void Offline_sensor_parks_the_needle()
    {
        var engine = Engine(Store(_ => { }));
        _sink.Publish("cpu", 80);
        Step(engine);
        _sink.Invalidate("cpu");
        Step(engine, 1);
        Assert.Equal(0, _output.Needles["AAA"], 3);
    }

    [Fact]
    public void Face_updates_are_rate_limited_but_page_changes_are_immediate()
    {
        var engine = Engine(Store(c =>
        {
            c.Profiles[0].Dials[0].RotationSeconds = 0;
            c.Profiles[0].Dials[0].Pages.Add(Page("second", "p/cpu"));
        }));
        _sink.Publish("cpu", 10);
        Step(engine);
        Assert.Equal(1, _output.FaceUploads);

        _sink.Publish("cpu", 11);
        Step(engine, 2);
        Assert.Equal(1, _output.FaceUploads);

        engine.Advance(1, +1);
        Step(engine);
        Assert.Equal(2, _output.FaceUploads);
        Assert.Equal(1, engine.Snapshot.Dials[0].PageIndex);

        _sink.Publish("cpu", 12);
        Step(engine, 61, ticks: 61);
        Assert.Equal(3, _output.FaceUploads);
    }

    [Fact]
    public void Pages_rotate_on_their_interval()
    {
        var engine = Engine(Store(c =>
        {
            c.Profiles[0].Dials[0].RotationSeconds = 10;
            c.Profiles[0].Dials[0].Pages.Add(Page("second", "p/cpu"));
        }));
        Step(engine);
        Assert.Equal(0, engine.Snapshot.Dials[0].PageIndex);
        Step(engine, 11, ticks: 11);
        Assert.Equal(1, engine.Snapshot.Dials[0].PageIndex);
    }

    [Fact]
    public void Sensor_condition_activates_a_higher_priority_profile()
    {
        var engine = Engine(Store(c => c.Profiles.Add(new Profile
        {
            Id = "gaming",
            Name = "Gaming",
            Priority = 10,
            Activation = ProfileActivation.Auto,
            Conditions = [new SensorCondition { SensorId = "p/game", Comparison = Comparison.Above, Value = 0.5 }],
            Dials = [new DialAssignment { Slot = 1, Pages = [Page("fps", "p/fps")] }],
        })));
        Step(engine);
        Assert.Equal("default", engine.Snapshot.ActiveProfileId);

        _sink.Publish("game", 1);
        Step(engine, 2);
        Assert.Equal("gaming", engine.Snapshot.ActiveProfileId);
        Assert.Equal("p/fps", engine.Snapshot.Dials[0].SensorId);
    }

    [Fact]
    public void Manual_profile_overrides_automatic_selection()
    {
        var engine = Engine(Store(c => c.Profiles.Add(new Profile { Id = "work", Name = "Work", Activation = ProfileActivation.Manual })));
        engine.SetManualProfile("work");
        Step(engine);
        Assert.Equal("work", engine.Snapshot.ActiveProfileId);
        Assert.Equal("p/cpu", engine.Snapshot.Dials[0].SensorId);
    }

    [Fact]
    public void Locking_the_session_switches_lights_off()
    {
        var engine = Engine(Store(c => c.Night.OnLock = NightAction.LightsOff));
        _sink.Publish("cpu", 50);
        var session = _hub.CreateSink(WellKnownSensors.SessionProvider);
        session.Publish("locked", 1);
        Step(engine, 2);
        Assert.True(_output.Lights["AAA"].IsOff);
        Assert.Equal(NightAction.LightsOff, engine.Snapshot.Night);
        Assert.Equal(50, _output.Needles["AAA"], 3);
    }

    [Fact]
    public void Unknown_dials_get_the_next_free_slot()
    {
        var store = Store(_ => { });
        var engine = Engine(store);
        _output.DialList = [new DialInfo("AAA", 0, true), new DialInfo("BBB", 1, true)];
        Step(engine, 3);
        Assert.Contains(store.Current.Hardware.Dials, d => d.Uid == "BBB" && d.Slot == 2);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeOutput : IDialOutput
    {
        public IReadOnlyList<DialInfo> DialList { get; set; } = [];

        public Dictionary<string, double> Needles { get; } = [];

        public Dictionary<string, LedLevels> Lights { get; } = [];

        public int FaceUploads { get; private set; }

        public HubStatus Status => new(HubConnectionState.Connected, "FAKE", null, true);

        public IReadOnlyList<DialInfo> Dials => DialList;

        public event Action? Changed;

        public void SetNeedle(string uid, double percent) => Needles[uid] = percent;

        public void SetBacklight(string uid, LedLevels levels) => Lights[uid] = levels;

        public void SetBacklightTransition(string uid, int milliseconds)
        {
        }

        public void SetNeedleEasing(string uid, Easing easing)
        {
        }

        public void SetFace(string uid, MonoBitmap face) => FaceUploads++;

        public void Invalidate() => Changed?.Invoke();

        public Task IdentifyAsync(string uid, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RescanAsync(bool provision, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> SetRawAsync(string uid, int raw, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CalibrateAsync(string uid, CalibrationPoint point, int raw, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>Encodes the rendered "{title}|{value}" text into pixels so content changes change the hash.</summary>
    private sealed class FakeRenderer : IFaceRenderer
    {
        public IReadOnlyList<ThemeInfo> Themes => [];

        public IReadOnlyList<FontInfo> Fonts => [];

        public IReadOnlyList<IconInfo> Icons => [];

        public FaceDocument BuildDocument(FaceConfig config, FaceRenderContext context) => config.Custom ?? new FaceDocument();

        public MonoBitmap Render(FaceDocument document, FaceRenderContext context)
        {
            var bitmap = new MonoBitmap();
            var text = TextTemplate.Render("{title}|{value}", context);
            for (var i = 0; i < text.Length; i++)
            {
                bitmap[i, text[i] % 144] = false;
            }

            return bitmap;
        }

        public byte[] EncodePng(MonoBitmap bitmap, int scale = 1, RgbwColor? tint = null) => [];
    }
}
