using VuStudio.Hardware.Discovery;

namespace VuStudio.Tests.Hardware;

public sealed class HubPortFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vustudio-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Linux_finds_ft231x_ports_through_sysfs_links()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Creating symbolic links needs a Unix file system.");
        var sys = Path.Combine(_root, "sys");

        // ftdi_sio: class/tty/ttyUSB0 -> devices/.../1-2:1.0/ttyUSB0/tty/ttyUSB0, whose device -> ../../../ttyUSB0.
        AddUsbDevice(sys, "usb1/1-2", "0403", "6015", manufacturer: "FTDI", product: "FT231X USB UART", serial: "DQ0164KA");
        AddUsbSerialTty(sys, "ttyUSB0", "usb1/1-2/1-2:1.0");

        // Another USB-serial adapter (CH340) is ignored.
        AddUsbDevice(sys, "usb1/1-3", "1a86", "7523", manufacturer: null, product: "USB Serial", serial: null);
        AddUsbSerialTty(sys, "ttyUSB1", "usb1/1-3/1-3:1.0");

        // cdc_acm: class/tty/ttyACM0 -> devices/.../2-1:1.0/tty/ttyACM0, whose device -> ../../../2-1:1.0.
        AddUsbDevice(sys, "usb2/2-1", "0403", "6015", manufacturer: null, product: null, serial: null);
        Directory.CreateDirectory(Path.Combine(sys, "devices/pci0000:00/usb2/2-1/2-1:1.0/tty/ttyACM0"));
        Link(Path.Combine(sys, "devices/pci0000:00/usb2/2-1/2-1:1.0/tty/ttyACM0/device"), "../../../2-1:1.0");
        Link(Path.Combine(sys, "class/tty/ttyACM0"), "../../devices/pci0000:00/usb2/2-1/2-1:1.0/tty/ttyACM0");

        // A platform UART and a tty without a device link are ignored.
        Directory.CreateDirectory(Path.Combine(sys, "devices/platform/serial8250/tty/ttyS0"));
        Link(Path.Combine(sys, "class/tty/ttyS0"), "../../devices/platform/serial8250/tty/ttyS0");
        Directory.CreateDirectory(Path.Combine(sys, "devices/virtual/tty/ttyUSB9"));
        Link(Path.Combine(sys, "class/tty/ttyUSB9"), "../../devices/virtual/tty/ttyUSB9");

        var finder = new HubPortFinder { SysfsRoot = sys };
        var candidates = finder.FindLinux();

        Assert.Equal(["/dev/ttyACM0", "/dev/ttyUSB0"], candidates.Select(c => c.PortName));
        Assert.Equal("FTDI FT231X USB UART, serial DQ0164KA", candidates[1].Description);
        Assert.Equal("USB serial 0403:6015", candidates[0].Description);
    }

    [Fact]
    public void Linux_without_sysfs_finds_nothing() =>
        Assert.Empty(new HubPortFinder { SysfsRoot = Path.Combine(_root, "missing") }.FindLinux());

    [Fact]
    public void MacOS_lists_ftdi_callout_devices()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "cu.usbserial-DQ0164KA", "tty.usbserial-DQ0164KA", "cu.Bluetooth-Incoming-Port", "cu.usbserial-A1" })
        {
            File.WriteAllText(Path.Combine(_root, name), "");
        }

        var candidates = new HubPortFinder { DeviceRoot = _root }.FindMacOS();

        Assert.Equal([Path.Combine(_root, "cu.usbserial-A1"), Path.Combine(_root, "cu.usbserial-DQ0164KA")], candidates.Select(c => c.PortName));
        Assert.Contains("DQ0164KA", candidates[1].Description);
    }

    [Theory]
    [InlineData("COM5")]
    [InlineData(" /dev/ttyUSB0 ")]
    public void A_configured_port_overrides_discovery(string port)
    {
        var locator = new FixedPortLocator(new HubPortCandidate("COM9", "discovered"));
        var candidates = HubPortFinder.Resolve(port, locator);
        Assert.Equal([port.Trim()], candidates.Select(c => c.PortName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public void Auto_asks_the_locator(string? port)
    {
        var locator = new FixedPortLocator(new HubPortCandidate("COM9", "discovered"));
        Assert.True(HubPortFinder.IsAuto(port));
        Assert.Equal(["COM9"], HubPortFinder.Resolve(port, locator).Select(c => c.PortName));
    }

    [Theory]
    [InlineData("VID_0403+PID_6015+DQ0164KAA", true)]
    [InlineData("vid_0403&pid_6015&mi_00", true)]
    [InlineData("COMPORT&VID_0403&PID_6015", true)]
    [InlineData("VID_0403+PID_6001+A600XYZA", false)]
    [InlineData("VID_1A86&PID_7523", false)]
    public void Matches_registry_keys_of_the_hub_bridge(string key, bool expected) =>
        Assert.Equal(expected, HubPortFinder.MatchesHardwareId(key));

    [Theory]
    [InlineData("VID_0403+PID_6015+DQ0164KAA", "DQ0164KA")]
    [InlineData("VID_0403+PID_6015+DQ0164KA", "DQ0164K")]
    [InlineData("VID_0403+PID_6015", null)]
    [InlineData("COMPORT&VID_0403&PID_6015", null)]
    public void Reads_the_serial_from_ftdibus_keys(string key, string? serial) =>
        Assert.Equal(serial, HubPortFinder.SerialFromFtdiBusKey(key));

    private static void AddUsbDevice(string sys, string path, string idVendor, string idProduct, string? manufacturer, string? product, string? serial)
    {
        var device = Path.Combine(sys, "devices/pci0000:00", path);
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "idVendor"), idVendor + "\n");
        File.WriteAllText(Path.Combine(device, "idProduct"), idProduct + "\n");
        if (manufacturer is not null)
        {
            File.WriteAllText(Path.Combine(device, "manufacturer"), manufacturer + "\n");
        }

        if (product is not null)
        {
            File.WriteAllText(Path.Combine(device, "product"), product + "\n");
        }

        if (serial is not null)
        {
            File.WriteAllText(Path.Combine(device, "serial"), serial + "\n");
        }
    }

    private static void AddUsbSerialTty(string sys, string name, string interfacePath)
    {
        var port = $"devices/pci0000:00/{interfacePath}/{name}";
        Directory.CreateDirectory(Path.Combine(sys, port, "tty", name));
        Link(Path.Combine(sys, port, "tty", name, "device"), $"../../../{name}");
        Link(Path.Combine(sys, "class/tty", name), $"../../{port}/tty/{name}");
    }

    private static void Link(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }
}
