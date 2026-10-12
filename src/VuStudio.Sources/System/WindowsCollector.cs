using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>Physical memory as Windows reports it.</summary>
internal readonly record struct WindowsMemory(ulong TotalPhysical, ulong AvailablePhysical);

/// <summary>The two Win32 calls the collector needs, abstracted so the arithmetic can be tested anywhere.</summary>
internal interface IWindowsApi
{
    /// <summary>System-wide CPU times in 100 ns units; <c>kernel</c> includes <c>idle</c>.</summary>
    bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user);

    bool TryGetMemory(out WindowsMemory memory);
}

/// <summary>The real thing: <c>GetSystemTimes</c> and <c>GlobalMemoryStatusEx</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32Api : IWindowsApi
{
    public bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user) =>
        GetSystemTimes(out idle, out kernel, out user);

    public bool TryGetMemory(out WindowsMemory memory)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            memory = default;
            return false;
        }

        memory = new WindowsMemory(status.TotalPhys, status.AvailPhys);
        return true;
    }

    // A FILETIME is two little-endian DWORDs, so an out ulong receives it correctly.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

/// <summary>Total CPU load (<c>cpu/load</c>) and memory (<c>mem/…</c>) on Windows, for when no richer source is running.</summary>
internal sealed class WindowsCollector(IWindowsApi api) : ISystemCollector
{
    private (ulong Idle, ulong Total)? _previous;

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        if (api.TryGetCpuTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel time already contains the idle time
            if (_previous is { } before && total > before.Total && idle >= before.Idle)
            {
                var totalDelta = (double)(total - before.Total);
                var idleDelta = (double)(idle - before.Idle);
                Metrics.EnsureNumber(sensors, "cpu/load", "CPU total", "CPU", SensorCategory.Cpu, SensorKinds.Load, Units.Percent, 0, 100, "cpu-64-bit");
                sensors.Publish("cpu/load", Math.Clamp(100 * (totalDelta - idleDelta) / totalDelta, 0, 100));
            }

            _previous = (idle, total);
        }

        if (api.TryGetMemory(out var memory))
        {
            Metrics.PublishMemory(sensors, memory.TotalPhysical, memory.AvailablePhysical);
        }
    }

    public void Dispose()
    {
    }
}
