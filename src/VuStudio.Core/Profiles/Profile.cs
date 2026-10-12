using System.Text.Json.Serialization;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;

namespace VuStudio.Core.Profiles;

public enum ProfileActivation
{
    /// <summary>Always eligible (the default profile).</summary>
    Always,

    /// <summary>Eligible while its conditions hold.</summary>
    Auto,

    /// <summary>Only when chosen by the user (tray, hotkey, API).</summary>
    Manual,
}

public enum ConditionMatch
{
    Any,
    All,
}

public enum OfflineBehavior
{
    /// <summary>Needle to 0.</summary>
    Zero,

    /// <summary>Keep the last value.</summary>
    Hold,

    /// <summary>Needle to 0 and lights dimmed.</summary>
    Dim,
}

/// <summary>On-device easing: the dial moves <see cref="Step"/> % every <see cref="PeriodMs"/> ms.</summary>
public sealed record Easing(int Step, int PeriodMs)
{
    public static Easing DefaultNeedle { get; } = new(2, 50);

    public static Easing DefaultBacklight { get; } = new(5, 100);

    /// <summary>Easing that completes a full-scale change in about <paramref name="milliseconds"/>.</summary>
    public static Easing ForTransition(int milliseconds, int periodMs = 20)
    {
        if (milliseconds <= periodMs)
        {
            return new Easing(100, periodMs);
        }

        var steps = Math.Max(1, milliseconds / periodMs);
        return new Easing(Math.Clamp((int)Math.Ceiling(100.0 / steps), 1, 100), periodMs);
    }
}

/// <summary>What a page shows on the needle and how the value is mapped.</summary>
public sealed class ValueBinding
{
    public string? SensorId { get; set; }

    /// <summary>Display unit ("auto", "°F", "Mbit/s"…). Min/Max are expressed in this unit.</summary>
    public string DisplayUnit { get; set; } = UnitConverter.Auto;

    /// <summary>Range start; null uses the sensor's suggested minimum (or 0).</summary>
    public double? Min { get; set; }

    /// <summary>Range end; null uses the sensor's suggested maximum (or 100).</summary>
    public double? Max { get; set; }

    public ScaleType Scale { get; set; } = ScaleType.Linear;

    public List<ScalePoint>? Points { get; set; }

    public bool Invert { get; set; }

    /// <summary>Smoothing time constant while rising (ms).</summary>
    public double AttackMs { get; set; } = 150;

    /// <summary>Smoothing time constant while falling (ms).</summary>
    public double ReleaseMs { get; set; } = 300;

    public double HoldMs { get; set; }

    public OfflineBehavior Offline { get; set; } = OfflineBehavior.Zero;

    /// <summary>Fixed decimals for value text; null = automatic.</summary>
    public int? Decimals { get; set; }

    /// <summary>Label used as the face title when <see cref="FaceConfig.Title"/> is not set.</summary>
    public string? Label { get; set; }

    /// <summary>Needle easing override for this page.</summary>
    public Easing? NeedleEasing { get; set; }
}

/// <summary>One "screen" of a dial: data, face and light.</summary>
public sealed class DialPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    public string? Name { get; set; }

    public ValueBinding Value { get; set; } = new();

    public FaceConfig Face { get; set; } = new();

    public BacklightConfig Light { get; set; } = new();

    /// <summary>Seconds this page stays visible when rotating; null uses the assignment's interval.</summary>
    public int? DurationSeconds { get; set; }
}

/// <summary>What a physical dial position (slot) shows within a profile.</summary>
public sealed class DialAssignment
{
    /// <summary>1-based dial position; mapped to a dial UID in hardware settings.</summary>
    public int Slot { get; set; }

    public List<DialPage> Pages { get; set; } = [];

    public int RotationSeconds { get; set; } = 15;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProcessRunningCondition), "processRunning")]
[JsonDerivedType(typeof(ForegroundAppCondition), "foregroundApp")]
[JsonDerivedType(typeof(ScheduleCondition), "schedule")]
[JsonDerivedType(typeof(SensorCondition), "sensor")]
public abstract class ProfileCondition
{
}

/// <summary>True while any of the listed executables runs (names without path; '*' wildcards allowed).</summary>
public sealed class ProcessRunningCondition : ProfileCondition
{
    public List<string> Processes { get; set; } = [];
}

/// <summary>True while one of the listed executables owns the foreground window.</summary>
public sealed class ForegroundAppCondition : ProfileCondition
{
    public List<string> Processes { get; set; } = [];
}

/// <summary>True on the given days between From and To (local time; may wrap past midnight).</summary>
public sealed class ScheduleCondition : ProfileCondition
{
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];

    public TimeOnly From { get; set; } = new(9, 0);

    public TimeOnly To { get; set; } = new(18, 0);
}

/// <summary>True while a sensor compares to a value for at least ForSeconds.</summary>
public sealed class SensorCondition : ProfileCondition
{
    public string SensorId { get; set; } = "";

    public Comparison Comparison { get; set; } = Comparison.Above;

    public double Value { get; set; }

    public double ForSeconds { get; set; }
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    public string Name { get; set; } = "Profile";

    /// <summary>Icon id for the UI.</summary>
    public string? Icon { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Higher wins among eligible profiles.</summary>
    public int Priority { get; set; }

    public ProfileActivation Activation { get; set; } = ProfileActivation.Auto;

    public ConditionMatch Match { get; set; } = ConditionMatch.Any;

    public List<ProfileCondition> Conditions { get; set; } = [];

    /// <summary>
    /// Slots without an assignment inherit the default profile's assignment when true; otherwise they go idle.
    /// </summary>
    public bool InheritUnassigned { get; set; } = true;

    public List<DialAssignment> Dials { get; set; } = [];
}
