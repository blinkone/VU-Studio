using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.Lhm;

internal enum LhmPhase
{
    /// <summary><c>Open()</c> plus the first update pass (may legitimately be slow).</summary>
    Opening,

    /// <summary><c>Reset()</c> after a suspected resume from sleep.</summary>
    Resetting,

    /// <summary>An update pass is running.</summary>
    Updating,

    /// <summary>Waiting for the next tick.</summary>
    Idle,

    Stopped,
}

internal enum WatchdogLevel
{
    Ok,
    Degraded,
    Fail,
}

/// <summary>Time limits of the lifecycle. Defaults follow the provider spec; tests shrink them.</summary>
internal sealed record LhmTimings
{
    public TimeSpan DegradedAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FailAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan OpenDegradedAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan OpenFailAfter { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan WatchTick { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan DisposeWait { get; init; } = TimeSpan.FromSeconds(5);

    public int NullCpuTemperaturePolls { get; init; } = 10;

    public int MaxConsecutivePassFailures { get; init; } = 3;

    /// <summary>Hardware that vanished for this many complete passes is removed from the catalog.</summary>
    public int MissedPassesBeforeRemoval { get; init; } = 3;
}

/// <summary>Environment checks, injectable so the policy is testable on any OS.</summary>
internal sealed record LhmEnvironment(Func<bool> IsElevated, Func<bool> PawnIoInstalled);

internal readonly record struct LhmStatus(ProviderHealth Health, string? Message);

/// <summary>Pure decision functions for the watchdog and the health report.</summary>
internal static class LhmHealth
{
    public const string NotElevatedMessage =
        "Not running with administrator rights: CPU, motherboard and disk sensors may be missing. " +
        "Run the VU Studio service as SYSTEM or as an administrator.";

    public const string PawnIoMessage =
        "CPU temperatures are unavailable: the PawnIO driver is required. Install it from " +
        "https://github.com/namazso/PawnIO.Setup or run: winget install --id namazso.PawnIO -e --silent";

    public const string PawnIoInstalledMessage =
        "CPU temperature sensors return no data although PawnIO is installed. Check that the driver is loaded " +
        "(Memory Integrity can block it) and that the service runs as SYSTEM.";

    public static WatchdogLevel Watchdog(LhmPhase phase, TimeSpan stuckFor, LhmTimings t)
    {
        var (degraded, fail) = phase switch
        {
            LhmPhase.Opening or LhmPhase.Resetting => (t.OpenDegradedAfter, t.OpenFailAfter),
            LhmPhase.Updating => (t.DegradedAfter, t.FailAfter),
            _ => (TimeSpan.MaxValue, TimeSpan.MaxValue),
        };
        return stuckFor >= fail ? WatchdogLevel.Fail : stuckFor >= degraded ? WatchdogLevel.Degraded : WatchdogLevel.Ok;
    }

    /// <summary>True when the gap since the last pass suggests the machine slept and LHM should be reset.</summary>
    public static bool ShouldReset(TimeSpan gapSinceLastPass, TimeSpan interval) =>
        gapSinceLastPass > TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, interval.Ticks * 10));

    public static LhmStatus Evaluate(
        WatchdogLevel watchdog,
        TimeSpan stuckFor,
        bool firstPassDone,
        bool isElevated,
        int cpuTemperatureMisses,
        bool pawnIoInstalled,
        int failedHardware,
        string? lastError,
        LhmTimings t)
    {
        var messages = new List<string>();
        if (watchdog != WatchdogLevel.Ok)
        {
            messages.Add($"LibreHardwareMonitor has not responded for {(int)stuckFor.TotalSeconds} s.");
        }

        if (!isElevated)
        {
            messages.Add(NotElevatedMessage);
        }

        if (cpuTemperatureMisses >= t.NullCpuTemperaturePolls)
        {
            messages.Add(pawnIoInstalled ? PawnIoInstalledMessage : PawnIoMessage);
        }

        if (failedHardware > 0)
        {
            messages.Add($"{failedHardware} hardware component(s) failed to update{(lastError is null ? "." : ": " + lastError)}");
        }

        if (messages.Count > 0)
        {
            return new LhmStatus(ProviderHealth.Degraded, string.Join(' ', messages));
        }

        return firstPassDone ? new LhmStatus(ProviderHealth.Ok, null) : new LhmStatus(ProviderHealth.Starting, null);
    }
}
