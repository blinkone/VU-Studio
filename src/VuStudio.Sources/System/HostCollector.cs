using System.Diagnostics;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>
/// Uptime (<c>uptime</c>, seconds) and the number of running processes (<c>process/count</c>) on systems
/// without a /proc to read them from. The process list is costly to build, so it is counted every five seconds.
/// </summary>
internal sealed class HostCollector : ISystemCollector
{
    private readonly Throttle _processes = new(TimeSpan.FromSeconds(5));

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        Metrics.EnsureNumber(sensors, "uptime", "Uptime", "System", SensorCategory.System, SensorKinds.Duration, Units.Seconds, 0, null, "update");
        sensors.Publish("uptime", Environment.TickCount64 / 1000.0);

        if (_processes.Due(context.Time))
        {
            var processes = Process.GetProcesses();
            try
            {
                Metrics.EnsureNumber(sensors, "process/count", "Processes", "System", SensorCategory.System, SensorKinds.Count, Units.None, 0, null, "counter");
                sensors.Publish("process/count", processes.Length);
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
    }
}
