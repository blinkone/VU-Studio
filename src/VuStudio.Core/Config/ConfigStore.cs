using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VuStudio.Core.Config;

/// <summary>Raised when a save is based on an outdated revision.</summary>
public sealed class ConfigConflictException(long expected, long actual)
    : Exception($"Configuration changed meanwhile (expected revision {expected}, current {actual}).")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

/// <summary>
/// Loads and saves <c>config.json</c> atomically, keeps a <c>.bak</c> copy, and notifies listeners.
/// Readers always get an immutable snapshot (a private deep copy), so the engine and the API never
/// observe a half-applied edit.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly ILogger<ConfigStore> _logger;
    private readonly Lock _lock = new();
    private AppConfig _current;

    public ConfigStore(string path, ILogger<ConfigStore> logger, Func<AppConfig>? defaults = null)
    {
        _path = path;
        _logger = logger;
        _current = Load(defaults ?? (() => new AppConfig()));
    }

    public string FilePath => _path;

    /// <summary>Raised after a successful save, with the new snapshot.</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>The current configuration. Do not mutate; use <see cref="Update"/>.</summary>
    public AppConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>A mutable deep copy of the current configuration.</summary>
    public AppConfig GetCopy() => Json.Clone(Current);

    /// <summary>Applies <paramref name="mutate"/> to a copy and saves it.</summary>
    public AppConfig Update(Action<AppConfig> mutate)
    {
        AppConfig saved;
        lock (_lock)
        {
            var copy = Json.Clone(_current);
            mutate(copy);
            saved = SaveLocked(copy);
        }

        Changed?.Invoke(saved);
        return saved;
    }

    /// <summary>Replaces the whole configuration; fails if <paramref name="baseRevision"/> is outdated.</summary>
    public AppConfig Replace(AppConfig config, long? baseRevision = null)
    {
        AppConfig saved;
        lock (_lock)
        {
            if (baseRevision is { } expected && expected != _current.Revision)
            {
                throw new ConfigConflictException(expected, _current.Revision);
            }

            saved = SaveLocked(Json.Clone(config));
        }

        Changed?.Invoke(saved);
        return saved;
    }

    private AppConfig SaveLocked(AppConfig config)
    {
        config.Version = AppConfig.CurrentVersion;
        config.Revision = _current.Revision + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Json.Options);
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(_path))
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }

        File.Move(tmp, _path, overwrite: true);
        _current = config;
        return config;
    }

    private AppConfig Load(Func<AppConfig> defaults)
    {
        foreach (var candidate in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllBytes(candidate), Json.Options);
                if (config is not null)
                {
                    Migrate(config);
                    if (candidate != _path)
                    {
                        _logger.LogWarning("Loaded configuration from backup {Path}", candidate);
                    }

                    return config;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                _logger.LogError(ex, "Could not read configuration {Path}", candidate);
            }
        }

        _logger.LogInformation("Creating default configuration at {Path}", _path);
        var created = defaults();
        lock (_lock)
        {
            _current = new AppConfig { Revision = 0 };
            return SaveLocked(created);
        }
    }

    private static void Migrate(AppConfig config)
    {
        // Version 1 is the first schema; future migrations go here, oldest first.
        config.Version = AppConfig.CurrentVersion;
    }
}
