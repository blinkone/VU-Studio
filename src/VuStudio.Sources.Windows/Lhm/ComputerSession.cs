using System.Runtime.Versioning;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>The real LibreHardwareMonitor <c>Computer</c>. Windows only; never constructed elsewhere.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComputerSession(LhmSettings settings) : ILhmSession
{
    private Computer? _computer;

    public void Open()
    {
        // SMART queries wake disks and stutter: ask drives at most every 30 s (NVMe temperature stays cheap).
        StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(30);
        _computer = new Computer
        {
            IsCpuEnabled = settings.Cpu,
            IsGpuEnabled = settings.Gpu,
            IsMemoryEnabled = settings.Memory,
            IsMotherboardEnabled = settings.Motherboard,
            IsControllerEnabled = settings.Controllers,
            IsStorageEnabled = settings.Storage,
            IsNetworkEnabled = settings.Network,
            IsPsuEnabled = settings.Psu,
            IsBatteryEnabled = settings.Battery,
            IsPowerMonitorEnabled = false,
        };
        _computer.Open();
    }

    public void Accept(IVisitor visitor) => _computer!.Accept(visitor);

    public void Reset() => _computer!.Reset();

    public void Close()
    {
        var computer = _computer;
        _computer = null;
        computer?.Close();
    }

    /// <summary>True when the process token is SYSTEM or an elevated administrator.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool PawnIoInstalled() => LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
}
