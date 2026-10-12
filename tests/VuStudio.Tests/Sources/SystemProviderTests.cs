using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.SystemMetrics;

namespace VuStudio.Tests.Sources;

internal sealed class FakeNetworkReader : INetworkInterfaceReader
{
    public List<NetworkInterfaceSample> Samples { get; set; } = [];

    public IReadOnlyList<NetworkInterfaceSample> Read() => Samples;
}

internal sealed class FakeDriveReader : IDriveReader
{
    public List<DriveSample> Drives { get; set; } = [];

    public int Reads { get; private set; }

    public IReadOnlyList<DriveSample> Read()
    {
        Reads++;
        return Drives;
    }
}

public class NetworkCollectorTests
{
    private static NetworkInterfaceSample Nic(string name, long rx, long tx, bool up = true, bool loopback = false, bool isVirtual = false, long? speed = null) =>
        new(name, up, loopback, isVirtual, speed, rx, tx);

    [Fact]
    public void Rates_per_interface_and_totals_over_real_interfaces()
    {
        using var f = new CollectorFixture();
        var reader = new FakeNetworkReader
        {
            Samples =
            [
                Nic("eth0", 1_000, 500, speed: 1_000_000_000),
                Nic("wlan0", 0, 0),
                Nic("docker0", 0, 0, isVirtual: true),
                Nic("lo", 0, 0, loopback: true),
                Nic("veth1234", 0, 0, isVirtual: true),
                Nic("eth9", 0, 0, up: false),
            ],
        };
        using var network = new NetworkCollector(reader);

        f.Collect(network, 0);
        Assert.Null(f.Number("net/eth0/rx")); // needs two readings

        reader.Samples =
        [
            Nic("eth0", 11_000, 2_500, speed: 1_000_000_000),
            Nic("wlan0", 4_000, 1_000),
            Nic("docker0", 90_000, 90_000, isVirtual: true),
            Nic("lo", 999, 999, loopback: true),
            Nic("veth1234", 5, 5, isVirtual: true),
            Nic("eth9", 5, 5, up: false),
        ];
        f.Collect(network, 2);

        Assert.Equal(5_000, f.Number("net/eth0/rx"));   // +10 000 B in 2 s
        Assert.Equal(1_000, f.Number("net/eth0/tx"));   // +2 000 B in 2 s
        Assert.Equal(2_000, f.Number("net/wlan0/rx"));
        Assert.Equal(45_000, f.Number("net/docker0/rx")); // virtual interfaces still get their own sensors...
        Assert.Equal(7_000, f.Number("net/rx"));          // ...but do not add to the totals (eth0 + wlan0)
        Assert.Equal(1_500, f.Number("net/tx"));

        Assert.Null(f.Hub.Get("system/net/lo/rx"));
        Assert.Null(f.Hub.Get("system/net/veth1234/rx"));
        Assert.Null(f.Hub.Get("system/net/eth9/rx"));

        var info = f.Info("net/eth0/rx");
        Assert.Equal("eth0", info.Device);
        Assert.Equal(SensorCategory.Network, info.Category);
        Assert.Equal(SensorKinds.Throughput, info.Kind);
        Assert.Equal(Units.BytesPerSecond, info.Unit);
        Assert.Equal(125_000_000, info.Max); // 1 Gbit/s link speed in bytes per second
        Assert.Null(f.Info("net/wlan0/rx").Max);
        Assert.Equal(SensorCategory.Network, f.Info("net/rx").Category);
    }

    [Fact]
    public void With_only_virtual_interfaces_the_totals_count_them_all()
    {
        using var f = new CollectorFixture();
        var reader = new FakeNetworkReader { Samples = [Nic("br0", 0, 0, isVirtual: true)] };
        using var network = new NetworkCollector(reader);
        f.Collect(network, 0);
        reader.Samples = [Nic("br0", 3_000, 1_000, isVirtual: true)];
        f.Collect(network, 1);
        Assert.Equal(3_000, f.Number("net/rx"));
    }

    [Fact]
    public void A_counter_that_resets_does_not_produce_a_negative_or_huge_rate()
    {
        using var f = new CollectorFixture();
        var reader = new FakeNetworkReader { Samples = [Nic("eth0", 1_000_000, 1_000_000)] };
        using var network = new NetworkCollector(reader);
        f.Collect(network, 0);
        reader.Samples = [Nic("eth0", 10_000, 10_000)];
        f.Collect(network, 1);
        Assert.Null(f.Number("net/eth0/rx"));
        reader.Samples = [Nic("eth0", 12_000, 11_000)];
        f.Collect(network, 2);
        Assert.Equal(2_000, f.Number("net/eth0/rx"));
    }

    [Fact]
    public void Names_become_clean_unique_ids_and_vanished_interfaces_are_removed()
    {
        using var f = new CollectorFixture();
        var reader = new FakeNetworkReader { Samples = [Nic("Wi-Fi 2", 0, 0), Nic("Wi-Fi  2", 0, 0), Nic("vEthernet (WSL)", 0, 0)] };
        using var network = new NetworkCollector(reader);
        f.Collect(network, 0);
        f.Collect(network, 1);

        Assert.NotNull(f.Hub.Get("system/net/wi-fi-2/rx"));
        Assert.NotNull(f.Hub.Get("system/net/wi-fi-2-2/rx"));
        Assert.NotNull(f.Hub.Get("system/net/vethernet-wsl/rx"));

        reader.Samples = [Nic("Wi-Fi 2", 0, 0)];
        f.Collect(network, 2);
        Assert.Null(f.Hub.Get("system/net/vethernet-wsl/rx"));
        Assert.NotNull(f.Hub.Get("system/net/wi-fi-2/rx"));
    }

    [Fact]
    public void The_real_reader_returns_something_sensible()
    {
        var samples = new SystemNetworkInterfaceReader().Read();
        Assert.All(samples, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.Name));
            Assert.True(s.BytesReceived >= 0 && s.BytesSent >= 0);
        });
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(samples, s => s.IsLoopback);
        }
    }
}

public class DiskUsageCollectorTests
{
    [Fact]
    public void Usage_is_computed_like_df_and_ids_follow_the_mount_point()
    {
        using var f = new CollectorFixture();
        var reader = new FakeDriveReader
        {
            Drives =
            [
                // 1000 B volume, 400 B in use, 100 B reserved for root: a user can still use 500 B.
                new DriveSample("/", null, 1000, 600, 500),
                new DriveSample("/mnt/disk1", null, 2000, 2000, 2000),
                new DriveSample(@"C:\", "System", 4000, 1000, 1000),
            ],
        };
        using var disks = new DiskUsageCollector(reader);
        f.Collect(disks, 0);

        Assert.Equal(100.0 * 400 / 900, f.Number("disk/root/usage")!.Value, 9);
        Assert.Equal(400, f.Number("disk/root/used"));
        Assert.Equal(500, f.Number("disk/root/free"));
        Assert.Equal(0, f.Number("disk/mnt-disk1/usage"));
        Assert.Equal(75, f.Number("disk/c/usage")!.Value, 9);
        Assert.Equal(3000, f.Number("disk/c/used"));

        var info = f.Info("disk/c/usage");
        Assert.Equal("C: (System)", info.Device);
        Assert.Equal(SensorCategory.Storage, info.Category);
        Assert.Equal(Units.Percent, info.Unit);
        Assert.Equal(100, info.Max);
        Assert.Equal(4000, f.Info("disk/c/used").Max);
        Assert.Equal(Units.Bytes, f.Info("disk/c/free").Unit);
        Assert.Equal("/", f.Info("disk/root/usage").Device);
        Assert.Equal(60, f.Info("disk/c/usage").StaleAfterSeconds);
    }

    [Fact]
    public void Drives_are_read_every_ten_seconds_and_removed_when_they_vanish()
    {
        using var f = new CollectorFixture();
        var reader = new FakeDriveReader { Drives = [new DriveSample("/", null, 100, 50, 50), new DriveSample("/data", null, 100, 50, 50)] };
        using var disks = new DiskUsageCollector(reader);

        f.Collect(disks, 0);
        f.Collect(disks, 3);
        f.Collect(disks, 9.9);
        Assert.Equal(1, reader.Reads);

        reader.Drives = [new DriveSample("/", null, 100, 25, 25)];
        f.Collect(disks, 10);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(75, f.Number("disk/root/used"));
        Assert.Null(f.Hub.Get("system/disk/data/usage"));
    }

    [Fact]
    public void Slugs()
    {
        Assert.Equal("root", DiskUsageCollector.DriveSlug("/"));
        Assert.Equal("c", DiskUsageCollector.DriveSlug(@"C:\"));
        Assert.Equal("mnt-disk1", DiskUsageCollector.DriveSlug("/mnt/disk1"));
        Assert.Equal("home", DiskUsageCollector.DriveSlug("/home"));
    }

    [Fact]
    public void The_real_reader_lists_fixed_drives_with_sane_numbers()
    {
        var drives = new SystemDriveReader().Read();
        Assert.All(drives, d =>
        {
            Assert.True(d.TotalBytes > 0);
            Assert.InRange(d.AvailableBytes, 0, d.TotalBytes);
            Assert.InRange(d.FreeBytes, 0, d.TotalBytes);
        });
        if (OperatingSystem.IsLinux())
        {
            Assert.DoesNotContain(drives, d => d.Name.StartsWith("/proc", StringComparison.Ordinal) || d.Name.StartsWith("/sys", StringComparison.Ordinal));
        }
    }
}

public class WindowsCollectorTests
{
    private sealed class FakeApi : IWindowsApi
    {
        public (ulong Idle, ulong Kernel, ulong User)? Times { get; set; }

        public WindowsMemory? Memory { get; set; }

        public bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            (idle, kernel, user) = Times ?? default;
            return Times is not null;
        }

        public bool TryGetMemory(out WindowsMemory memory)
        {
            memory = Memory ?? default;
            return Memory is not null;
        }
    }

    [Fact]
    public void Cpu_load_is_the_busy_share_of_kernel_plus_user_time()
    {
        using var f = new CollectorFixture();
        var api = new FakeApi { Times = (1_000, 5_000, 2_000) };
        using var windows = new WindowsCollector(api);
        f.Collect(windows, 0);
        Assert.Null(f.Number("cpu/load"));

        // +100 idle, +400 kernel (which includes the idle time), +100 user: 500 elapsed, 400 of them busy.
        api.Times = (1_100, 5_400, 2_100);
        f.Collect(windows, 1);
        Assert.Equal(80, f.Number("cpu/load")!.Value, 9);
        Assert.Equal(SensorCategory.Cpu, f.Info("cpu/load").Category);
    }

    [Fact]
    public void Memory_uses_the_shared_ids()
    {
        using var f = new CollectorFixture();
        var api = new FakeApi { Memory = new WindowsMemory(16UL << 30, 4UL << 30) };
        using var windows = new WindowsCollector(api);
        f.Collect(windows, 0);

        Assert.Equal(75, f.Number("mem/usage")!.Value, 9);
        Assert.Equal(12.0 * (1L << 30), f.Number("mem/used"));
        Assert.Equal(4.0 * (1L << 30), f.Number("mem/available"));
        Assert.Equal(16.0 * (1L << 30), f.Number("mem/total"));
    }

    [Fact]
    public void Failing_calls_publish_nothing()
    {
        using var f = new CollectorFixture();
        using var windows = new WindowsCollector(new FakeApi());
        f.Collect(windows, 0);
        f.Collect(windows, 1);
        Assert.Equal(0, f.Hub.Count);
    }
}

public class NvidiaSmiTests
{
    [Fact]
    public void Parses_one_row_per_gpu_and_treats_unsupported_fields_as_missing()
    {
        var gpus = NvidiaSmi.Parse("35, 52, 120.45, 1024, 8192, 40\n[N/A], 40, [N/A], 100, 2048, [Not Supported]\n\nbroken, row\n");

        Assert.Equal(2, gpus.Count);
        Assert.Equal(new NvidiaGpu(0, 35, 52, 120.45, 1024, 8192, 40), gpus[0]);
        Assert.Equal(new NvidiaGpu(1, null, 40, null, 100, 2048, null), gpus[1]);
        Assert.Empty(NvidiaSmi.Parse(null));
        Assert.Empty(NvidiaSmi.Parse("No devices were found"));
    }

    [Fact]
    public void Asks_for_exactly_the_documented_fields()
    {
        Assert.Equal(
            ["--query-gpu=utilization.gpu,temperature.gpu,power.draw,memory.used,memory.total,fan.speed", "--format=csv,noheader,nounits"],
            NvidiaSmi.Arguments);
    }
}

public class NvidiaSmiCollectorTests
{
    private static ProcessRunResult Output(string csv, int exit = 0) => new(exit, csv, "", false, TimeSpan.Zero);

    private static async Task SettleAsync(NvidiaSmiCollector collector)
    {
        if (collector.Pending is { } pending)
        {
            await pending;
        }
    }

    [Fact]
    public async Task Publishes_gpu_sensors_on_the_pass_after_the_query_finished()
    {
        using var f = new CollectorFixture();
        var runs = 0;
        using var nvidia = new NvidiaSmiCollector(() => "/usr/bin/nvidia-smi", (path, _) =>
        {
            runs++;
            Assert.Equal("/usr/bin/nvidia-smi", path);
            return Task.FromResult(Output("35, 52, 120.45, 1024, 8192, 40\n"));
        });

        f.Collect(nvidia, 0);
        Assert.Null(f.Number("gpu/nvidia0/load")); // the provider's tick never waits for the tool
        await SettleAsync(nvidia);

        f.Collect(nvidia, 0.5);
        Assert.Equal(35, f.Number("gpu/nvidia0/load"));
        Assert.Equal(52, f.Number("gpu/nvidia0/temp"));
        Assert.Equal(120.45, f.Number("gpu/nvidia0/power"));
        Assert.Equal(40, f.Number("gpu/nvidia0/fan"));
        Assert.Equal(1024.0 * 1024 * 1024, f.Number("gpu/nvidia0/vram/used"));
        Assert.Equal(8192.0 * 1024 * 1024, f.Number("gpu/nvidia0/vram/total"));
        Assert.Equal(12.5, f.Number("gpu/nvidia0/vram/usage")!.Value, 9);
        Assert.Equal(1, runs);

        Assert.Equal(SensorCategory.Gpu, f.Info("gpu/nvidia0/load").Category);
        Assert.Equal("NVIDIA GPU", f.Info("gpu/nvidia0/load").Device);
        Assert.Equal(Units.Celsius, f.Info("gpu/nvidia0/temp").Unit);
        Assert.Equal(Units.Watt, f.Info("gpu/nvidia0/power").Unit);
        Assert.Equal(SensorCategory.Cooling, f.Info("gpu/nvidia0/fan").Category);
        Assert.Equal(8192.0 * 1024 * 1024, f.Info("gpu/nvidia0/vram/used").Max);
    }

    [Fact]
    public async Task Queries_at_most_every_two_seconds()
    {
        using var f = new CollectorFixture();
        var runs = 0;
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) =>
        {
            runs++;
            return Task.FromResult(Output("1, 2, 3, 4, 5, 6"));
        });

        foreach (var t in new[] { 0.0, 1.0, 1.9 })
        {
            f.Collect(nvidia, t);
            await SettleAsync(nvidia);
        }

        Assert.Equal(1, runs);
        f.Collect(nvidia, 2.0);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 2.1);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Does_not_start_a_second_query_while_one_is_running()
    {
        using var f = new CollectorFixture();
        var gate = new TaskCompletionSource<ProcessRunResult>();
        var runs = 0;
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) =>
        {
            runs++;
            return gate.Task;
        });

        f.Collect(nvidia, 0);
        f.Collect(nvidia, 5);
        f.Collect(nvidia, 10);
        Assert.Equal(1, runs);

        gate.SetResult(Output("9, 9, 9, 9, 9, 9"));
        await SettleAsync(nvidia);
        f.Collect(nvidia, 11);
        Assert.Equal(9, f.Number("gpu/nvidia0/load"));
    }

    [Fact]
    public async Task Without_the_binary_nothing_runs_and_it_is_looked_for_again_after_a_minute()
    {
        using var f = new CollectorFixture();
        var lookups = 0;
        string? found = null;
        var runs = 0;
        using var nvidia = new NvidiaSmiCollector(() =>
        {
            lookups++;
            return found;
        }, (_, _) =>
        {
            runs++;
            return Task.FromResult(Output("1, 2, 3, 4, 5, 6"));
        });

        f.Collect(nvidia, 0);
        f.Collect(nvidia, 30);
        Assert.Equal(1, lookups);
        Assert.Equal(0, runs);

        found = "/opt/nvidia-smi"; // the driver got installed
        f.Collect(nvidia, 61);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 62);
        Assert.Equal(2, lookups);
        Assert.Equal(1, runs);
        Assert.Equal(1, f.Number("gpu/nvidia0/load"));
    }

    [Fact]
    public async Task A_failing_tool_is_retried_after_a_pause_and_a_vanished_binary_is_forgotten()
    {
        using var f = new CollectorFixture();
        var attempt = 0;
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) =>
        {
            attempt++;
            return attempt switch
            {
                1 => Task.FromResult(Output("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.", exit: 9)),
                2 => throw new System.ComponentModel.Win32Exception("No such file"),
                _ => Task.FromResult(Output("7, 7, 7, 7, 7, 7")),
            };
        });

        f.Collect(nvidia, 0);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 2);   // harvests the failure: backs off 30 s
        f.Collect(nvidia, 20);
        Assert.Equal(1, attempt);

        f.Collect(nvidia, 33);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 35);  // harvests the exception, backs off again
        f.Collect(nvidia, 64);
        Assert.Equal(2, attempt);

        f.Collect(nvidia, 70);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 71);
        Assert.Equal(7, f.Number("gpu/nvidia0/load"));
    }

    [Fact]
    public async Task Fields_the_card_does_not_report_go_offline()
    {
        using var f = new CollectorFixture();
        var csv = "50, 60, 70, 100, 200, 30";
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) => Task.FromResult(Output(csv)));
        f.Collect(nvidia, 0);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 0.1);
        Assert.Equal(30, f.Number("gpu/nvidia0/fan"));

        csv = "50, 60, [N/A], 100, 200, [Not Supported]";
        f.Collect(nvidia, 3);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 3.1);
        Assert.Null(f.Number("gpu/nvidia0/fan"));
        Assert.Null(f.Number("gpu/nvidia0/power"));
        Assert.Equal(60, f.Number("gpu/nvidia0/temp"));
    }

    [Fact]
    public async Task Several_gpus_are_numbered_by_row()
    {
        using var f = new CollectorFixture();
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) => Task.FromResult(Output("10, 1, 1, 1, 2, 1\n20, 2, 2, 2, 4, 2")));
        f.Collect(nvidia, 0);
        await SettleAsync(nvidia);
        f.Collect(nvidia, 0.1);
        Assert.Equal(10, f.Number("gpu/nvidia0/load"));
        Assert.Equal(20, f.Number("gpu/nvidia1/load"));
        Assert.Equal("NVIDIA GPU 1", f.Info("gpu/nvidia1/load").Device);
    }

    [Fact]
    public async Task Disposing_cancels_a_query_in_flight()
    {
        using var f = new CollectorFixture();
        CancellationToken seen = default;
        var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", async (_, token) =>
        {
            seen = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Output("");
        });

        f.Collect(nvidia, 0);
        var pending = nvidia.Pending!;
        nvidia.Dispose();

        await pending; // finishes (with no data) instead of hanging
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public void The_real_locator_does_not_throw()
    {
        using var f = new CollectorFixture();
        using var nvidia = new NvidiaSmiCollector();
        f.Collect(nvidia, 0); // finds nothing on most machines; on one with a driver it starts a real query
    }
}

public class SystemProviderTests
{
    private sealed class ScriptedCollector(Action<SensorRegistry, SampleContext> collect) : ISystemCollector
    {
        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        public void Collect(SensorRegistry sensors, SampleContext context)
        {
            Calls++;
            collect(sensors, context);
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task A_failing_collector_does_not_stop_the_others_or_degrade_the_provider()
    {
        var broken = new ScriptedCollector((_, _) => throw new IOException("sensor chip on fire"));
        var good = new ScriptedCollector((sensors, context) =>
        {
            Metrics.EnsureNumber(sensors, "t", "T", "Test", SensorCategory.System, SensorKinds.Duration, Units.Seconds, 0, null);
            sensors.Publish("t", context.Time.TotalSeconds);
        });
        var provider = new SystemProvider("system", [broken, good], TimeSpan.FromMilliseconds(50), TimeProvider.System, NullLogger.Instance);
        var hub = new SensorHub();
        var status = new RecordingStatus();

        await ProviderRunner.RunUntilAsync(provider, hub, () => good.Calls >= 3 && hub.GetNumber("system/t") is not null, status: status);

        Assert.True(broken.Calls >= 3); // it keeps being tried
        Assert.DoesNotContain(status.Reports, r => r.Health == ProviderHealth.Degraded);
        Assert.True(broken.Disposed && good.Disposed); // RunUntilAsync disposes the provider
    }

    [Fact]
    public async Task Collectors_see_time_since_the_provider_started()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var seen = new List<double>();
        var collector = new ScriptedCollector((_, context) => seen.Add(context.Time.TotalSeconds));
        var provider = new SystemProvider("system", [collector], TimeSpan.FromSeconds(1), time, NullLogger.Instance);
        var sink = new SensorHub().CreateSink("system");

        await ProviderDriver.InitializeAsync(provider, sink);
        await ProviderDriver.PollAsync(provider, sink);
        time.Advance(TimeSpan.FromSeconds(1.5));
        await ProviderDriver.PollAsync(provider, sink);

        Assert.Equal([0, 1.5], seen);
    }

    [Fact]
    public async Task Sensors_get_the_default_staleness_for_the_interval()
    {
        var collector = new ScriptedCollector((sensors, _) => Metrics.EnsureNumber(sensors, "x", "X", "Test", SensorCategory.System, SensorKinds.Count, Units.None, 0, null));
        var provider = new SystemProvider("system", [collector], TimeSpan.FromSeconds(20), TimeProvider.System, NullLogger.Instance);
        var hub = new SensorHub();
        var sink = hub.CreateSink("system");
        await ProviderDriver.InitializeAsync(provider, sink);
        await ProviderDriver.PollAsync(provider, sink);
        Assert.Equal(60, hub.Get("system/x")!.Info.StaleAfterSeconds);
    }

    [Fact]
    public void The_factory_picks_collectors_for_this_machine()
    {
        var collectors = SystemProviderFactory.BuildCollectors(perCore: true, nvidia: true);
        Assert.Contains(collectors, c => c is NetworkCollector);
        Assert.Contains(collectors, c => c is DiskUsageCollector);
        Assert.Equal(OperatingSystem.IsLinux(), collectors.Any(c => c is VuStudio.Sources.SystemMetrics.Linux.LinuxCollector));
        Assert.Equal(!OperatingSystem.IsLinux(), collectors.Any(c => c is HostCollector));
        Assert.Equal(OperatingSystem.IsWindows(), collectors.Any(c => c is WindowsCollector));
        Assert.Contains(collectors, c => c is NvidiaSmiCollector);
        Assert.DoesNotContain(SystemProviderFactory.BuildCollectors(true, nvidia: false), c => c is NvidiaSmiCollector);
        collectors.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task The_real_provider_publishes_basic_metrics_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Directory.Exists("/proc/self"), "needs Linux /proc");

        var provider = new SystemProviderFactory().Create(TestConfig.For("system", """{"interval":0.1,"nvidiaSmi":false}"""), TestServices.Empty());
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            provider,
            hub,
            () => hub.GetNumber("system/cpu/load") is not null && hub.GetNumber("system/mem/usage") is not null && hub.GetNumber("system/uptime") is not null);

        Assert.InRange(hub.GetNumber("system/cpu/load")!.Value, 0, 100);
        Assert.InRange(hub.GetNumber("system/mem/usage")!.Value, 0, 100);
        Assert.True(hub.GetNumber("system/mem/total") > 0);
        Assert.True(hub.GetNumber("system/uptime") > 0);
        Assert.True(hub.GetNumber("system/process/count") > 0);
        Assert.All(hub.GetCatalog(), info =>
        {
            Assert.True(LocalIds.IsValid(info.Id["system/".Length..]), info.Id);
            Assert.False(string.IsNullOrWhiteSpace(info.Name), info.Id);
            Assert.False(string.IsNullOrWhiteSpace(info.Icon), info.Id);
        });
    }
}
