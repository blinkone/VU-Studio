using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Providers;
using VuStudio.Server.Live;

namespace VuStudio.Server.Hosting;

/// <summary>
/// Starts and stops the moving parts in order (providers → hardware → engine) and reconciles them
/// whenever the configuration is saved.
/// </summary>
public sealed class VuStudioHost(
    ConfigStore config,
    ProviderManager providers,
    IHardwareHost hardware,
    DialEngine engine,
    SessionChannel sessionChannel,
    SessionState session,
    RuntimeOptions runtime,
    ILogger<VuStudioHost> logger) : IHostedService
{
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _housekeeping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("{App} {Version} starting (data: {Config})", AppInfo.Name, AppInfo.Version, config.FilePath);

        var needsDefaults = config.Current.Profiles.Count == 0 || providers.EnsureAutoCreated(config.GetCopy().Providers);
        if (needsDefaults)
        {
            config.Update(c =>
            {
                providers.EnsureAutoCreated(c.Providers);
                if (c.Profiles.Count == 0)
                {
                    c.Profiles.Add(StarterProfile.Create());
                }
            });
        }

        await providers.ApplyAsync(config.Current.Providers, cancellationToken).ConfigureAwait(false);
        await hardware.StartAsync(EffectiveHardware(config.Current), cancellationToken).ConfigureAwait(false);
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);

        config.Changed += OnConfigChanged;
        _cts = new CancellationTokenSource();
        _housekeeping = HousekeepingAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        config.Changed -= OnConfigChanged;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        await engine.StopAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        await hardware.StopAsync(cancellationToken).ConfigureAwait(false);
        await providers.StopAllAsync().ConfigureAwait(false);
        if (_housekeeping is not null)
        {
            try
            {
                await _housekeeping.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        logger.LogInformation("{App} stopped", AppInfo.Name);
    }

    private HardwareSettings EffectiveHardware(AppConfig cfg)
    {
        var settings = Json.Clone(cfg.Hardware);
        if (runtime.Simulate > 0)
        {
            settings.Simulate = runtime.Simulate;
        }

        return settings;
    }

    private void OnConfigChanged(AppConfig cfg) => _ = Task.Run(async () =>
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await providers.ApplyAsync(cfg.Providers).ConfigureAwait(false);
            hardware.Apply(EffectiveHardware(cfg));
            await sessionChannel.BroadcastProvidersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying the new configuration failed");
        }
        finally
        {
            _applyGate.Release();
        }
    });

    private async Task HousekeepingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            session.Refresh();
        }
    }
}
