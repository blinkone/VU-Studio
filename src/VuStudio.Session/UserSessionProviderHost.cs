using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Script;

namespace VuStudio.Session;

/// <summary>Runtime state of one user-session provider instance.</summary>
public sealed record UserProviderStatus(string Id, string Type, ProviderHealth Health, string? Message, int Restarts);

public sealed class UserSessionProviderHostOptions
{
    /// <summary>First restart delay after a crash; doubles up to <see cref="MaxRestartDelay"/>.</summary>
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A run that lasted this long resets the restart backoff.</summary>
    public TimeSpan StableRun { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Runs the user-session providers (scripts) that the service lists in its <c>providers</c> message, so
/// that user code never executes as LocalSystem. Mirrors <see cref="ProviderManager"/>: only types whose
/// descriptor has <see cref="ProviderDescriptor.RunsInUserSession"/> run; instances are started, stopped
/// and restarted when their configuration changes; crashed instances restart with exponential backoff.
/// Output goes through a <see cref="ForwardingSink"/>.
/// </summary>
public sealed class UserSessionProviderHost : IAsyncDisposable
{
    private readonly ISessionPublisher _publisher;
    private readonly Dictionary<string, IProviderFactory> _factories;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly UserSessionProviderHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserProviderStatus> _status = new(StringComparer.Ordinal);

    public UserSessionProviderHost(
        ISessionPublisher publisher,
        IEnumerable<IProviderFactory> factories,
        IServiceProvider services,
        ILogger? logger = null,
        UserSessionProviderHostOptions? options = null,
        TimeProvider? time = null)
    {
        _publisher = publisher;
        _factories = factories
            .Where(f => f.Descriptor.RunsInUserSession)
            .GroupBy(f => f.Descriptor.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _services = services;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new UserSessionProviderHostOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Types this host can run.</summary>
    public IReadOnlyCollection<string> SupportedTypes => _factories.Keys;

    /// <summary>The user-session factories of the <c>VuStudio.Sources</c> assembly (the <c>script</c> provider and future ones).</summary>
    public static IReadOnlyList<IProviderFactory> DiscoverFactories() =>
        ProviderCatalog.Discover(typeof(ScriptProviderFactory).Assembly)
            .Where(f => f.Descriptor.RunsInUserSession)
            .ToList();

    public IReadOnlyList<UserProviderStatus> GetStatus() =>
        _status.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>Reconciles the running instances with <paramref name="configs"/> (the service's <c>providers</c> list).</summary>
    public async Task ApplyAsync(IReadOnlyList<ProviderInstanceConfig> configs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = new Dictionary<string, (ProviderInstanceConfig Config, IProviderFactory Factory)>(StringComparer.Ordinal);
            foreach (var config in configs)
            {
                if (config is null || !config.Enabled)
                {
                    continue;
                }

                if (!ProviderManager.ValidId().IsMatch(config.Id) || config.Id == WellKnownSensors.SessionProvider)
                {
                    _logger.LogWarning("Ignoring user-session provider with invalid id '{Id}'", config.Id);
                    continue;
                }

                if (!_factories.TryGetValue(config.Type, out var factory))
                {
                    _logger.LogWarning("User-session provider {Id}: type '{Type}' is not available in the desktop app", config.Id, config.Type);
                    SetStatus(config, ProviderHealth.Unavailable, $"Provider type '{config.Type}' is not available in the desktop app", 0);
                    _publisher.ReportStatus(config.Id, ProviderHealth.Unavailable, $"Provider type '{config.Type}' is not available in the desktop app");
                    continue;
                }

                if (!factory.Descriptor.IsSupportedHere)
                {
                    SetStatus(config, ProviderHealth.Unavailable, "Not supported on this platform", 0);
                    _publisher.ReportStatus(config.Id, ProviderHealth.Unavailable, "Not supported on this platform");
                    continue;
                }

                if (!wanted.TryAdd(config.Id, (config, factory)))
                {
                    _logger.LogWarning("Ignoring duplicate user-session provider id '{Id}'", config.Id);
                }
            }

            foreach (var (id, running) in _running.ToList())
            {
                if (!wanted.TryGetValue(id, out var entry) || running.Fingerprint != Fingerprint(entry.Config))
                {
                    await StopAsync(id).ConfigureAwait(false);
                }
            }

            foreach (var id in _status.Keys.Where(id => !wanted.ContainsKey(id) && !_running.ContainsKey(id)).ToList())
            {
                if (!configs.Any(c => c?.Id == id))
                {
                    _status.TryRemove(id, out _);
                }
            }

            foreach (var (id, entry) in wanted)
            {
                if (!_running.ContainsKey(id))
                {
                    Start(entry.Config, entry.Factory);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _running.Keys.ToList())
            {
                await StopAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void Start(ProviderInstanceConfig config, IProviderFactory factory)
    {
        _logger.LogInformation("Starting user-session provider {Id} ({Type})", config.Id, config.Type);
        var running = new Running(Fingerprint(config), new CancellationTokenSource());
        _running[config.Id] = running;
        var token = running.Cts.Token;
        running.Task = Task.Run(() => SuperviseAsync(config, factory, running, token));
    }

    private async Task StopAsync(string id)
    {
        if (!_running.Remove(id, out var running))
        {
            return;
        }

        _logger.LogInformation("Stopping user-session provider {Id}", id);
        await running.Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (running.Task is { } task)
            {
                await task.WaitAsync(_options.StopTimeout).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("User-session provider {Id} did not stop within {Timeout}", id, _options.StopTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            running.Cts.Dispose();
            _publisher.ClearProvider(id);
            _status.TryRemove(id, out _);
        }
    }

    private async Task SuperviseAsync(ProviderInstanceConfig config, IProviderFactory factory, Running running, CancellationToken ct)
    {
        var backoff = new Backoff(_options.InitialRestartDelay, _options.MaxRestartDelay);
        var sink = new ForwardingSink(_publisher, config.Id);
        while (!ct.IsCancellationRequested)
        {
            var status = new ForwardingStatus(_publisher, config.Id, (health, message) => SetStatus(config, health, message, running.Restarts));
            ISensorProvider? provider = null;
            var startedAt = _time.GetTimestamp();
            try
            {
                provider = factory.Create(config, _services);
                status.Report(ProviderHealth.Starting);
                await provider.RunAsync(sink, status, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                status.Report(ProviderHealth.Stopped, "Provider exited");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-session provider {Id} ({Type}) failed", config.Id, config.Type);
                status.Report(ProviderHealth.Error, ex.Message);
            }
            finally
            {
                if (provider is not null)
                {
                    try
                    {
                        await provider.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Disposing user-session provider {Id} failed", config.Id);
                    }
                }
            }

            running.Restarts++;
            if (_time.GetElapsedTime(startedAt) >= _options.StableRun)
            {
                backoff.Reset();
            }

            try
            {
                await Task.Delay(backoff.Next(), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetStatus(ProviderInstanceConfig config, ProviderHealth health, string? message, int restarts) =>
        _status[config.Id] = new UserProviderStatus(config.Id, config.Type, health, message, restarts);

    private static string Fingerprint(ProviderInstanceConfig config) =>
        $"{config.Type}|{config.Enabled}|{config.Name}|{config.Settings.ToJsonString(JsonSerializerOptions.Default)}";

    private sealed class Running(string fingerprint, CancellationTokenSource cts)
    {
        public string Fingerprint { get; } = fingerprint;

        public CancellationTokenSource Cts { get; } = cts;

        public Task? Task { get; set; }

        public int Restarts { get; set; }
    }
}
