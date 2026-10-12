using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Demo;

/// <summary>
/// Publishes synthetic signals for trying out faces, needles and lights without real hardware.
/// </summary>
/// <remarks>Sensor ids: <c>sine</c>, <c>sawtooth</c>, <c>square</c>, <c>temperature</c>, <c>network</c>, <c>audio</c>.</remarks>
internal sealed class DemoProvider : PollingSensorProvider
{
    private const string Device = "Demo";

    private readonly DemoSignals _signals;
    private readonly TimeProvider _time;
    private long? _lastTimestamp;

    public DemoProvider(string id, ProviderSettings settings, TimeProvider time, ILogger logger)
        : base(id, settings.GetSeconds("interval", 0.1), logger)
    {
        _time = time;
        _signals = new DemoSignals(settings.GetInt("seed", 42), settings.GetDouble("period", 10));
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        Define(sink, "sine", "Sine wave", SensorKinds.Percent, Units.Percent, 0, 100, "sine-wave");
        Define(sink, "sawtooth", "Sawtooth wave", SensorKinds.Percent, Units.Percent, 0, 100, "chart-line");
        Define(sink, "square", "Square wave", SensorKinds.Percent, Units.Percent, 0, 100, "chart-timeline-variant");
        Define(sink, "temperature", "Temperature", SensorKinds.Temperature, Units.Celsius, DemoSignals.TemperatureMin, DemoSignals.TemperatureMax, "thermometer");
        Define(sink, "network", "Network rate", SensorKinds.Throughput, Units.BytesPerSecond, 0, DemoSignals.NetworkMax, "swap-vertical");
        Define(sink, "audio", "Audio level", SensorKinds.Level, Units.Percent, 0, 100, "waveform");
        return Task.CompletedTask;
    }

    protected override Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        // Advance by the time that really passed so the waves keep their period even when ticks jitter.
        var now = _time.GetTimestamp();
        var elapsed = _lastTimestamp is { } last ? _time.GetElapsedTime(last, now).TotalSeconds : 0;
        _lastTimestamp = now;

        var frame = _signals.Step(elapsed);
        sink.Publish("sine", frame.Sine);
        sink.Publish("sawtooth", frame.Sawtooth);
        sink.Publish("square", frame.Square);
        sink.Publish("temperature", frame.Temperature);
        sink.Publish("network", frame.NetworkRate);
        sink.Publish("audio", frame.Audio);
        return Task.CompletedTask;
    }

    private static void Define(ISensorSink sink, string id, string name, string kind, string unit, double min, double max, string icon) =>
        sink.Define(new SensorInfo
        {
            Id = id,
            Name = name,
            Device = Device,
            Category = SensorCategory.Custom,
            Kind = kind,
            Unit = unit,
            Min = min,
            Max = max,
            Icon = icon,
        });
}
