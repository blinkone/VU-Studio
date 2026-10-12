using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.SystemMetrics;
using VuStudio.Sources.SystemMetrics.Linux;

namespace VuStudio.Tests.Sources;

/// <summary>A collector under test together with a fake root, a hub and a clock the test moves by hand.</summary>
internal sealed class CollectorFixture : IDisposable
{
    public CollectorFixture()
    {
        Root = new TempDirectory("fake-linux");
        Hub = new SensorHub();
        Sensors = new SensorRegistry(Hub.CreateSink("system"), 60);
    }

    public TempDirectory Root { get; }

    public SensorHub Hub { get; }

    public SensorRegistry Sensors { get; }

    public void Collect(ISystemCollector collector, double seconds) =>
        collector.Collect(Sensors, new SampleContext(TimeSpan.FromSeconds(seconds), DateTimeOffset.UnixEpoch.AddSeconds(seconds)));

    public double? Number(string localId) => Hub.GetNumber("system/" + localId);

    public SensorInfo Info(string localId) => Hub.Get("system/" + localId)!.Info;

    public void Dispose() => Root.Dispose();
}

public class ProcParserTests
{
    [Fact]
    public void Stat_busy_excludes_idle_and_iowait_and_does_not_double_count_guest()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var cpus = ProcParsers.ParseStat("cpu  100 10 20 700 50 5 15 100 999 999\ncpu0 1 2 3 4 5 6 7 8\nintr 12345\nctxt 5\n");

        Assert.Equal(["cpu", "cpu0"], cpus.Select(c => c.Name));
        Assert.Equal(100 + 10 + 20 + 5 + 15 + 100, cpus[0].Busy);
        Assert.Equal(cpus[0].Busy + 700 + 50, cpus[0].Total);
        Assert.Equal(1 + 2 + 3 + 6 + 7 + 8, cpus[1].Busy);
    }

    [Fact]
    public void Stat_tolerates_short_lines_and_garbage()
    {
        Assert.Empty(ProcParsers.ParseStat(null));
        Assert.Empty(ProcParsers.ParseStat("cpu 1 2"));
        var old = ProcParsers.ParseStat("cpu 10 0 5 85"); // kernels without iowait and later fields
        Assert.Equal(15, old[0].Busy);
        Assert.Equal(100, old[0].Total);
    }

    [Fact]
    public void Meminfo_values_are_bytes()
    {
        var info = ProcParsers.ParseMemInfo("MemTotal:       16384 kB\nHugePages_Total:       4\nSwapFree:  0 kB\nbroken line\n");
        Assert.Equal(16384L * 1024, info["MemTotal"]);
        Assert.Equal(4, info["HugePages_Total"]);
        Assert.Equal(0, info["SwapFree"]);
    }

    [Fact]
    public void Loadavg_and_cpuinfo()
    {
        Assert.Equal([1.5, 1.2, 0.9], ProcParsers.ParseLoadAverage("1.50 1.20 0.90 2/512 4242")!);
        Assert.Null(ProcParsers.ParseLoadAverage("nonsense"));
        Assert.Equal("AMD Ryzen 7 7800X3D 8-Core Processor", ProcParsers.ParseCpuModel("processor : 0\nmodel name\t: AMD Ryzen 7 7800X3D 8-Core Processor\n"));
        Assert.Equal("Raspberry Pi 4", ProcParsers.ParseCpuModel("processor : 0\nHardware : Raspberry Pi 4\n"));
        Assert.Null(ProcParsers.ParseCpuModel("nothing useful"));
    }

    [Fact]
    public void Diskstats_columns()
    {
        var disks = ProcParsers.ParseDiskStats("   8       0 sda 100 0 2048 50 200 0 4096 80 0 500 130 0 0 0 0\nshort line\n");
        var sda = Assert.Single(disks);
        Assert.Equal(new ProcParsers.DiskCounters("sda", 2048, 4096, 500), sda);
    }
}

public class LinuxCpuCollectorTests
{
    private const string FirstStat = """
        cpu  1000 0 500 8000 500 0 0 0 0 0
        cpu0 500 0 250 4000 250 0 0 0 0 0
        cpu1 500 0 250 4000 250 0 0 0 0 0
        intr 1
        """;

    private const string SecondStat = """
        cpu  1200 0 600 8400 700 0 0 0 0 0
        cpu0 650 0 250 4100 250 0 0 0 0 0
        cpu1 500 0 250 4400 250 0 0 0 0 0
        intr 2
        """;

    [Fact]
    public void Load_is_the_busy_share_of_the_time_between_two_readings()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", FirstStat);
        f.Root.Write("proc/cpuinfo", "model name : Test CPU 9000\n");
        using var cpu = new LinuxCpuCollector(f.Root.Path, perCore: true);

        f.Collect(cpu, 0);
        Assert.Null(f.Number("cpu/load")); // a rate needs two readings

        f.Root.Write("proc/stat", SecondStat);
        f.Collect(cpu, 1);

        Assert.Equal(100.0 * 300 / 900, f.Number("cpu/load")!.Value, 9); // busy +300 of +900
        Assert.Equal(60, f.Number("cpu/0/load")!.Value, 9);               // busy +150 of +250
        Assert.Equal(0, f.Number("cpu/1/load")!.Value, 9);                // busy +0 of +400

        var info = f.Info("cpu/load");
        Assert.Equal("Test CPU 9000", info.Device);
        Assert.Equal(SensorCategory.Cpu, info.Category);
        Assert.Equal(SensorKinds.Load, info.Kind);
        Assert.Equal(Units.Percent, info.Unit);
        Assert.Equal(0, info.Min);
        Assert.Equal(100, info.Max);
        Assert.False(string.IsNullOrWhiteSpace(info.Icon));
    }

    [Fact]
    public void A_reading_with_no_elapsed_cpu_time_is_ignored_and_load_follows_later_deltas()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", FirstStat);
        using var cpu = new LinuxCpuCollector(f.Root.Path, true);
        f.Collect(cpu, 0);
        f.Collect(cpu, 1); // unchanged counters: nothing to publish
        Assert.Null(f.Number("cpu/load"));

        f.Root.Write("proc/stat", SecondStat);
        f.Collect(cpu, 2);
        Assert.Equal(100.0 * 300 / 900, f.Number("cpu/load")!.Value, 9);
    }

    [Fact]
    public void Per_core_sensors_can_be_switched_off()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", FirstStat);
        f.Root.Write("sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "3600000\n");
        using var cpu = new LinuxCpuCollector(f.Root.Path, perCore: false);
        f.Collect(cpu, 0);
        f.Root.Write("proc/stat", SecondStat);
        f.Collect(cpu, 1);

        Assert.NotNull(f.Number("cpu/load"));
        Assert.NotNull(f.Number("cpu/freq"));
        Assert.Null(f.Hub.Get("system/cpu/0/load"));
        Assert.Null(f.Hub.Get("system/cpu/0/freq"));
    }

    [Fact]
    public void Clock_speed_is_converted_from_kilohertz_to_megahertz()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", FirstStat);
        f.Root.Write("sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "3600000\n");
        f.Root.Write("sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "5000000\n");
        f.Root.Write("sys/devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "4200000\n");
        using var cpu = new LinuxCpuCollector(f.Root.Path, true);
        f.Collect(cpu, 0);

        Assert.Equal(3600, f.Number("cpu/0/freq"));
        Assert.Equal(4200, f.Number("cpu/1/freq"));
        Assert.Equal(3900, f.Number("cpu/freq"));
        Assert.Equal(Units.MHz, f.Info("cpu/0/freq").Unit);
        Assert.Equal(SensorKinds.Clock, f.Info("cpu/0/freq").Kind);
        Assert.Equal(5000, f.Info("cpu/0/freq").Max);
        Assert.Equal(5000, f.Info("cpu/freq").Max);
    }

    [Fact]
    public void Load_average_is_scaled_to_the_core_count()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", FirstStat);
        f.Root.Write("proc/loadavg", "1.50 1.20 0.90 2/512 4242\n");
        using var cpu = new LinuxCpuCollector(f.Root.Path, true);
        f.Collect(cpu, 0);

        Assert.Equal(1.5, f.Number("loadavg/1"));
        Assert.Equal(1.2, f.Number("loadavg/5"));
        Assert.Equal(0.9, f.Number("loadavg/15"));
        Assert.Equal(2, f.Info("loadavg/1").Max); // two cores
    }

    [Fact]
    public void Missing_files_are_not_an_error()
    {
        using var f = new CollectorFixture();
        using var cpu = new LinuxCpuCollector(f.Root.Path, true);
        f.Collect(cpu, 0);
        f.Collect(cpu, 1);
        Assert.Equal(0, f.Hub.Count);
    }
}

public class LinuxMemoryAndDiskCollectorTests
{
    [Fact]
    public void Memory_uses_memavailable()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/meminfo", "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    4000000 kB\nSwapTotal:       4000000 kB\nSwapFree:        3000000 kB\n");
        using var memory = new LinuxMemoryCollector(f.Root.Path);
        f.Collect(memory, 0);

        Assert.Equal(16_000_000.0 * 1024, f.Number("mem/total"));
        Assert.Equal(4_000_000.0 * 1024, f.Number("mem/available"));
        Assert.Equal(12_000_000.0 * 1024, f.Number("mem/used"));
        Assert.Equal(75, f.Number("mem/usage")!.Value, 9);
        Assert.Equal(1_000_000.0 * 1024, f.Number("swap/used"));
        Assert.Equal(4_000_000.0 * 1024, f.Number("swap/total"));
        Assert.Equal(25, f.Number("swap/usage")!.Value, 9);

        var usage = f.Info("mem/usage");
        Assert.Equal(SensorCategory.Memory, usage.Category);
        Assert.Equal(Units.Percent, usage.Unit);
        Assert.Equal(Units.Bytes, f.Info("mem/used").Unit);
        Assert.Equal(16_000_000.0 * 1024, f.Info("mem/used").Max);
    }

    [Fact]
    public void Old_kernels_without_memavailable_get_the_classic_estimate_and_no_swap_means_no_swap_sensors()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/meminfo", "MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 150 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n");
        using var memory = new LinuxMemoryCollector(f.Root.Path);
        f.Collect(memory, 0);

        Assert.Equal(300.0 * 1024, f.Number("mem/available"));
        Assert.Equal(70, f.Number("mem/usage")!.Value, 9);
        Assert.Null(f.Hub.Get("system/swap/usage"));
    }

    [Fact]
    public void Garbage_or_missing_meminfo_publishes_nothing()
    {
        using var f = new CollectorFixture();
        using var memory = new LinuxMemoryCollector(f.Root.Path);
        f.Collect(memory, 0);
        f.Root.Write("proc/meminfo", "nonsense");
        f.Collect(memory, 1);
        Assert.Equal(0, f.Hub.Count);
    }

    private const string Disks = """
           8       0 sda 100 0 2048 50 200 0 4096 80 0 500 130 0 0 0 0
           8       1 sda1 90 0 1000 40 100 0 1000 40 0 400 80 0 0 0 0
         259       0 nvme0n1 10 0 100 5 10 0 100 5 0 10 10 0 0 0 0
         259       1 nvme0n1p1 10 0 100 5 10 0 100 5 0 10 10 0 0 0 0
           7       0 loop0 1 0 8 0 0 0 0 0 0 0 0 0 0 0 0
         253       0 dm-0 5 0 50 2 5 0 50 2 0 5 4 0 0 0 0
          11       0 sr0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
         251       0 zram0 1 0 8 0 1 0 8 0 0 0 0 0 0 0 0
        """;

    [Fact]
    public void Disk_rates_come_from_512_byte_sectors_and_partitions_and_loops_are_skipped()
    {
        using var f = new CollectorFixture();
        f.Root.CreateDirectory("sys/block/sda");
        f.Root.CreateDirectory("sys/block/nvme0n1");
        f.Root.CreateDirectory("sys/block/dm-0");
        f.Root.CreateDirectory("sys/block/loop0");
        f.Root.Write("proc/diskstats", Disks);
        using var io = new LinuxDiskIoCollector(f.Root.Path);

        f.Collect(io, 0);
        Assert.Null(f.Number("diskio/sda/read"));

        // sda: +2048 sectors read, +1024 written, +500 ms busy, one second later.
        f.Root.Write("proc/diskstats", Disks.Replace("sda 100 0 2048 50 200 0 4096 80 0 500", "sda 100 0 4096 50 200 0 5120 80 0 1000"));
        f.Collect(io, 1);

        Assert.Equal(2048 * 512, f.Number("diskio/sda/read"));
        Assert.Equal(1024 * 512, f.Number("diskio/sda/write"));
        Assert.Equal(50, f.Number("diskio/sda/busy")!.Value, 9);
        Assert.Equal(0, f.Number("diskio/nvme0n1/read"));
        Assert.Equal(0, f.Number("diskio/dm-0/write"));

        var ids = f.Hub.GetCatalog().Select(i => i.Id.Split('/')[2]).Distinct().Order().ToList();
        Assert.Equal(["dm-0", "nvme0n1", "sda"], ids); // no sda1, nvme0n1p1, loop0, sr0, zram0

        var info = f.Info("diskio/sda/read");
        Assert.Equal(SensorCategory.Storage, info.Category);
        Assert.Equal(Units.BytesPerSecond, info.Unit);
        Assert.Equal(SensorKinds.Throughput, info.Kind);
        Assert.Equal("sda", info.Device);
    }

    [Fact]
    public void Without_sys_block_partitions_are_recognised_by_name()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/diskstats", Disks + "\n 179 0 mmcblk0 1 0 8 0 1 0 8 0 0 0 0 0 0 0 0\n 179 1 mmcblk0p1 1 0 8 0 1 0 8 0 0 0 0 0 0 0 0\n");
        using var io = new LinuxDiskIoCollector(f.Root.Path);
        f.Collect(io, 0);
        f.Collect(io, 1);

        var ids = f.Hub.GetCatalog().Select(i => i.Id.Split('/')[2]).Distinct().Order().ToList();
        Assert.Equal(["dm-0", "mmcblk0", "nvme0n1", "sda"], ids);
    }

    [Fact]
    public void A_disk_that_disappears_is_removed()
    {
        using var f = new CollectorFixture();
        f.Root.CreateDirectory("sys/block/sda");
        f.Root.CreateDirectory("sys/block/sdb");
        f.Root.Write("proc/diskstats", " 8 0 sda 1 0 8 0 1 0 8 0 0 1 1 0 0 0 0\n 8 16 sdb 1 0 8 0 1 0 8 0 0 1 1 0 0 0 0\n");
        using var io = new LinuxDiskIoCollector(f.Root.Path);
        f.Collect(io, 0);
        Assert.NotNull(f.Hub.Get("system/diskio/sdb/read"));

        f.Root.Write("proc/diskstats", " 8 0 sda 1 0 8 0 1 0 8 0 0 1 1 0 0 0 0\n");
        f.Collect(io, 1);
        Assert.Null(f.Hub.Get("system/diskio/sdb/read"));
        Assert.NotNull(f.Hub.Get("system/diskio/sda/read"));
    }
}

public class LinuxHwmonCollectorTests
{
    private static void BuildTree(TempDirectory root)
    {
        root.Write("sys/class/hwmon/hwmon0/name", "k10temp\n");
        root.Write("sys/class/hwmon/hwmon0/temp1_input", "45125\n");
        root.Write("sys/class/hwmon/hwmon0/temp1_label", "Tctl\n");
        root.Write("sys/class/hwmon/hwmon0/temp3_input", "0\n"); // an unconnected channel
        root.Write("sys/class/hwmon/hwmon0/temp3_label", "Tccd1\n");

        root.Write("sys/class/hwmon/hwmon1/name", "nct6798\n");
        root.Write("sys/class/hwmon/hwmon1/fan1_input", "1200\n");
        root.Write("sys/class/hwmon/hwmon1/fan2_input", "0\n");
        root.Write("sys/class/hwmon/hwmon1/in0_input", "1008\n");
        root.Write("sys/class/hwmon/hwmon1/in1_input", "3300\n");
        root.Write("sys/class/hwmon/hwmon1/temp1_input", "38000\n");
        root.Write("sys/class/hwmon/hwmon1/temp1_label", "SYSTIN\n");

        root.Write("sys/class/hwmon/hwmon2/name", "amdgpu\n");
        root.Write("sys/class/hwmon/hwmon2/temp1_input", "61000\n");
        root.Write("sys/class/hwmon/hwmon2/temp1_label", "edge\n");
        root.Write("sys/class/hwmon/hwmon2/power1_average", "85000000\n");
        root.Write("sys/class/hwmon/hwmon2/power1_input", "90000000\n");
        root.Write("sys/class/hwmon/hwmon2/freq1_input", "1500000000\n");
        root.Write("sys/class/hwmon/hwmon2/curr1_input", "2500\n");

        root.Write("sys/class/hwmon/hwmon3/name", "nvme\n");
        root.Write("sys/class/hwmon/hwmon3/temp1_input", "38850\n");
        root.Write("sys/class/hwmon/hwmon3/temp1_label", "Composite\n");
        root.Write("sys/class/hwmon/hwmon4/name", "nvme\n");
        root.Write("sys/class/hwmon/hwmon4/temp1_input", "41000\n");

        root.Write("sys/class/hwmon/hwmon5/name", "mystery\n");
        root.Write("sys/class/hwmon/hwmon5/temp1_input", "30000\n");
        root.Write("sys/class/hwmon/hwmon5/power1_input", "5000000\n");

        root.Write("sys/class/hwmon/hwmon10/name", "acpitz\n");
        root.Write("sys/class/hwmon/hwmon10/temp1_input", "27800\n");
    }

    [Fact]
    public void Converts_every_kind_of_channel_to_canonical_units()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);

        Assert.Equal(45.125, f.Number("hwmon/k10temp/temp1")!.Value, 9);   // m°C -> °C
        Assert.Equal(1200, f.Number("hwmon/nct6798/fan1"));                  // RPM
        Assert.Equal(0, f.Number("hwmon/nct6798/fan2"));                     // a stopped fan is a valid reading
        Assert.Equal(1.008, f.Number("hwmon/nct6798/in0")!.Value, 9);        // mV -> V
        Assert.Equal(3.3, f.Number("hwmon/nct6798/in1")!.Value, 9);
        Assert.Equal(38, f.Number("hwmon/nct6798/temp1")!.Value, 9);
        Assert.Equal(61, f.Number("hwmon/amdgpu/temp1")!.Value, 9);
        Assert.Equal(85, f.Number("hwmon/amdgpu/power1")!.Value, 9);         // µW -> W, the _average file wins
        Assert.Equal(1500, f.Number("hwmon/amdgpu/freq1")!.Value, 9);        // Hz -> MHz
        Assert.Equal(2.5, f.Number("hwmon/amdgpu/curr1")!.Value, 9);         // mA -> A
        Assert.Equal(5, f.Number("hwmon/mystery/power1")!.Value, 9);         // _input when there is no _average
        Assert.Equal(27.8, f.Number("hwmon/acpitz/temp1")!.Value, 9);
    }

    [Fact]
    public void Implausible_readings_are_invalid_rather_than_zero_degrees()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);
        Assert.Null(f.Number("hwmon/k10temp/temp3"));
    }

    [Fact]
    public void Sensors_get_names_devices_categories_and_ranges()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);

        var tctl = f.Info("hwmon/k10temp/temp1");
        Assert.Equal("Tctl", tctl.Name);
        Assert.Equal("AMD CPU", tctl.Device);
        Assert.Equal(SensorCategory.Cpu, tctl.Category);
        Assert.Equal(Units.Celsius, tctl.Unit);
        Assert.Equal(SensorKinds.Temperature, tctl.Kind);
        Assert.Equal(0, tctl.Min);
        Assert.Equal(105, tctl.Max);

        Assert.Equal("Fan 1", f.Info("hwmon/nct6798/fan1").Name); // no label file
        Assert.Equal(SensorCategory.Cooling, f.Info("hwmon/nct6798/fan1").Category);
        Assert.Equal(Units.Rpm, f.Info("hwmon/nct6798/fan1").Unit);
        Assert.Equal("Voltage 0", f.Info("hwmon/nct6798/in0").Name);
        Assert.Equal(Units.Volt, f.Info("hwmon/nct6798/in0").Unit);
        Assert.Equal(SensorCategory.Motherboard, f.Info("hwmon/nct6798/temp1").Category);

        Assert.Equal(SensorCategory.Gpu, f.Info("hwmon/amdgpu/temp1").Category);
        Assert.Equal(SensorCategory.Gpu, f.Info("hwmon/amdgpu/power1").Category);
        Assert.Equal(Units.Watt, f.Info("hwmon/amdgpu/power1").Unit);
        Assert.Equal(Units.MHz, f.Info("hwmon/amdgpu/freq1").Unit);
        Assert.Equal(Units.Ampere, f.Info("hwmon/amdgpu/curr1").Unit);

        Assert.Equal(SensorCategory.Storage, f.Info("hwmon/nvme/temp1").Category);
        Assert.Equal(85, f.Info("hwmon/nvme/temp1").Max);
        Assert.Equal("Composite", f.Info("hwmon/nvme/temp1").Name);

        Assert.Equal(SensorCategory.System, f.Info("hwmon/mystery/temp1").Category);
        Assert.Equal(SensorCategory.Power, f.Info("hwmon/mystery/power1").Category);
        Assert.Equal(SensorCategory.Motherboard, f.Info("hwmon/acpitz/temp1").Category);
        Assert.All(f.Hub.GetCatalog(), info => Assert.False(string.IsNullOrWhiteSpace(info.Icon)));
    }

    [Fact]
    public void Chips_with_the_same_name_get_numbered_ids_and_devices()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);

        Assert.Equal(38.85, f.Number("hwmon/nvme/temp1")!.Value, 9);
        Assert.Equal(41, f.Number("hwmon/nvme-2/temp1")!.Value, 9);
        Assert.Equal("NVMe drive", f.Info("hwmon/nvme/temp1").Device);
        Assert.Equal("NVMe drive 2", f.Info("hwmon/nvme-2/temp1").Device);
        Assert.Equal("Temperature 1", f.Info("hwmon/nvme-2/temp1").Name);
    }

    [Fact]
    public void A_suspended_device_is_not_read_so_it_can_keep_sleeping()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);
        Assert.Equal(61, f.Number("hwmon/amdgpu/temp1"));

        f.Root.Write("sys/class/hwmon/hwmon2/device/power/runtime_status", "suspended\n");
        f.Root.Write("sys/class/hwmon/hwmon2/temp1_input", "99000\n");
        f.Collect(hwmon, 1);
        Assert.Equal(61, f.Number("hwmon/amdgpu/temp1")); // untouched

        f.Root.Write("sys/class/hwmon/hwmon2/device/power/runtime_status", "active\n");
        f.Collect(hwmon, 2);
        Assert.Equal(99, f.Number("hwmon/amdgpu/temp1"));
    }

    [Fact]
    public void Chips_are_rescanned_every_thirty_seconds()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);

        f.Root.Write("sys/class/hwmon/hwmon6/name", "drivetemp\n");
        f.Root.Write("sys/class/hwmon/hwmon6/temp1_input", "33000\n");
        Directory.Delete(Path.Combine(f.Root.Path, "sys/class/hwmon/hwmon5"), recursive: true);

        f.Collect(hwmon, 10);
        Assert.Null(f.Hub.Get("system/hwmon/drivetemp/temp1")); // not yet
        Assert.NotNull(f.Hub.Get("system/hwmon/mystery/temp1"));

        f.Collect(hwmon, 31);
        Assert.Equal(33, f.Number("hwmon/drivetemp/temp1"));
        Assert.Equal(SensorCategory.Storage, f.Info("hwmon/drivetemp/temp1").Category);
        Assert.Null(f.Hub.Get("system/hwmon/mystery/temp1"));
    }

    [Fact]
    public void A_channel_that_stops_answering_goes_offline()
    {
        using var f = new CollectorFixture();
        BuildTree(f.Root);
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);
        File.Delete(Path.Combine(f.Root.Path, "sys/class/hwmon/hwmon1/fan1_input"));
        f.Collect(hwmon, 1);
        Assert.Null(f.Number("hwmon/nct6798/fan1"));
        Assert.NotNull(f.Hub.Get("system/hwmon/nct6798/fan1"));
    }

    [Fact]
    public void A_chip_without_a_name_file_uses_its_directory_name()
    {
        using var f = new CollectorFixture();
        f.Root.Write("sys/class/hwmon/hwmon7/temp1_input", "20000\n");
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);
        Assert.Equal(20, f.Number("hwmon/hwmon7/temp1"));
    }

    [Theory]
    [InlineData("k10temp", SensorCategory.Cpu)]
    [InlineData("coretemp", SensorCategory.Cpu)]
    [InlineData("zenpower", SensorCategory.Cpu)]
    [InlineData("amdgpu", SensorCategory.Gpu)]
    [InlineData("nouveau", SensorCategory.Gpu)]
    [InlineData("nvme", SensorCategory.Storage)]
    [InlineData("drivetemp", SensorCategory.Storage)]
    [InlineData("nct6775", SensorCategory.Motherboard)]
    [InlineData("it8688", SensorCategory.Motherboard)]
    [InlineData("acpitz", SensorCategory.Motherboard)]
    [InlineData("BAT0", SensorCategory.Battery)]
    [InlineData("iwlwifi_1", SensorCategory.Network)]
    [InlineData("spd5118", SensorCategory.Memory)]
    public void Chips_are_categorised_by_driver_name(string chip, SensorCategory expected) =>
        Assert.Equal(expected, LinuxHwmonCollector.CategoryOf(chip));

    [Fact]
    public void Unknown_chips_have_no_category()
    {
        Assert.Null(LinuxHwmonCollector.CategoryOf("something-new"));
    }

    [Fact]
    public void No_hwmon_directory_is_fine()
    {
        using var f = new CollectorFixture();
        using var hwmon = new LinuxHwmonCollector(f.Root.Path);
        f.Collect(hwmon, 0);
        Assert.Equal(0, f.Hub.Count);
    }
}

public class LinuxDeviceCollectorTests
{
    [Fact]
    public void Amd_gpu_load_and_vram()
    {
        using var f = new CollectorFixture();
        f.Root.Write("sys/class/drm/card0/device/gpu_busy_percent", "37\n");
        f.Root.Write("sys/class/drm/card0/device/mem_info_vram_used", "2147483648\n");
        f.Root.Write("sys/class/drm/card0/device/mem_info_vram_total", "17179869184\n");
        f.Root.CreateDirectory("sys/class/drm/card0-DP-1");
        f.Root.Write("sys/class/drm/card1/device/vendor", "0x8086\n"); // an Intel iGPU: no gpu_busy_percent
        using var gpu = new LinuxAmdGpuCollector(f.Root.Path);
        f.Collect(gpu, 0);

        Assert.Equal(37, f.Number("gpu/amd0/load"));
        Assert.Equal(2147483648.0, f.Number("gpu/amd0/vram/used"));
        Assert.Equal(17179869184.0, f.Number("gpu/amd0/vram/total"));
        Assert.Equal(12.5, f.Number("gpu/amd0/vram/usage")!.Value, 9);
        Assert.Null(f.Hub.Get("system/gpu/amd1/load"));

        var info = f.Info("gpu/amd0/load");
        Assert.Equal(SensorCategory.Gpu, info.Category);
        Assert.Equal("AMD GPU", info.Device);
        Assert.Equal(17179869184.0, f.Info("gpu/amd0/vram/used").Max);
    }

    [Fact]
    public void Two_amd_cards_are_numbered_in_card_order_and_a_sleeping_one_is_left_alone()
    {
        using var f = new CollectorFixture();
        f.Root.Write("sys/class/drm/card10/device/gpu_busy_percent", "10\n");
        f.Root.Write("sys/class/drm/card2/device/gpu_busy_percent", "20\n");
        using var gpu = new LinuxAmdGpuCollector(f.Root.Path);
        f.Collect(gpu, 0);
        Assert.Equal(20, f.Number("gpu/amd0/load")); // card2 sorts before card10
        Assert.Equal(10, f.Number("gpu/amd1/load"));
        Assert.Equal("AMD GPU 1", f.Info("gpu/amd1/load").Device);

        f.Root.Write("sys/class/drm/card2/device/power/runtime_status", "suspended\n");
        f.Root.Write("sys/class/drm/card2/device/gpu_busy_percent", "99\n");
        f.Collect(gpu, 1);
        Assert.Equal(20, f.Number("gpu/amd0/load"));
    }

    [Fact]
    public void Batteries_report_charge_status_and_power()
    {
        using var f = new CollectorFixture();
        f.Root.Write("sys/class/power_supply/BAT0/type", "Battery\n");
        f.Root.Write("sys/class/power_supply/BAT0/capacity", "76\n");
        f.Root.Write("sys/class/power_supply/BAT0/status", "Discharging\n");
        f.Root.Write("sys/class/power_supply/BAT0/power_now", "12500000\n");
        f.Root.Write("sys/class/power_supply/BAT0/model_name", "DELL 1234\n");

        f.Root.Write("sys/class/power_supply/BAT1/type", "Battery\n");
        f.Root.Write("sys/class/power_supply/BAT1/energy_now", "30000000\n");
        f.Root.Write("sys/class/power_supply/BAT1/energy_full", "60000000\n");
        f.Root.Write("sys/class/power_supply/BAT1/status", "Charging\n");
        f.Root.Write("sys/class/power_supply/BAT1/current_now", "1500000\n");
        f.Root.Write("sys/class/power_supply/BAT1/voltage_now", "11400000\n");

        f.Root.Write("sys/class/power_supply/AC/type", "Mains\n");
        f.Root.Write("sys/class/power_supply/AC/online", "1\n");
        using var batteries = new LinuxBatteryCollector(f.Root.Path);
        f.Collect(batteries, 0);

        Assert.Equal(76, f.Number("battery/bat0/capacity"));
        Assert.Equal("Discharging", f.Hub.GetText("system/battery/bat0/status"));
        Assert.Equal(12.5, f.Number("battery/bat0/power"));
        Assert.Equal("BAT0 (DELL 1234)", f.Info("battery/bat0/capacity").Device);

        Assert.Equal(50, f.Number("battery/bat1/capacity"));       // from energy_now / energy_full
        Assert.Equal("Charging", f.Hub.GetText("system/battery/bat1/status"));
        Assert.Equal(17.1, f.Number("battery/bat1/power")!.Value, 9); // 1.5 A x 11.4 V

        Assert.Null(f.Hub.Get("system/battery/ac/capacity"));
        var capacity = f.Info("battery/bat0/capacity");
        Assert.Equal(SensorCategory.Battery, capacity.Category);
        Assert.Equal(Units.Percent, capacity.Unit);
        Assert.Equal(SensorValueKind.Text, f.Info("battery/bat0/status").ValueKind);
        Assert.Equal(Units.Watt, f.Info("battery/bat0/power").Unit);
    }

    [Fact]
    public void Uptime_and_process_count_come_from_proc()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/uptime", "12345.67 54321.00\n");
        f.Root.CreateDirectory("proc/1");
        f.Root.CreateDirectory("proc/42");
        f.Root.CreateDirectory("proc/self");
        f.Root.CreateDirectory("proc/sys");
        f.Root.Write("proc/stat", "x");
        using var host = new LinuxHostCollector(f.Root.Path);
        f.Collect(host, 0);

        Assert.Equal(12345.67, f.Number("uptime"));
        Assert.Equal(2, f.Number("process/count"));
        Assert.Equal(Units.Seconds, f.Info("uptime").Unit);
        Assert.Equal(SensorKinds.Duration, f.Info("uptime").Kind);

        // The process list is only recounted every five seconds, uptime every time.
        f.Root.CreateDirectory("proc/77");
        f.Root.Write("proc/uptime", "12346.67 1\n");
        f.Collect(host, 1);
        Assert.Equal(12346.67, f.Number("uptime"));
        Assert.Equal(2, f.Number("process/count"));
        f.Collect(host, 6);
        Assert.Equal(3, f.Number("process/count"));
    }

    [Fact]
    public void The_whole_collector_works_on_an_empty_tree_and_on_a_full_one()
    {
        using var empty = new CollectorFixture();
        using var collector = new LinuxCollector(empty.Root.Path);
        empty.Collect(collector, 0);
        empty.Collect(collector, 1);
        Assert.Equal(0, empty.Hub.Count);

        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", "cpu 100 0 100 800 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0\n");
        f.Root.Write("proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 500 kB\n");
        f.Root.Write("proc/uptime", "10 10\n");
        f.Root.Write("sys/class/hwmon/hwmon0/name", "k10temp\n");
        f.Root.Write("sys/class/hwmon/hwmon0/temp1_input", "50000\n");
        using var full = new LinuxCollector(f.Root.Path);
        f.Collect(full, 0);

        Assert.Equal(50, f.Number("mem/usage"));
        Assert.Equal(10, f.Number("uptime"));
        Assert.Equal(50, f.Number("hwmon/k10temp/temp1"));
    }

    [Fact]
    public void Everything_it_defines_has_a_valid_local_id()
    {
        using var f = new CollectorFixture();
        f.Root.Write("proc/stat", "cpu 100 0 100 800 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0\n");
        f.Root.Write("proc/loadavg", "1 1 1 1/1 1\n");
        f.Root.Write("proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 500 kB\nSwapTotal: 10 kB\nSwapFree: 5 kB\n");
        f.Root.Write("proc/uptime", "10 10\n");
        f.Root.Write("proc/diskstats", " 8 0 sda 1 0 8 0 1 0 8 0 0 1 1 0 0 0 0\n");
        f.Root.CreateDirectory("sys/block/sda");
        f.Root.Write("sys/class/hwmon/hwmon0/name", "Weird Chip! (v2)\n");
        f.Root.Write("sys/class/hwmon/hwmon0/temp1_input", "50000\n");
        f.Root.Write("sys/class/power_supply/BAT 0/type", "Battery\n");
        f.Root.Write("sys/class/power_supply/BAT 0/capacity", "50\n");
        using var collector = new LinuxCollector(f.Root.Path);
        f.Collect(collector, 0);
        f.Collect(collector, 1);

        Assert.NotEmpty(f.Hub.GetCatalog());
        Assert.All(f.Hub.GetCatalog(), info => Assert.True(LocalIds.IsValid(info.Id["system/".Length..]), info.Id));
    }
}
