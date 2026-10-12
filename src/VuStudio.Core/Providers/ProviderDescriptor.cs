using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Providers;

/// <summary>A user-facing string in English with an optional Italian translation.</summary>
public sealed record LocalizedText(string En, string? It = null)
{
    public string Get(string? language) =>
        language is not null && language.StartsWith("it", StringComparison.OrdinalIgnoreCase) && It is not null ? It : En;

    public static implicit operator LocalizedText(string en) => new(en);
}

/// <summary>Where a provider can run.</summary>
[Flags]
public enum PlatformSupport
{
    Windows = 1,
    Linux = 2,
    MacOS = 4,
    Any = Windows | Linux | MacOS,
}

/// <summary>Grouping of providers in the UI.</summary>
public enum ProviderGroup
{
    /// <summary>Sensors of this PC (LibreHardwareMonitor, HWiNFO, system, network adapters).</summary>
    ThisPc,

    /// <summary>Audio, media, games, processes, user session.</summary>
    Software,

    /// <summary>Other machines on the LAN (agents, Glances, Prometheus, router).</summary>
    Network,

    /// <summary>Home Assistant, MQTT.</summary>
    HomeAutomation,

    /// <summary>Weather, markets, generic web APIs.</summary>
    Online,

    /// <summary>Clock, timers, scripts, push API, demo.</summary>
    Utility,
}

public enum SettingKind
{
    Text,
    Number,
    Boolean,

    /// <summary>Stored like text but masked in the UI and never echoed back by the API.</summary>
    Secret,

    /// <summary>One value from <see cref="SettingField.Options"/>.</summary>
    Select,

    /// <summary>A list of strings.</summary>
    TextList,

    Url,

    /// <summary>Multi-line text (scripts, JSON templates).</summary>
    Multiline,

    /// <summary>An array of objects; each row has the fields in <see cref="SettingField.Columns"/>.</summary>
    Table,

    /// <summary>A duration in seconds.</summary>
    Seconds,
}

public sealed record SettingOption(string Value, LocalizedText Label);

/// <summary>Describes one setting of a provider. The UI renders forms generically from these.</summary>
public sealed record SettingField
{
    /// <summary>JSON property name inside <c>ProviderInstanceConfig.Settings</c>.</summary>
    public required string Key { get; init; }

    public required LocalizedText Label { get; init; }

    public SettingKind Kind { get; init; } = SettingKind.Text;

    public JsonNode? Default { get; init; }

    public bool Required { get; init; }

    public LocalizedText? Help { get; init; }

    public string? Placeholder { get; init; }

    public IReadOnlyList<SettingOption>? Options { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>Row schema for <see cref="SettingKind.Table"/>.</summary>
    public IReadOnlyList<SettingField>? Columns { get; init; }
}

/// <summary>Static description of a provider type.</summary>
public sealed record ProviderDescriptor
{
    /// <summary>Stable type id, e.g. <c>lhm</c>, <c>ping</c>, <c>homeassistant</c>.</summary>
    public required string Type { get; init; }

    public required LocalizedText Name { get; init; }

    public LocalizedText? Description { get; init; }

    /// <summary>Icon id from the bundled icon set.</summary>
    public string Icon { get; init; } = "cog";

    public ProviderGroup Group { get; init; } = ProviderGroup.Utility;

    /// <summary>At most one instance may exist; its id equals <see cref="Type"/>.</summary>
    public bool Singleton { get; init; }

    /// <summary>An instance with id = <see cref="Type"/> is created automatically on first start.</summary>
    public bool AutoCreate { get; init; }

    public PlatformSupport Platforms { get; init; } = PlatformSupport.Any;

    /// <summary>Settings may only be changed from the local machine (e.g. scripts).</summary>
    public bool LocalConfigOnly { get; init; }

    /// <summary>The provider is executed by the tray app in the user session rather than by the service.</summary>
    public bool RunsInUserSession { get; init; }

    public IReadOnlyList<SettingField> Settings { get; init; } = [];

    [JsonIgnore]
    public bool IsSupportedHere =>
        (OperatingSystem.IsWindows() && Platforms.HasFlag(PlatformSupport.Windows)) ||
        (OperatingSystem.IsLinux() && Platforms.HasFlag(PlatformSupport.Linux)) ||
        (OperatingSystem.IsMacOS() && Platforms.HasFlag(PlatformSupport.MacOS));
}

/// <summary>Persisted configuration of one provider instance.</summary>
public sealed class ProviderInstanceConfig
{
    /// <summary>Unique id, <c>[a-z0-9-]+</c>. Becomes the prefix of every sensor id of this instance.</summary>
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    /// <summary>Optional user label (e.g. "Unraid", "Casa").</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    public JsonObject Settings { get; set; } = new();
}

/// <summary>Creates provider instances of one type. Register implementations in DI.</summary>
public interface IProviderFactory
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Creates an instance. <paramref name="services"/> resolves shared services such as
    /// <c>ILoggerFactory</c>, <c>IHttpClientFactory</c>, <c>TimeProvider</c> and <see cref="SensorHub"/>.
    /// </summary>
    ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services);
}
