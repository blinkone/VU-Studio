using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Hardware;
using VuStudio.Hardware.Discovery;

namespace VuStudio.Server.Hosting;

/// <summary>Runs the <see cref="DialController"/> with options derived from the hardware settings.</summary>
public sealed class DialControllerHost(DialController controller) : IHardwareHost
{
    public IDialOutput Output => controller;

    public Task StartAsync(HardwareSettings settings, CancellationToken cancellationToken)
    {
        controller.ApplyOptions(ToOptions(settings));
        return controller.StartAsync(cancellationToken);
    }

    public void Apply(HardwareSettings settings) => controller.ApplyOptions(ToOptions(settings));

    public Task StopAsync(CancellationToken cancellationToken) => controller.StopAsync(cancellationToken);

    private DialControllerOptions ToOptions(HardwareSettings settings) => controller.Options with
    {
        Port = string.IsNullOrWhiteSpace(settings.Port) ? HubPortFinder.AutoPort : settings.Port,
        Simulate = Math.Max(0, settings.Simulate),
    };
}
