namespace VuStudio.Core.Sensors;

/// <summary>Broad grouping used by the UI to organise the sensor catalog.</summary>
public enum SensorCategory
{
    Cpu,
    Gpu,
    Memory,
    Storage,
    Network,
    Cooling,
    Power,
    Battery,
    Motherboard,
    Audio,
    Media,
    Game,
    Process,
    Time,
    Weather,
    Finance,
    Home,
    System,
    Custom,
}

/// <summary>What kind of payload a sensor carries.</summary>
public enum SensorValueKind
{
    /// <summary>A number in <see cref="SensorInfo.Unit"/>.</summary>
    Number,

    /// <summary>Free text (song title, weather description, process name…).</summary>
    Text,

    /// <summary>A color as text in <c>#RRGGBB</c> form (accent color, album art color…).</summary>
    Color,
}

/// <summary>Well-known values for <see cref="SensorInfo.Kind"/>. Providers may use other strings.</summary>
public static class SensorKinds
{
    public const string Load = "load";
    public const string Temperature = "temperature";
    public const string Clock = "clock";
    public const string Power = "power";
    public const string Voltage = "voltage";
    public const string Current = "current";
    public const string Energy = "energy";
    public const string Fan = "fan";
    public const string Flow = "flow";
    public const string Level = "level";
    public const string Data = "data";
    public const string Throughput = "throughput";
    public const string Fps = "fps";
    public const string FrameTime = "frametime";
    public const string Latency = "latency";
    public const string Percent = "percent";
    public const string Duration = "duration";
    public const string Count = "count";
    public const string Humidity = "humidity";
    public const string Speed = "speed";
    public const string Price = "price";
    public const string State = "state";
    public const string Text = "text";
    public const string Color = "color";
}

/// <summary>
/// Canonical units. Providers always publish in these units; display units (°F, Mbit/s, GB, GHz…)
/// are conversions applied at presentation time by <see cref="Mapping.UnitConverter"/>.
/// </summary>
public static class Units
{
    public const string None = "";
    public const string Percent = "%";
    public const string Celsius = "°C";
    public const string MHz = "MHz";
    public const string Hz = "Hz";
    public const string Watt = "W";
    public const string Volt = "V";
    public const string Ampere = "A";
    public const string Rpm = "RPM";
    public const string Bytes = "B";
    public const string BytesPerSecond = "B/s";
    public const string Milliseconds = "ms";
    public const string Seconds = "s";
    public const string Fps = "fps";
    public const string Decibel = "dB";
    public const string WattHour = "Wh";
    public const string LitersPerHour = "L/h";
}

/// <summary>
/// Metadata describing one sensor. The <see cref="Id"/> is globally unique and has the form
/// <c>{providerId}/{localPath}</c>, e.g. <c>lhm/cpu/0/temperature/1</c>.
/// </summary>
public sealed record SensorInfo
{
    /// <summary>Full id. Providers pass a local path to <see cref="ISensorSink"/>; the hub prefixes the provider id.</summary>
    public required string Id { get; init; }

    /// <summary>Human readable name, e.g. "CPU Package".</summary>
    public required string Name { get; init; }

    /// <summary>Hardware or logical device the sensor belongs to, e.g. "AMD Ryzen 7 7800X3D", "Wi-Fi", "Spotify".</summary>
    public string? Device { get; init; }

    public SensorCategory Category { get; init; } = SensorCategory.Custom;

    /// <summary>See <see cref="SensorKinds"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>Canonical unit, see <see cref="Units"/>.</summary>
    public string Unit { get; init; } = Units.None;

    public SensorValueKind ValueKind { get; init; } = SensorValueKind.Number;

    /// <summary>Suggested lower bound for gauges (e.g. 0 for a load).</summary>
    public double? Min { get; init; }

    /// <summary>Suggested upper bound for gauges (e.g. 100 for a load, 105 for a CPU temperature).</summary>
    public double? Max { get; init; }

    /// <summary>Suggested icon id from the bundled icon set (e.g. "cpu-64-bit").</summary>
    public string? Icon { get; init; }

    /// <summary>Remote host label for sensors coming from other machines; <c>null</c> means this PC.</summary>
    public string? Host { get; init; }

    /// <summary>Id of the provider instance that owns the sensor (filled in by the hub).</summary>
    public string ProviderId { get; init; } = "";

    /// <summary>After how long without updates the value is considered stale. <c>null</c> uses the hub default.</summary>
    public double? StaleAfterSeconds { get; init; }
}

/// <summary>Immutable view of a sensor and its latest value.</summary>
public sealed record SensorSnapshot(
    SensorInfo Info,
    double? Value,
    string? Text,
    DateTimeOffset? Updated,
    long Version,
    bool IsStale)
{
    public string Id => Info.Id;

    /// <summary>True when the sensor has a numeric value that is not stale.</summary>
    public bool HasFreshNumber => Value.HasValue && !IsStale;
}
