using VuStudio.Core.Config;
using VuStudio.Core.Engine;

namespace VuStudio.Server.Hosting;

/// <summary>Lifecycle wrapper around the component that drives the dials.</summary>
public interface IHardwareHost
{
    IDialOutput Output { get; }

    Task StartAsync(HardwareSettings settings, CancellationToken cancellationToken);

    /// <summary>Applies changed hardware settings (port, simulator) — may reconnect.</summary>
    void Apply(HardwareSettings settings);

    Task StopAsync(CancellationToken cancellationToken);
}
