namespace VuStudio.Core.Lighting;

public enum BacklightMode
{
    Off,

    /// <summary>One fixed color.</summary>
    Fixed,

    /// <summary>Color interpolated along the dial scale (position 0..1).</summary>
    Gradient,

    /// <summary>Color steps by value (display units): the last threshold ≤ value wins.</summary>
    Thresholds,

    /// <summary>Color taken from another source (Windows accent, album art, a color sensor).</summary>
    Sync,
}

public enum SyncSource
{
    AccentColor,
    AlbumArt,
    Sensor,
}

public enum LightEffect
{
    None,

    /// <summary>Smooth sine fade.</summary>
    Breathe,

    /// <summary>Heartbeat-like: fast rise, exponential decay.</summary>
    Pulse,

    /// <summary>Square on/off.</summary>
    Blink,

    /// <summary>Cycles the hue.</summary>
    Rainbow,
}

public enum Comparison
{
    Above,
    Below,
}

public sealed record GradientStop(double Position, RgbwColor Color);

public sealed record ThresholdStep(double Value, RgbwColor Color);

/// <summary>Overrides the light while a condition holds, optionally raising a desktop notification.</summary>
public sealed class AlarmRule
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sensor to watch (canonical units). <c>null</c> watches the page value (display units).</summary>
    public string? SensorId { get; set; }

    public Comparison Comparison { get; set; } = Comparison.Above;

    public double Threshold { get; set; }

    /// <summary>The alarm clears only once the value is this far back on the safe side.</summary>
    public double Hysteresis { get; set; }

    /// <summary>The condition must hold this long before the alarm fires.</summary>
    public double ForSeconds { get; set; } = 3;

    public RgbwColor Color { get; set; } = RgbwColor.Red;

    public LightEffect Effect { get; set; } = LightEffect.Blink;

    public int PeriodMs { get; set; } = 800;

    public bool Notify { get; set; }

    /// <summary>Notification text; supports the same templates as face text (e.g. "CPU at {value}{unit}").</summary>
    public string? Message { get; set; }
}

public sealed class BacklightConfig
{
    public BacklightMode Mode { get; set; } = BacklightMode.Gradient;

    /// <summary>Color for <see cref="BacklightMode.Fixed"/> and fallback for Sync when the source has no color.</summary>
    public RgbwColor Color { get; set; } = RgbwColor.WarmWhite;

    public List<GradientStop> Gradient { get; set; } =
    [
        new(0, new RgbwColor(0, 200, 255)),
        new(0.6, new RgbwColor(0, 255, 80)),
        new(0.8, RgbwColor.Yellow),
        new(1, RgbwColor.Red),
    ];

    public List<ThresholdStep> Thresholds { get; set; } = [];

    public SyncSource Sync { get; set; } = SyncSource.AccentColor;

    /// <summary>Color-valued sensor for <see cref="SyncSource.Sensor"/>.</summary>
    public string? SyncSensorId { get; set; }

    /// <summary>0..100.</summary>
    public int Brightness { get; set; } = 80;

    public LightEffect Effect { get; set; } = LightEffect.None;

    public int EffectPeriodMs { get; set; } = 4000;

    /// <summary>0..100: how much the effect dims at its lowest point.</summary>
    public int EffectDepth { get; set; } = 60;

    public List<AlarmRule> Alarms { get; set; } = [];
}

/// <summary>Inputs for one backlight evaluation.</summary>
public readonly record struct BacklightInputs(
    double? Value,
    double Fraction,
    DateTimeOffset Now,
    Func<string, double?> Number,
    Func<string, string?> Text,
    RgbwColor? AccentColor,
    RgbwColor? AlbumColor,
    double NightFactor = 1);

/// <summary>Result of a backlight evaluation.</summary>
/// <param name="Levels">Device levels to send.</param>
/// <param name="TransitionMs">Desired full-scale transition time; the controller maps it to on-device easing.</param>
/// <param name="ActiveAlarm">The alarm currently overriding the light, if any.</param>
/// <param name="RaisedAlarm">An alarm that fired during this evaluation (notify once).</param>
public readonly record struct BacklightOutput(LedLevels Levels, int TransitionMs, AlarmRule? ActiveAlarm, AlarmRule? RaisedAlarm);

/// <summary>Per-page runtime state for alarms (hold timers, hysteresis).</summary>
public sealed class BacklightState
{
    internal Dictionary<AlarmRule, AlarmTracker> Trackers { get; } = new(ReferenceEqualityComparer.Instance);

    internal sealed class AlarmTracker
    {
        public DateTimeOffset? ConditionSince { get; set; }

        public bool Active { get; set; }
    }

    public void Reset() => Trackers.Clear();
}

public static class BacklightEngine
{
    public const int DefaultTransitionMs = 400;

    public static BacklightOutput Compute(BacklightConfig config, BacklightState state, in BacklightInputs input)
    {
        var (activeAlarm, raised) = EvaluateAlarms(config, state, input);

        RgbwColor color;
        LightEffect effect;
        int periodMs;
        var depth = Math.Clamp(config.EffectDepth, 0, 100) / 100.0;

        if (activeAlarm is not null)
        {
            color = activeAlarm.Color;
            effect = activeAlarm.Effect;
            periodMs = activeAlarm.PeriodMs;
            depth = 1;
        }
        else
        {
            color = BaseColor(config, input);
            effect = config.Effect;
            periodMs = config.EffectPeriodMs;
        }

        periodMs = Math.Clamp(periodMs, 100, 120_000);
        var phase = (input.Now.ToUnixTimeMilliseconds() % periodMs) / (double)periodMs;
        var factor = 1.0;
        var transition = DefaultTransitionMs;

        switch (effect)
        {
            case LightEffect.Breathe:
                factor = 1 - (depth * (0.5 - (0.5 * Math.Cos(2 * Math.PI * phase))));
                transition = Math.Max(50, periodMs / 10);
                break;
            case LightEffect.Pulse:
                factor = (1 - depth) + (depth * Math.Exp(-6 * phase));
                transition = 60;
                break;
            case LightEffect.Blink:
                factor = phase < 0.5 ? 1 : 1 - depth;
                transition = 0;
                break;
            case LightEffect.Rainbow:
                color = RgbwColor.FromHsv(phase * 360);
                transition = Math.Max(50, periodMs / 24);
                break;
        }

        var brightness = activeAlarm is not null ? Math.Max(config.Brightness, 60) : config.Brightness;
        var level = Math.Clamp(brightness, 0, 100) / 100.0 * factor * Math.Clamp(input.NightFactor, 0, 1);
        if (config.Mode == BacklightMode.Off && activeAlarm is null)
        {
            level = 0;
        }

        return new BacklightOutput(color.ToLevels(level), transition, activeAlarm, raised);
    }

    /// <summary>Color before effects, alarms and brightness.</summary>
    public static RgbwColor BaseColor(BacklightConfig config, in BacklightInputs input)
    {
        switch (config.Mode)
        {
            case BacklightMode.Off:
                return RgbwColor.Black;
            case BacklightMode.Fixed:
                return config.Color;
            case BacklightMode.Gradient:
                return SampleGradient(config.Gradient, input.Fraction, config.Color);
            case BacklightMode.Thresholds:
            {
                if (input.Value is not { } value || config.Thresholds.Count == 0)
                {
                    return config.Color;
                }

                var chosen = config.Color;
                foreach (var step in config.Thresholds.OrderBy(t => t.Value))
                {
                    if (value >= step.Value)
                    {
                        chosen = step.Color;
                    }
                }

                return chosen;
            }

            case BacklightMode.Sync:
            {
                RgbwColor? synced = config.Sync switch
                {
                    SyncSource.AccentColor => input.AccentColor,
                    SyncSource.AlbumArt => input.AlbumColor,
                    SyncSource.Sensor when config.SyncSensorId is { } id && RgbwColor.TryParse(input.Text(id), out var c) => c,
                    _ => null,
                };
                return synced ?? config.Color;
            }

            default:
                return config.Color;
        }
    }

    public static RgbwColor SampleGradient(IReadOnlyList<GradientStop> stops, double position, RgbwColor fallback)
    {
        if (stops.Count == 0)
        {
            return fallback;
        }

        var ordered = stops.OrderBy(s => s.Position).ToList();
        position = Math.Clamp(position, 0, 1);
        if (position <= ordered[0].Position)
        {
            return ordered[0].Color;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (position <= ordered[i].Position)
            {
                var a = ordered[i - 1];
                var b = ordered[i];
                var span = b.Position - a.Position;
                return span <= 0 ? b.Color : RgbwColor.Lerp(a.Color, b.Color, (position - a.Position) / span);
            }
        }

        return ordered[^1].Color;
    }

    private static (AlarmRule? Active, AlarmRule? Raised) EvaluateAlarms(BacklightConfig config, BacklightState state, in BacklightInputs input)
    {
        AlarmRule? active = null;
        AlarmRule? raised = null;
        foreach (var rule in config.Alarms)
        {
            if (!rule.Enabled)
            {
                state.Trackers.Remove(rule);
                continue;
            }

            if (!state.Trackers.TryGetValue(rule, out var tracker))
            {
                tracker = new BacklightState.AlarmTracker();
                state.Trackers[rule] = tracker;
            }

            var value = rule.SensorId is { } id ? input.Number(id) : input.Value;
            if (value is not { } v)
            {
                tracker.ConditionSince = null;
                tracker.Active = false;
                continue;
            }

            var hysteresis = Math.Abs(rule.Hysteresis);
            var triggered = rule.Comparison == Comparison.Above ? v > rule.Threshold : v < rule.Threshold;
            var cleared = rule.Comparison == Comparison.Above ? v <= rule.Threshold - hysteresis : v >= rule.Threshold + hysteresis;

            if (tracker.Active)
            {
                if (cleared)
                {
                    tracker.Active = false;
                    tracker.ConditionSince = null;
                }
            }
            else if (triggered)
            {
                tracker.ConditionSince ??= input.Now;
                if ((input.Now - tracker.ConditionSince.Value).TotalSeconds >= rule.ForSeconds)
                {
                    tracker.Active = true;
                    if (rule.Notify)
                    {
                        raised ??= rule;
                    }
                }
            }
            else
            {
                tracker.ConditionSince = null;
            }

            if (tracker.Active)
            {
                active ??= rule;
            }
        }

        return (active, raised);
    }
}
