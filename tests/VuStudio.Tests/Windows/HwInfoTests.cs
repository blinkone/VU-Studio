using System.Text;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.HwInfo;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Windows;

public class HwInfoParserTests
{
    private static readonly SynthBuffer.Sensor[] OneSensor = [new(0xF0000300, 1, "CPU [#0]: AMD Ryzen 9 7900X")];

    [Fact]
    public void Parses_revision1_utf8_buffer_with_user_labels()
    {
        var buffer = SynthBuffer.Build(1, true, 1_760_000_000, 2000,
            [new(0xF0000300, 0, "CPU [#0]: AMD Ryzen", "My CPU", "Mój procesor")],
            [
                new(1, 0, 0x8000000, "Core 0 (Tctl)", "°C", 61.5, LabelUser: "Package", LabelUtf8: "Temperatura — Pakiet"),
                new(7, 0, 0x8000001, "Total CPU Usage", "%", 12.25),
            ]);

        var result = HwInfoParser.Parse(buffer, Encoding.Latin1);

        Assert.Equal(HwInfoParseStatus.Ok, result.Status);
        var s = result.Snapshot!;
        Assert.Equal(1u, s.Header.Revision);
        Assert.Equal(2u, s.Header.Version);
        Assert.Equal(2000u, s.Header.PollingPeriodMs);
        Assert.Equal(1_760_000_000, s.Header.PollTime);
        Assert.Equal("Mój procesor", s.Sensors[0].Name);
        Assert.Equal("CPU [#0]: AMD Ryzen", s.Sensors[0].OriginalName);
        Assert.Equal(0xF0000300u, s.Sensors[0].Id);
        Assert.Equal("Temperatura — Pakiet", s.Readings[0].Label);
        Assert.Equal("Core 0 (Tctl)", s.Readings[0].OriginalLabel);
        Assert.Equal("°C", s.Readings[0].Unit);
        Assert.Equal(HwInfoReadingType.Temp, s.Readings[0].Type);
        Assert.Equal(61.5, s.Readings[0].Value);
        Assert.Equal(60.5, s.Readings[0].Min);
        Assert.Equal(62.5, s.Readings[0].Max);
        Assert.Equal("Total CPU Usage", s.Readings[1].Label);
        Assert.Equal(HwInfoReadingType.Usage, s.Readings[1].Type);
        Assert.Equal(12.25, s.Readings[1].Value);
    }

    [Fact]
    public void Parses_revision0_ansi_buffer_without_polling_period()
    {
        var buffer = SynthBuffer.Build(0, false, 42, 999, OneSensor, [new(1, 0, 5, "Temp", "°C", 50, LabelUser: "Renamed")]);

        var result = HwInfoParser.Parse(buffer, Encoding.Latin1);

        Assert.Equal(HwInfoParseStatus.Ok, result.Status);
        Assert.Equal(0u, result.Header.Revision);
        Assert.Equal(0u, result.Header.PollingPeriodMs);
        Assert.Equal(44u, result.Header.SensorOffset);
        Assert.Equal(264u, result.Header.SensorStride);
        Assert.Equal(316u, result.Header.ReadingStride);
        var reading = result.Snapshot!.Readings[0];
        Assert.Equal("Renamed", reading.Label);
        Assert.Equal("°C", reading.Unit);
        Assert.Equal("CPU [#0]: AMD Ryzen 9 7900X", result.Snapshot.Sensors[0].Name);
    }

    [Fact]
    public void Ansi_bytes_follow_the_requested_code_page()
    {
        var buffer = SynthBuffer.Build(1, false, 1, 1000, OneSensor, [new(1, 0, 5, "Temp", "°C", 50)]);

        var ascii = HwInfoParser.Parse(buffer, Encoding.ASCII).Snapshot!;

        Assert.NotEqual("°C", ascii.Readings[0].Unit); // 0xB0 is not ASCII: the right code page matters
    }

    [Fact]
    public void Accepts_larger_strides_than_known()
    {
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(1, 0, 5, "Temp", "°C", 50), new(2, 0, 6, "Vcore", "V", 1.1)],
            extraSensorStride: 16, extraReadingStride: 32);

        var result = HwInfoParser.Parse(buffer, Encoding.Latin1);

        Assert.Equal(HwInfoParseStatus.Ok, result.Status);
        Assert.Equal(408u, result.Header.SensorStride);
        Assert.Equal(492u, result.Header.ReadingStride);
        Assert.Equal(1.1, result.Snapshot!.Readings[1].Value);
    }

    [Fact]
    public void Unknown_reading_types_become_other()
    {
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(99, 0, 5, "Weird", "", 1)]);

        Assert.Equal(HwInfoReadingType.Other, HwInfoParser.Parse(buffer, Encoding.Latin1).Snapshot!.Readings[0].Type);
    }

    [Fact]
    public void Strings_without_terminator_fill_the_field_and_stop_at_nul()
    {
        var long127 = new string('a', 128);
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(1, 0, 5, long127, "x", 1)]);

        var reading = HwInfoParser.Parse(buffer, Encoding.Latin1).Snapshot!.Readings[0];

        Assert.Equal(128, reading.Label.Length);
        Assert.Equal("x", reading.Unit);
    }

    [Theory]
    [InlineData(0x12345678u)]
    [InlineData(0x44414544u)]
    [InlineData(0x44454144u)]
    public void Rejects_foreign_and_dead_signatures(uint signature)
    {
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(1, 0, 5, "T", "°C", 1)], signature);

        var result = HwInfoParser.Parse(buffer, Encoding.Latin1);

        Assert.Equal(signature == 0x12345678u ? HwInfoParseStatus.BadSignature : HwInfoParseStatus.Dead, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Rejects_short_and_truncated_buffers()
    {
        Assert.Equal(HwInfoParseStatus.TooShort, HwInfoParser.Parse(new byte[10], Encoding.Latin1).Status);
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(1, 0, 5, "T", "°C", 1)]);

        Assert.Equal(HwInfoParseStatus.BadLayout, HwInfoParser.Parse(buffer.AsSpan(0, buffer.Length - 1), Encoding.Latin1).Status);
    }

    [Fact]
    public void Rejects_strides_that_are_too_small()
    {
        var buffer = SynthBuffer.Build(1, true, 1, 1000, OneSensor, [new(1, 0, 5, "T", "°C", 1)]);
        buffer[36] = 100; // reading stride 100
        buffer[37] = 0;

        Assert.Equal(HwInfoParseStatus.BadLayout, HwInfoParser.Parse(buffer, Encoding.Latin1).Status);
    }

    [Fact]
    public void Header_ignores_polling_period_for_revision0_and_needs_48_bytes_for_revision1()
    {
        var rev1 = SynthBuffer.Build(1, false, 1, 1000, [], []);

        Assert.False(HwInfoHeader.TryParse(rev1.AsSpan(0, 46), out _));
        Assert.True(HwInfoHeader.TryParse(rev1, out var header));
        Assert.Equal(1000u, header.PollingPeriodMs);
    }

    [Fact]
    public void Local_ids_use_lowercase_hex_and_decimal_instance()
    {
        Assert.Equal("f0000300-0/08000000", HwInfoIds.Format(0xF0000300, 0, 0x8000000));
        Assert.Equal("00000001-12/0000abcd", HwInfoIds.Format(1, 12, 0xABCD));
    }
}

public class HwInfoReaderTests
{
    private static byte[] Sample(long pollTime = 100) => SynthBuffer.Build(1, true, pollTime, 2000,
        [new(1, 0, "CPU")], [new(1, 0, 1, "Temp", "°C", 50), new(2, 0, 2, "Vcore", "V", 1.2)]);

    [Fact]
    public void Reads_a_stable_snapshot_under_the_lock()
    {
        var memory = new FakeHwInfoMemory(Sample());

        var result = new HwInfoReader(Encoding.Latin1).Read(memory);

        Assert.Equal(HwInfoReadStatus.Ok, result.Status);
        Assert.Equal(2, result.Snapshot!.Readings.Length);
        Assert.Equal(1, memory.LocksTaken);
    }

    [Fact]
    public void Retries_when_poll_time_changes_between_header_reads()
    {
        var memory = new FakeHwInfoMemory(Sample(100));
        var backoffs = 0;
        // read 1 = header, 2 = bulk copy, 3 = header again: HWiNFO republishes right then
        memory.OnRead = (n, m) =>
        {
            if (n == 3)
            {
                m.Data = Sample(101);
            }
        };

        var result = new HwInfoReader(Encoding.Latin1, 5, _ => backoffs++).Read(memory);

        Assert.Equal(HwInfoReadStatus.Ok, result.Status);
        Assert.Equal(101, result.Snapshot!.Header.PollTime);
        Assert.Equal(1, backoffs);
    }

    [Fact]
    public void Detects_reading_count_dropping_to_zero_mid_copy()
    {
        var memory = new FakeHwInfoMemory(Sample());
        memory.OnRead = (n, m) =>
        {
            if (n == 3)
            {
                m.Data = SynthBuffer.Build(1, true, 100, 2000, [new(1, 0, "CPU")], []);
            }
        };

        var result = new HwInfoReader(Encoding.Latin1, 3, _ => { }).Read(memory);

        Assert.Equal(HwInfoReadStatus.Ok, result.Status); // second attempt sees the stable, refilled-then-empty state
        Assert.Empty(result.Snapshot!.Readings);
    }

    [Fact]
    public void Gives_up_when_the_area_never_settles()
    {
        var memory = new FakeHwInfoMemory(Sample());
        var tick = 100;
        memory.OnRead = (n, m) =>
        {
            if (n % 3 == 0)
            {
                m.Data = Sample(++tick);
            }
        };

        var result = new HwInfoReader(Encoding.Latin1, 4, _ => { }).Read(memory);

        Assert.Equal(HwInfoReadStatus.Inconsistent, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Reports_dead_and_bad_signatures_without_copying()
    {
        var dead = new FakeHwInfoMemory(SynthBuffer.Build(1, true, 1, 1, [], [], 0x44414544));
        var junk = new FakeHwInfoMemory(new byte[64]);

        Assert.Equal(HwInfoReadStatus.Dead, new HwInfoReader(Encoding.Latin1).Read(dead).Status);
        Assert.Equal(HwInfoReadStatus.BadSignature, new HwInfoReader(Encoding.Latin1).Read(junk).Status);
        Assert.Equal(1, dead.Reads);
    }

    [Fact]
    public void Reports_bad_layout_when_the_view_is_smaller_than_the_header_announces()
    {
        var data = Sample();
        var memory = new FakeHwInfoMemory(data[..^10]);

        Assert.Equal(HwInfoReadStatus.BadLayout, new HwInfoReader(Encoding.Latin1).Read(memory).Status);
        Assert.Equal(HwInfoReadStatus.BadLayout, new HwInfoReader(Encoding.Latin1).Read(new FakeHwInfoMemory(new byte[8])).Status);
    }
}

public class HwInfoUnitsTests
{
    [Theory]
    [InlineData("°C", "°C", 50, 50)]
    [InlineData("°F", "°C", 212, 100)]
    [InlineData("mV", "V", 1200, 1.2)]
    [InlineData("mW", "W", 1500, 1.5)]
    [InlineData("GHz", "MHz", 4.2, 4200)]
    [InlineData("MB", "B", 1, 1_048_576)]
    [InlineData("GB", "B", 2, 2_147_483_648)]
    [InlineData("KB/s", "B/s", 3, 3072)]
    [InlineData("MB/s", "B/s", 1, 1_048_576)]
    [InlineData("FPS", "fps", 144, 144)]
    [InlineData("ms", "ms", 16.6, 16.6)]
    [InlineData("%", "%", 77, 77)]
    [InlineData("RPM", "RPM", 1200, 1200)]
    [InlineData("A", "A", 5, 5)]
    [InlineData("W", "W", 65, 65)]
    [InlineData("x", "", 45.5, 45.5)]
    [InlineData("Yes/No", "", 1, 1)]
    [InlineData("Yes/No", "", 7, 1)]
    [InlineData("", "", 3, 3)]
    public void Maps_to_canonical_units(string unit, string expectedUnit, double value, double expected)
    {
        var mapped = HwInfoUnits.Canonicalize(unit);

        Assert.Equal(expectedUnit, mapped.Unit);
        Assert.Equal(expected, mapped.Convert(value), 6);
    }

    [Fact]
    public void Unknown_units_pass_through()
    {
        var mapped = HwInfoUnits.Canonicalize("GT/s");

        Assert.Equal("GT/s", mapped.Unit);
        Assert.Equal(8, mapped.Convert(8));
    }

    [Theory]
    [InlineData(HwInfoReadingType.Temp, "°C", SensorKinds.Temperature)]
    [InlineData(HwInfoReadingType.Volt, "V", SensorKinds.Voltage)]
    [InlineData(HwInfoReadingType.Fan, "RPM", SensorKinds.Fan)]
    [InlineData(HwInfoReadingType.Fan, "%", SensorKinds.Level)]
    [InlineData(HwInfoReadingType.Current, "A", SensorKinds.Current)]
    [InlineData(HwInfoReadingType.Power, "W", SensorKinds.Power)]
    [InlineData(HwInfoReadingType.Clock, "MHz", SensorKinds.Clock)]
    [InlineData(HwInfoReadingType.Usage, "%", SensorKinds.Load)]
    [InlineData(HwInfoReadingType.Other, "MB", SensorKinds.Data)]
    [InlineData(HwInfoReadingType.Other, "MB/s", SensorKinds.Throughput)]
    [InlineData(HwInfoReadingType.Other, "FPS", SensorKinds.Fps)]
    [InlineData(HwInfoReadingType.Other, "Yes/No", SensorKinds.State)]
    [InlineData(HwInfoReadingType.Other, "x", "factor")]
    [InlineData(HwInfoReadingType.Other, "%", SensorKinds.Percent)]
    [InlineData(HwInfoReadingType.Power, "%", SensorKinds.Percent)]
    [InlineData(HwInfoReadingType.Temp, "T", SensorKinds.Temperature)]
    public void Kind_comes_from_the_reading_type_unless_the_unit_contradicts_it(HwInfoReadingType type, string unit, string expected) =>
        Assert.Equal(expected, HwInfoUnits.Kind(type, HwInfoUnits.Canonicalize(unit)));

    [Fact]
    public void Other_with_unknown_unit_has_no_kind() =>
        Assert.Null(HwInfoUnits.Kind(HwInfoReadingType.Other, HwInfoUnits.Canonicalize("GT/s")));
}

public class HwInfoCategoryTests
{
    [Theory]
    [InlineData("CPU [#0]: AMD Ryzen 9 7900X", SensorCategory.Cpu)]
    [InlineData("CPU [#0]: Intel Core i9-12900K: DTS", SensorCategory.Cpu)]
    [InlineData("GPU [#0]: NVIDIA GeForce RTX 4090: ", SensorCategory.Gpu)]
    [InlineData("Memory Timings", SensorCategory.Memory)]
    [InlineData("DIMM Temperature Sensor", SensorCategory.Memory)]
    [InlineData("Drive: Samsung SSD 980 PRO 1TB (S5GX)", SensorCategory.Storage)]
    [InlineData("S.M.A.R.T.: WDC WD40EFRX", SensorCategory.Storage)]
    [InlineData("Network: Intel Ethernet Connection I219-V", SensorCategory.Network)]
    [InlineData("Battery: Dell Inc. DELL 4GVMP", SensorCategory.Battery)]
    [InlineData("ASUS ROG STRIX B550-F GAMING (WI-FI) (Nuvoton NCT6798D)", SensorCategory.Motherboard)]
    [InlineData("Gigabyte X570 AORUS ELITE WIFI (ITE IT8688E)", SensorCategory.Motherboard)]
    [InlineData("System: ASUS ROG STRIX B550-F GAMING (WI-FI)", SensorCategory.System)]
    [InlineData("Windows Hardware Errors (WHEA)", SensorCategory.System)]
    [InlineData("", SensorCategory.System)]
    public void Guesses_the_category_from_the_sensor_group_name(string name, SensorCategory expected) =>
        Assert.Equal(expected, HwInfoCategories.FromSensorName(name));
}

public class HwInfoProviderTests
{
    private const long Now = 1_760_000_000;

    private static readonly SynthBuffer.Sensor[] Sensors =
    [
        new(0xF0000300, 0, "CPU [#0]: AMD Ryzen 9 7900X"),
        new(0xF0000100, 0, "Drive: Samsung SSD 980 PRO"),
    ];

    private sealed class Rig
    {
        public Rig(HwInfoSettings? settings = null)
        {
            Time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(Now));
            Hub = new SensorHub(Time);
            Sink = Hub.CreateSink("hwinfo");
            Provider = new HwInfoProvider("hwinfo", settings ?? new HwInfoSettings(), Open, Encoding.Latin1,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Time);
        }

        public ManualTimeProvider Time { get; }

        public SensorHub Hub { get; }

        public ISensorSink Sink { get; }

        public RecordingStatus Status { get; } = new();

        public HwInfoProvider Provider { get; }

        public FakeHwInfoMemory? Memory { get; set; }

        public LocalizedText? Failure { get; set; }

        public bool Poll() => Provider.PollOnce(Sink, Status);

        private HwInfoOpenResult Open() => Memory is { } m ? new(m, null) : new(null, Failure ?? HwInfoMessages.NotRunning);
    }

    private static SynthBuffer.Reading R(int type, uint sensor, uint id, string label, string unit, double value) =>
        new(type, sensor, id, label, unit, value);

    private static FakeHwInfoMemory Memory(long pollTime, params SynthBuffer.Reading[] readings) =>
        new(SynthBuffer.Build(1, true, pollTime, 2000, Sensors, readings));

    [Fact]
    public void Publishes_converted_values_with_metadata()
    {
        var rig = new Rig
        {
            Memory = Memory(Now, R(1, 0, 0x8000000, "Core 0 (Tctl/Tdie)", "°C", 61.5),
                R(7, 0, 0x8000001, "Total CPU Usage", "%", 12),
                R(8, 1, 0x1, "Total Host Writes", "GB", 3),
                R(8, 1, 0x2, "Drive Failure", "Yes/No", 1)),
        };

        Assert.True(rig.Poll());

        var temp = rig.Hub.Get("hwinfo/f0000300-0/08000000")!;
        Assert.Equal(61.5, temp.Value);
        Assert.Equal("Core 0 (Tctl/Tdie)", temp.Info.Name);
        Assert.Equal("CPU [#0]: AMD Ryzen 9 7900X", temp.Info.Device);
        Assert.Equal(SensorCategory.Cpu, temp.Info.Category);
        Assert.Equal(SensorKinds.Temperature, temp.Info.Kind);
        Assert.Equal(Units.Celsius, temp.Info.Unit);
        Assert.Equal((20, 100), (temp.Info.Min, temp.Info.Max));
        Assert.Equal("thermometer", temp.Info.Icon);
        Assert.Equal(SensorKinds.Load, rig.Hub.Get("hwinfo/f0000300-0/08000001")!.Info.Kind);
        var written = rig.Hub.Get("hwinfo/f0000100-0/00000001")!;
        Assert.Equal(3d * 1024 * 1024 * 1024, written.Value);
        Assert.Equal(SensorCategory.Storage, written.Info.Category);
        Assert.Equal(Units.Bytes, written.Info.Unit);
        Assert.Equal(1, rig.Hub.Get("hwinfo/f0000100-0/00000002")!.Value);
        Assert.Equal(ProviderHealth.Ok, rig.Status.Last!.Value.Health);
    }

    [Fact]
    public void Reports_unavailable_when_hwinfo_is_not_running_and_recovers()
    {
        var rig = new Rig();

        Assert.False(rig.Poll());
        Assert.Equal(ProviderHealth.Unavailable, rig.Status.Last!.Value.Health);
        Assert.Equal(HwInfoMessages.NotRunning.En, rig.Status.Last.Value.Message);
        Assert.Contains("Shared Memory Support", HwInfoMessages.NotRunning.En);
        Assert.Contains("12 hours", HwInfoMessages.NotRunning.En);

        rig.Memory = Memory(Now, R(1, 0, 1, "Temp", "°C", 40));
        Assert.True(rig.Poll());
        Assert.Equal(ProviderHealth.Ok, rig.Status.Last!.Value.Health);
        Assert.Equal(40, rig.Hub.GetNumber("hwinfo/f0000300-0/00000001"));
    }

    [Fact]
    public void Reports_access_denied_with_its_own_message()
    {
        var rig = new Rig { Failure = HwInfoMessages.AccessDenied };

        rig.Poll();

        Assert.Equal(HwInfoMessages.AccessDenied.En, rig.Status.Last!.Value.Message);
    }

    [Fact]
    public void Invalidates_values_when_the_mapping_disappears_and_repeats_report_only_once()
    {
        var rig = new Rig { Memory = Memory(Now, R(1, 0, 1, "Temp", "°C", 40)) };
        rig.Poll();
        rig.Memory = null;

        rig.Poll();
        rig.Poll();

        Assert.Null(rig.Hub.GetNumber("hwinfo/f0000300-0/00000001"));
        Assert.NotNull(rig.Hub.Get("hwinfo/f0000300-0/00000001"));
        Assert.Equal(1, rig.Status.Reports.Count(r => r.Health == ProviderHealth.Unavailable));
    }

    [Fact]
    public void Treats_a_stale_poll_time_as_orphaned_mapping()
    {
        var rig = new Rig { Memory = Memory(Now - 120, R(1, 0, 1, "Temp", "°C", 40)) };

        Assert.False(rig.Poll());

        Assert.Equal(HwInfoMessages.Stale.En, rig.Status.Last!.Value.Message);
        Assert.Contains("12 hours", HwInfoMessages.Stale.En);
        Assert.Null(rig.Hub.Get("hwinfo/f0000300-0/00000001"));
    }

    [Fact]
    public void Dead_signature_and_empty_area_are_unavailable()
    {
        var dead = new Rig { Memory = new FakeHwInfoMemory(SynthBuffer.Build(1, true, Now, 1000, Sensors, [], 0x44414544)) };
        var empty = new Rig { Memory = Memory(Now) };

        Assert.False(dead.Poll());
        Assert.False(empty.Poll());

        Assert.Equal(HwInfoMessages.ShuttingDown.En, dead.Status.Last!.Value.Message);
        Assert.Equal(HwInfoMessages.Empty.En, empty.Status.Last!.Value.Message);
    }

    [Fact]
    public void Skips_duplicate_readings_by_default_and_keeps_them_when_asked()
    {
        var duplicated = new SynthBuffer.Reading[] { R(1, 0, 5, "Temp A", "°C", 40), R(1, 0, 5, "Temp B", "°C", 41) };
        var skip = new Rig { Memory = Memory(Now, duplicated) };
        var keep = new Rig(new HwInfoSettings { SkipDuplicates = false }) { Memory = Memory(Now, duplicated) };

        skip.Poll();
        keep.Poll();

        Assert.Single(skip.Hub.GetCatalog());
        Assert.Equal("Temp A", skip.Hub.GetCatalog()[0].Name);
        Assert.Equal(2, keep.Hub.GetCatalog().Count);
        Assert.Equal(41, keep.Hub.GetNumber("hwinfo/f0000300-0/00000005~2"));
    }

    [Fact]
    public void Include_filter_matches_sensor_or_reading_names_case_insensitively()
    {
        var readings = new SynthBuffer.Reading[] { R(1, 0, 1, "Temp", "°C", 40), R(8, 1, 2, "Total Host Writes", "GB", 1) };
        var cpuOnly = new Rig(new HwInfoSettings { Include = ["cpu ["] }) { Memory = Memory(Now, readings) };
        var byLabel = new Rig(new HwInfoSettings { Include = ["HOST writes"] }) { Memory = Memory(Now, readings) };

        cpuOnly.Poll();
        byLabel.Poll();

        Assert.Equal(["hwinfo/f0000300-0/00000001"], cpuOnly.Hub.GetCatalog().Select(i => i.Id));
        Assert.Equal(["hwinfo/f0000100-0/00000002"], byLabel.Hub.GetCatalog().Select(i => i.Id));
    }

    [Fact]
    public void Removes_readings_that_disappear_and_ignores_invalid_sensor_indexes()
    {
        var rig = new Rig { Memory = Memory(Now, R(1, 0, 1, "A", "°C", 1), R(1, 0, 2, "B", "°C", 2), R(1, 9, 3, "C", "°C", 3)) };
        rig.Poll();
        Assert.Equal(2, rig.Hub.GetCatalog().Count);

        rig.Memory = Memory(Now + 1, R(1, 0, 1, "A", "°C", 1));
        rig.Poll();

        Assert.Equal(["hwinfo/f0000300-0/00000001"], rig.Hub.GetCatalog().Select(i => i.Id));
    }

    [Fact]
    public void Republishes_only_on_new_poll_time_or_changed_values()
    {
        var rig = new Rig { Memory = Memory(Now, R(1, 0, 1, "A", "°C", 1)) };
        rig.Poll();
        _ = rig.Hub.GetChangedSince(0, out var version);

        rig.Poll();
        Assert.Empty(rig.Hub.GetChangedSince(version, out version));

        rig.Memory = Memory(Now, R(1, 0, 1, "A", "°C", 2)); // same second, new value
        rig.Poll();
        Assert.Single(rig.Hub.GetChangedSince(version, out version));

        rig.Memory = Memory(Now + 2, R(1, 0, 1, "A", "°C", 2)); // new poll, same value: refresh the timestamp
        rig.Poll();
        Assert.Single(rig.Hub.GetChangedSince(version, out _));
    }

    [Fact]
    public void Nan_values_invalidate_the_sensor()
    {
        var rig = new Rig { Memory = Memory(Now, R(1, 0, 1, "A", "°C", 1)) };
        rig.Poll();

        rig.Memory = Memory(Now + 1, R(1, 0, 1, "A", "°C", double.NaN));
        rig.Poll();

        Assert.Null(rig.Hub.GetNumber("hwinfo/f0000300-0/00000001"));
    }

    [Fact]
    public async Task Run_loop_retries_and_stops_on_cancellation()
    {
        var rig = new Rig();
        var provider = new HwInfoProvider("hwinfo", new HwInfoSettings { Interval = TimeSpan.FromMilliseconds(20) },
            () => rig.Memory is { } m ? new(m, null) : new(null, HwInfoMessages.NotRunning), Encoding.Latin1,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(20));
        rig.Memory = Memory(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), R(1, 0, 1, "A", "°C", 5));
        using var cts = new CancellationTokenSource();
        var hub = new SensorHub();

        var run = provider.RunAsync(hub.CreateSink("hwinfo"), rig.Status, cts.Token);
        for (var i = 0; i < 200 && hub.GetNumber("hwinfo/f0000300-0/00000001") is null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(5, hub.GetNumber("hwinfo/f0000300-0/00000001"));
        Assert.Equal(ProviderHealth.Ok, rig.Status.Last!.Value.Health);
    }
}
