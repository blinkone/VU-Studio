namespace VuStudio.Sources.Demo;

/// <summary>One sample of every demo signal.</summary>
/// <param name="Sine">Sine wave, 0-100, starting at 50 and rising.</param>
/// <param name="Sawtooth">Rising ramp, 0 to 100, then back to 0.</param>
/// <param name="Square">100 for the first half of the period, 0 for the second.</param>
/// <param name="Temperature">Slow random walk between 30 and 95 °C.</param>
/// <param name="NetworkRate">Bursty traffic in bytes per second, 0 to <see cref="DemoSignals.NetworkMax"/>.</param>
/// <param name="Audio">Fast "music-like" level 0-100 with peaks and a slower fall-off.</param>
internal readonly record struct DemoFrame(
    double Sine,
    double Sawtooth,
    double Square,
    double Temperature,
    double NetworkRate,
    double Audio);

/// <summary>
/// Generates the synthetic signals of the demo provider. The generator advances by explicit time steps
/// and draws its randomness from a seeded <see cref="SplitMix64"/>, so for a given seed, period and
/// sequence of steps the output is always the same.
/// </summary>
internal sealed class DemoSignals
{
    public const double TemperatureMin = 30;
    public const double TemperatureMax = 95;

    /// <summary>1 Gbit/s expressed in bytes per second.</summary>
    public const double NetworkMax = 125_000_000;

    private readonly SplitMix64 _random;
    private readonly double _period;
    private double _time;

    private double _temperature = 55;
    private double _temperatureTarget = 55;
    private double _temperatureTargetLeft;

    private double _burstLeft;
    private double _burstDuration = 1;
    private double _burstPeak;

    private double _audio;
    private double _transient;

    public DemoSignals(int seed, double periodSeconds)
    {
        _random = new SplitMix64(seed);
        _period = double.IsFinite(periodSeconds) && periodSeconds > 0 ? periodSeconds : 10;
    }

    /// <summary>Advances the simulation by <paramref name="seconds"/> (clamped to 0-1 s) and returns the new sample.</summary>
    public DemoFrame Step(double seconds)
    {
        var dt = Math.Clamp(seconds, 0, 1);
        _time += dt;
        var phase = (_time % _period) / _period;
        return new DemoFrame(
            Sine: 50 + (50 * Math.Sin(2 * Math.PI * phase)),
            Sawtooth: 100 * phase,
            Square: phase < 0.5 ? 100 : 0,
            Temperature: StepTemperature(dt),
            NetworkRate: StepNetwork(dt),
            Audio: StepAudio(dt));
    }

    /// <summary>
    /// A drifting target that is re-drawn every 15-45 s, followed by a first-order lag with a 12 s time
    /// constant plus a little noise: slow ramps with a natural wobble, like a CPU temperature.
    /// </summary>
    private double StepTemperature(double dt)
    {
        _temperatureTargetLeft -= dt;
        if (_temperatureTargetLeft <= 0)
        {
            _temperatureTarget = _random.NextRange(38, 88);
            _temperatureTargetLeft = _random.NextRange(15, 45);
        }

        _temperature += ((_temperatureTarget - _temperature) * (1 - Math.Exp(-dt / 12))) + (_random.NextGaussian() * 0.25 * Math.Sqrt(dt));
        _temperature = Math.Clamp(_temperature, TemperatureMin, TemperatureMax);
        return _temperature;
    }

    /// <summary>Background chatter plus occasional bursts of 1-100 MB/s lasting 0.6-4 s with a fast attack and a slower release.</summary>
    private double StepNetwork(double dt)
    {
        if (_burstLeft > 0)
        {
            _burstLeft -= dt;
        }
        else if (_random.NextDouble() < 1 - Math.Exp(-0.35 * dt))
        {
            _burstDuration = _burstLeft = _random.NextRange(0.6, 4);
            _burstPeak = Math.Pow(10, _random.NextRange(6, 8));
        }

        var rate = 40_000 * (0.5 + _random.NextDouble());
        if (_burstLeft > 0)
        {
            var progress = 1 - (_burstLeft / _burstDuration);
            var envelope = progress < 0.15 ? progress / 0.15 : progress > 0.7 ? (1 - progress) / 0.3 : 1;
            rate += _burstPeak * envelope * (0.75 + (0.25 * _random.NextDouble()));
        }

        return Math.Clamp(rate, 0, NetworkMax);
    }

    /// <summary>
    /// A pulsing body (about 126 beats per minute) with random jitter and short transients, passed through
    /// VU-style ballistics: instant attack, release with a 0.3 s time constant.
    /// </summary>
    private double StepAudio(double dt)
    {
        var beat = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 2.1 * _time));
        var body = 0.18 + (0.30 * beat * beat) + (0.12 * _random.NextDouble());

        _transient *= Math.Exp(-dt / 0.15);
        if (_random.NextDouble() < 1 - Math.Exp(-1.2 * dt))
        {
            _transient = _random.NextRange(0.25, 0.5);
        }

        var raw = Math.Clamp(body + _transient, 0, 1) * 100;
        _audio = raw > _audio ? raw : _audio + ((raw - _audio) * (1 - Math.Exp(-dt / 0.3)));
        return Math.Clamp(_audio, 0, 100);
    }
}
