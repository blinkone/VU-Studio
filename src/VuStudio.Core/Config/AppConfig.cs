using VuStudio.Core.Faces;
using VuStudio.Core.Profiles;
using VuStudio.Core.Providers;

namespace VuStudio.Core.Config;

/// <summary>Root of <c>config.json</c>.</summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Incremented on every save; used for optimistic concurrency by the UI.</summary>
    public long Revision { get; set; }

    public GeneralSettings General { get; set; } = new();

    public HardwareSettings Hardware { get; set; } = new();

    public WebSettings Web { get; set; } = new();

    public NightSettings Night { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    public List<ProviderInstanceConfig> Providers { get; set; } = [];

    public List<Profile> Profiles { get; set; } = [];
}

public sealed class GeneralSettings
{
    /// <summary>"auto", "en" or "it".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Default display unit for temperatures: "°C" or "°F".</summary>
    public string TemperatureUnit { get; set; } = "°C";

    /// <summary>Use bits (Mbit/s) instead of bytes for network rates by default.</summary>
    public bool NetworkInBits { get; set; } = true;
}

public sealed class DialSettings
{
    public string Uid { get; set; } = "";

    /// <summary>1-based position used by profiles.</summary>
    public int Slot { get; set; }

    public string? Name { get; set; }

    public Easing? NeedleEasing { get; set; }

    public Easing? BacklightEasing { get; set; }
}

public sealed class HardwareSettings
{
    /// <summary>"auto" or a port name (COM5, /dev/ttyUSB0).</summary>
    public string Port { get; set; } = "auto";

    /// <summary>When &gt; 0, use a simulated hub with this many dials instead of real hardware.</summary>
    public int Simulate { get; set; }

    public List<DialSettings> Dials { get; set; } = [];

    public NeedleGeometry Geometry { get; set; } = NeedleGeometry.Default;

    /// <summary>Global brightness multiplier 0..100 applied on top of every page.</summary>
    public int MasterBrightness { get; set; } = 100;
}

public sealed class WebSettings
{
    public int Port { get; set; } = 5341;

    /// <summary>Listen on all interfaces (requires <see cref="PasswordHash"/>).</summary>
    public bool AllowLan { get; set; }

    /// <summary>PBKDF2 hash ("pbkdf2-sha256$iterations$salt$hash"); null = no LAN login possible.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Bearer token for the push API and external integrations.</summary>
    public string ApiToken { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Extra host names accepted in the Host header (DNS rebinding protection).</summary>
    public List<string> AllowedHosts { get; set; } = [];
}

public enum NightAction
{
    None,

    /// <summary>Dim lights to <see cref="NightSettings.DimPercent"/>.</summary>
    Dim,

    /// <summary>Lights off.</summary>
    LightsOff,

    /// <summary>Lights off and needles parked at 0.</summary>
    Sleep,
}

public sealed class NightSettings
{
    public NightAction OnLock { get; set; } = NightAction.LightsOff;

    public NightAction OnIdle { get; set; } = NightAction.Dim;

    public int IdleMinutes { get; set; } = 10;

    public bool QuietHoursEnabled { get; set; }

    public TimeOnly QuietFrom { get; set; } = new(23, 0);

    public TimeOnly QuietTo { get; set; } = new(7, 0);

    public NightAction QuietAction { get; set; } = NightAction.Dim;

    /// <summary>0..100 brightness kept when dimming.</summary>
    public int DimPercent { get; set; } = 20;

    /// <summary>Park needles and switch lights off when the service stops or the PC sleeps.</summary>
    public bool SleepOnShutdown { get; set; } = true;
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;

    public int RetentionDays { get; set; } = 30;

    /// <summary>Extra sensors to record besides the ones bound to dials.</summary>
    public List<string> ExtraSensors { get; set; } = [];
}
