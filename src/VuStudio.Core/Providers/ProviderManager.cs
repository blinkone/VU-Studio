using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Providers;

/// <summary>Runtime status of one provider instance.</summary>
public sealed record ProviderStatusInfo(
    string Id,
    string Type,
    string? Name,
    bool Enabled,
    ProviderHealth Health,
    string? Message,
    DateTimeOffset? Since,
    int Restarts);

/// <summary>
/// Owns the running provider instances: creates them from configuration, runs each on its own task,
/// restarts crashed instances with exponential backoff and reconciles on configuration changes.
/// </summary>
public sealed partial class ProviderManager : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, IProviderFactory> _factories;
    private readonly SensorHub _hub;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProviderManager> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderStatusInfo> _status = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProviderManager(
        IEnumerable<IProviderFactory> factories,
        SensorHub hub,
        IServiceProvider services,
        ILogger<ProviderManager> logger,
        TimeProvider? time = null)
    {
        _factories = factories
            .GroupBy(f => f.Descriptor.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _hub = hub;
        _services = services;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ProviderDescriptor> Descriptors =>
        _factories.Values.Select(f => f.Descriptor).OrderBy(d => d.Group).ThenBy(d => d.Type).ToList();

    public IProviderFactory? GetFactory(string type) => _factories.GetValueOrDefault(type);

    public IReadOnlyList<ProviderStatusInfo> GetStatus() =>
        _status.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Records the health the desktop app reports for a user-session provider it runs.
    /// Ignored for unknown, disabled or service-side instances.
    /// </summary>
    public bool ReportUserSessionStatus(string id, ProviderHealth health, string? message)
    {
        if (!_status.TryGetValue(id, out var current) || !current.Enabled
            || !_factories.TryGetValue(current.Type, out var factory) || !factory.Descriptor.RunsInUserSession)
        {
            return false;
        }

        var config = new ProviderInstanceConfig { Id = current.Id, Type = current.Type, Name = current.Name, Enabled = true };
        SetStatus(config, health, message is { Length: > 500 } ? message[..500] : message, current.Restarts);
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    public static partial Regex ValidId();

    /// <summary>
    /// Adds an instance for every <see cref="ProviderDescriptor.AutoCreate"/> type supported on this
    /// platform that is missing from <paramref name="configs"/>. Returns true when the list changed.
    /// </summary>
    public bool EnsureAutoCreated(List<ProviderInstanceConfig> configs)
    {
        var changed = false;
        foreach (var descriptor in Descriptors.Where(d => d.AutoCreate && d.IsSupportedHere))
        {
            if (configs.Any(c => c.Type == descriptor.Type))
            {
                continue;
            }

            configs.Add(new ProviderInstanceConfig { Id = descriptor.Type, Type = descriptor.Type, Enabled = true });
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Starts, stops and restarts instances so that the running set matches <paramref name="configs"/>.
    /// Instances whose configuration did not change keep running untouched.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<ProviderInstanceConfig> configs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = new Dictionary<string, ProviderInstanceConfig>(StringComparer.Ordinal);
            foreach (var config in configs)
            {
                if (!ValidId().IsMatch(config.Id))
                {
                    _logger.LogWarning("Ignoring provider with invalid id '{Id}'", config.Id);
                    continue;
                }

                if (!wanted.TryAdd(config.Id, config))
                {
                    _logger.LogWarning("Ignoring duplicate provider id '{Id}'", config.Id);
                }
            }

            foreach (var (id, running) in _running.ToArray())
            {
                if (!wanted.TryGetValue(id, out var config) || !config.Enabled || running.Fingerprint != Fingerprint(config))
                {
                    await StopAsync(id).ConfigureAwait(false);
                }
            }

            // User-session providers run in the tray, so StopAsync never sees them: drop their
            // sensors here once they are removed or disabled.
            foreach (var (id, status) in _status.ToArray())
            {
                var gone = !wanted.TryGetValue(id, out var current) || !current.Enabled;
                if (gone && _factories.TryGetValue(status.Type, out var f) && f.Descriptor.RunsInUserSession)
                {
                    _hub.RemoveProvider(id);
                }

                if (current is null)
                {
                    _status.TryRemove(id, out _);
                }
            }

            foreach (var config in wanted.Values)
            {
                if (!config.Enabled)
                {
                    SetStatus(config, ProviderHealth.Stopped, null, 0);
                    continue;
                }

                if (_running.ContainsKey(config.Id))
                {
                    continue;
                }

                if (!_factories.TryGetValue(config.Type, out var factory))
                {
                    SetStatus(config, ProviderHealth.Unavailable, $"Unknown provider type '{config.Type}'", 0);
                    continue;
                }

                if (!factory.Descriptor.IsSupportedHere || factory.Descriptor.RunsInUserSession)
                {
                    SetStatus(config, ProviderHealth.Unavailable,
                        factory.Descriptor.RunsInUserSession ? "Runs in the desktop app" : "Not supported on this platform", 0);
                    continue;
                }

                Start(config, factory);
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
        var cts = new CancellationTokenSource();
        var running = new Running(config.Id, Fingerprint(config), cts);
        _running[config.Id] = running;
        running.Task = Task.Run(() => SuperviseAsync(config, factory, running, cts.Token));
    }

    private async Task StopAsync(string id)
    {
        if (!_running.TryRemove(id, out var running))
        {
            return;
        }

        await running.Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (running.Task is { } task)
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            if (ex is TimeoutException)
            {
                _logger.LogWarning("Provider {Id} did not stop within 10 s", id);
            }
        }
        finally
        {
            running.Cts.Dispose();
            _hub.RemoveProvider(id);
        }
    }

    private async Task SuperviseAsync(ProviderInstanceConfig config, IProviderFactory factory, Running running, CancellationToken ct)
    {
        var restarts = 0;
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var status = new StatusReporter(this, config, () => restarts);
            ISensorProvider? provider = null;
            var startedAt = _time.GetUtcNow();
            try
            {
                provider = factory.Create(config, _services);
                status.Report(ProviderHealth.Starting);
                await provider.RunAsync(_hub.CreateSink(config.Id), status, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // RunAsync returned on its own: treat like a stop, but restart later in case the source comes back.
                status.Report(ProviderHealth.Stopped, "Provider exited");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider {Id} ({Type}) failed", config.Id, config.Type);
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
                        _logger.LogDebug(ex, "Disposing provider {Id} failed", config.Id);
                    }
                }
            }

            restarts++;
            if (_time.GetUtcNow() - startedAt > TimeSpan.FromMinutes(5))
            {
                backoff = TimeSpan.FromSeconds(2);
            }

            try
            {
                await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        SetStatus(config, ProviderHealth.Stopped, null, restarts);
    }

    private void SetStatus(ProviderInstanceConfig config, ProviderHealth health, string? message, int restarts)
    {
        _status.AddOrUpdate(
            config.Id,
            _ => new ProviderStatusInfo(config.Id, config.Type, config.Name, config.Enabled, health, message, _time.GetUtcNow(), restarts),
            (_, old) => old.Health == health && old.Message == message && old.Restarts == restarts
                ? old
                : new ProviderStatusInfo(config.Id, config.Type, config.Name, config.Enabled, health, message,
                    old.Health == health ? old.Since : _time.GetUtcNow(), restarts));
    }

    private static string Fingerprint(ProviderInstanceConfig config) =>
        $"{config.Type}|{config.Enabled}|{config.Name}|{config.ChangedRemotely}|{config.Settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}";

    private sealed class Running(string id, string fingerprint, CancellationTokenSource cts)
    {
        public string Id { get; } = id;

        public string Fingerprint { get; } = fingerprint;

        public CancellationTokenSource Cts { get; } = cts;

        public Task? Task { get; set; }
    }

    private sealed class StatusReporter(ProviderManager owner, ProviderInstanceConfig config, Func<int> restarts) : IProviderStatus
    {
        public void Report(ProviderHealth health, string? message = null) =>
            owner.SetStatus(config, health, message, restarts());
    }
}
