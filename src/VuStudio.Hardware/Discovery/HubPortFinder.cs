using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace VuStudio.Hardware.Discovery;

/// <summary>
/// Finds serial ports whose USB device is an FTDI FT231X (VID <c>0x0403</c>, PID <c>0x6015</c>), the VU1 hub's
/// bridge. Windows: device registry keys intersected with the ports present now. Linux: sysfs. macOS:
/// <c>/dev/cu.usbserial-*</c>. See <c>docs/integrations/windows-sources.md</c> §10.
/// </summary>
public sealed class HubPortFinder : IHubPortLocator
{
    public const int VendorId = 0x0403;

    public const int ProductId = 0x6015;

    /// <summary>The configuration value that asks for discovery.</summary>
    public const string AutoPort = "auto";

    private readonly ILogger _logger;

    public HubPortFinder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Root of sysfs on Linux; replaceable for tests.</summary>
    public string SysfsRoot { get; init; } = "/sys";

    /// <summary>Directory of device nodes: prefix of Linux port names and where macOS ports are listed.</summary>
    public string DeviceRoot { get; init; } = "/dev";

    /// <summary>True when <paramref name="port"/> asks for discovery (empty or "auto").</summary>
    public static bool IsAuto(string? port) =>
        string.IsNullOrWhiteSpace(port) || string.Equals(port.Trim(), AutoPort, StringComparison.OrdinalIgnoreCase);

    /// <summary>A configured port ("COM5", "/dev/ttyUSB0") overrides discovery; "auto" asks <paramref name="locator"/>.</summary>
    public static IReadOnlyList<HubPortCandidate> Resolve(string? configuredPort, IHubPortLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return IsAuto(configuredPort) ? locator.FindCandidates() : [new HubPortCandidate(configuredPort!.Trim(), "configured port")];
    }

    public IReadOnlyList<HubPortCandidate> Resolve(string? configuredPort) => Resolve(configuredPort, this);

    public IReadOnlyList<HubPortCandidate> FindCandidates()
    {
        try
        {
            var candidates = OperatingSystem.IsWindows() ? FindWindows()
                : OperatingSystem.IsMacOS() ? FindMacOS()
                : FindLinux();
            foreach (var candidate in candidates)
            {
                _logger.LogDebug("VU1 hub candidate {Port}: {Description}", candidate.PortName, candidate.Description);
            }

            return candidates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Looking for the VU1 hub failed");
            return [];
        }
    }

    /// <summary>
    /// Linux: for each <c>/sys/class/tty/ttyUSB*</c> and <c>ttyACM*</c>, resolves the <c>device</c> link and walks up
    /// to the USB device directory that holds <c>idVendor</c> and <c>idProduct</c>.
    /// </summary>
    internal IReadOnlyList<HubPortCandidate> FindLinux()
    {
        var ttyClass = Path.Combine(SysfsRoot, "class", "tty");
        if (!Directory.Exists(ttyClass))
        {
            return [];
        }

        var root = ResolvePhysicalPath(SysfsRoot) ?? Path.GetFullPath(SysfsRoot);
        var found = new List<HubPortCandidate>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(ttyClass))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith("ttyUSB", StringComparison.Ordinal) && !name.StartsWith("ttyACM", StringComparison.Ordinal))
            {
                continue;
            }

            var device = ResolvePhysicalPath(Path.Combine(entry, "device"));
            var usbDevice = device is null ? null : FindUsbDevice(device, root);
            if (usbDevice is null || ReadHex(usbDevice, "idVendor") != VendorId || ReadHex(usbDevice, "idProduct") != ProductId)
            {
                continue;
            }

            found.Add(new HubPortCandidate(
                Path.Combine(DeviceRoot, name),
                Describe(ReadText(usbDevice, "manufacturer"), ReadText(usbDevice, "product"), ReadText(usbDevice, "serial"))));
        }

        return [.. found.OrderBy(c => c.PortName, StringComparer.Ordinal)];
    }

    /// <summary>macOS: the FTDI callout devices. Exact VID/PID would need IOKit; the probe confirms the hub.</summary>
    internal IReadOnlyList<HubPortCandidate> FindMacOS()
    {
        if (!Directory.Exists(DeviceRoot))
        {
            return [];
        }

        return [.. Directory.EnumerateFileSystemEntries(DeviceRoot, "cu.usbserial-*")
            .Order(StringComparer.Ordinal)
            .Select(path => new HubPortCandidate(path, $"USB serial adapter {Path.GetFileName(path)["cu.usbserial-".Length..]}"))];
    }

    /// <summary>True for registry device keys of the hub's bridge (<c>VID_0403+PID_6015+…</c>, <c>VID_0403&amp;PID_6015…</c>).</summary>
    internal static bool MatchesHardwareId(string keyName) =>
        keyName.Contains($"VID_{VendorId:X4}", StringComparison.OrdinalIgnoreCase) &&
        keyName.Contains($"PID_{ProductId:X4}", StringComparison.OrdinalIgnoreCase);

    /// <summary>The USB serial number in an FTDIBUS key such as <c>VID_0403+PID_6015+DQ0164KAA</c> (channel letter removed).</summary>
    internal static string? SerialFromFtdiBusKey(string keyName)
    {
        var parts = keyName.Split('+');
        if (parts.Length < 3 || parts[2].Length == 0)
        {
            return null;
        }

        var serial = parts[2];
        return serial.Length > 1 && serial.EndsWith('A') ? serial[..^1] : serial;
    }

    /// <summary>
    /// Resolves every symbolic link along <paramref name="path"/> component by component, like <c>realpath</c>.
    /// sysfs links are relative to the directory they live in, which is itself reached through links, so
    /// a purely textual <c>..</c> would go wrong. Returns null on a missing component or a link loop.
    /// </summary>
    internal static string? ResolvePhysicalPath(string path)
    {
        var pending = new Stack<string>();
        Push(pending, Path.GetFullPath(path));
        var resolved = Path.GetPathRoot(Path.GetFullPath(path)) ?? "/";
        var links = 0;
        while (pending.TryPop(out var part))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var next = Path.Join(resolved, part);
            string? target;
            try
            {
                var info = new FileInfo(next);
                if (!info.Exists && !Directory.Exists(next))
                {
                    return null;
                }

                target = info.LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (target is null)
            {
                resolved = next;
                continue;
            }

            if (++links > 40)
            {
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                resolved = Path.GetPathRoot(target) ?? "/";
            }

            Push(pending, target);
        }

        return resolved;

        static void Push(Stack<string> stack, string value)
        {
            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                stack.Push(parts[i]);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private List<HubPortCandidate> FindWindows()
    {
        // Enum keys persist for unplugged devices: keep only ports that exist now (HARDWARE\DEVICEMAP\SERIALCOMM).
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, HubPortCandidate>(StringComparer.OrdinalIgnoreCase);
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey is null)
        {
            return [];
        }

        // FTDI VCP driver: FTDIBUS\VID_0403+PID_6015+<serial>A\0000\Device Parameters\PortName.
        using (var ftdiBus = enumKey.OpenSubKey("FTDIBUS"))
        {
            foreach (var deviceName in ftdiBus?.GetSubKeyNames() ?? [])
            {
                if (MatchesHardwareId(deviceName))
                {
                    using var device = ftdiBus!.OpenSubKey(deviceName);
                    var serial = SerialFromFtdiBusKey(deviceName);
                    AddInstances(device, _ => Describe("FTDI", "FT231X", serial));
                }
            }
        }

        // Generic USB serial driver: USB\VID_0403&PID_6015[&MI_xx]\<serial>\Device Parameters\PortName.
        using (var usb = enumKey.OpenSubKey("USB"))
        {
            foreach (var deviceName in usb?.GetSubKeyNames() ?? [])
            {
                if (MatchesHardwareId(deviceName))
                {
                    using var device = usb!.OpenSubKey(deviceName);
                    AddInstances(device, instance => Describe("FTDI", "FT231X", instance.Contains('&') ? null : instance));
                }
            }
        }

        return [.. found.Values.OrderBy(c => c.PortName, StringComparer.OrdinalIgnoreCase)];

        void AddInstances(RegistryKey? device, Func<string, string> describe)
        {
            foreach (var instanceName in device?.GetSubKeyNames() ?? [])
            {
                using var instance = device!.OpenSubKey(instanceName);
                using var parameters = instance?.OpenSubKey("Device Parameters");
                if (parameters?.GetValue("PortName") is not string portName || string.IsNullOrWhiteSpace(portName))
                {
                    continue;
                }

                portName = portName.Trim();
                if (!present.Contains(portName))
                {
                    _logger.LogDebug("Ignoring {Port}: registered for an FT231X but not present", portName);
                    continue;
                }

                var friendlyName = instance!.GetValue("FriendlyName") as string;
                var description = describe(instanceName);
                found.TryAdd(portName, new HubPortCandidate(portName, string.IsNullOrWhiteSpace(friendlyName) ? description : $"{friendlyName} — {description}"));
            }
        }
    }

    private static string? FindUsbDevice(string start, string root)
    {
        var prefix = root.EndsWith('/') ? root : root + "/";
        for (var dir = start; dir is not null && dir.StartsWith(prefix, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "idVendor")) && File.Exists(Path.Combine(dir, "idProduct")))
            {
                return dir;
            }
        }

        return null;
    }

    private static int ReadHex(string directory, string file) =>
        int.TryParse(ReadText(directory, file), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static string? ReadText(string directory, string file)
    {
        try
        {
            var path = Path.Combine(directory, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(string? manufacturer, string? product, string? serial)
    {
        var name = string.Join(' ', new[] { manufacturer, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (name.Length == 0)
        {
            name = $"USB serial {VendorId:x4}:{ProductId:x4}";
        }

        return string.IsNullOrWhiteSpace(serial) ? name : $"{name}, serial {serial}";
    }
}
