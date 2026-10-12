using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Clock;
using VuStudio.Sources.Common;
using VuStudio.Sources.Demo;
using VuStudio.Sources.Http;
using VuStudio.Sources.Pings;
using VuStudio.Sources.Processes;
using VuStudio.Sources.Push;
using VuStudio.Sources.Script;
using VuStudio.Sources.SystemMetrics;

namespace VuStudio.Tests.Sources;

public partial class ProviderFactoryTests
{
    private static readonly System.Reflection.Assembly SourcesAssembly = typeof(ClockProviderFactory).Assembly;

    private static readonly string[] OurTypes = ["clock", "demo", "http-json", "ping", "process", "push", "script", "system"];

    public static TheoryData<string> AllTypes()
    {
        var data = new TheoryData<string>();
        foreach (var factory in ProviderCatalog.Discover(SourcesAssembly))
        {
            data.Add(factory.Descriptor.Type);
        }

        return data;
    }

    private static IProviderFactory Factory(string type) =>
        ProviderCatalog.Discover(SourcesAssembly).Single(f => f.Descriptor.Type == type);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex TypeId();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex SettingKey();

    // ----- the catalogue -----

    [Fact]
    public void Reflection_discovers_every_provider_of_this_assembly()
    {
        var types = ProviderCatalog.Discover(SourcesAssembly).Select(f => f.Descriptor.Type).ToList();
        Assert.All(OurTypes, expected => Assert.Contains(expected, types));
    }

    [Fact]
    public void Type_ids_are_unique_well_formed_and_match_the_class_that_creates_them()
    {
        var all = SourcesAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsPublic: true } && typeof(IProviderFactory).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IProviderFactory)Activator.CreateInstance(t)!)
            .ToList();

        Assert.Equal(all.Count, all.Select(f => f.Descriptor.Type).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, f => Assert.Matches(TypeId(), f.Descriptor.Type));
        Assert.All(all, f => Assert.Matches(ProviderManager.ValidId(), f.Descriptor.Type)); // a singleton's id is its type
        Assert.All(all, f => Assert.EndsWith("ProviderFactory", f.GetType().Name));
    }

    [Fact]
    public void Descriptors_declare_what_the_brief_asks_for()
    {
        void Check(string type, ProviderGroup group, bool singleton, bool autoCreate, bool userSession = false, bool localOnly = false)
        {
            var d = Factory(type).Descriptor;
            Assert.Equal(group, d.Group);
            Assert.Equal(singleton, d.Singleton);
            Assert.Equal(autoCreate, d.AutoCreate);
            Assert.Equal(userSession, d.RunsInUserSession);
            Assert.Equal(localOnly, d.LocalConfigOnly);
            Assert.Equal(PlatformSupport.Any, d.Platforms);
            Assert.True(d.IsSupportedHere);
        }

        Check("clock", ProviderGroup.Utility, singleton: true, autoCreate: true);
        Check("demo", ProviderGroup.Utility, singleton: true, autoCreate: false);
        Check("system", ProviderGroup.ThisPc, singleton: true, autoCreate: true);
        Check("ping", ProviderGroup.Network, singleton: false, autoCreate: false);
        Check("process", ProviderGroup.Software, singleton: false, autoCreate: false);
        Check("http-json", ProviderGroup.Online, singleton: false, autoCreate: false);
        Check("script", ProviderGroup.Utility, singleton: false, autoCreate: false, userSession: true, localOnly: true);
        Check("push", ProviderGroup.Utility, singleton: true, autoCreate: true);
    }

    // ----- descriptors -----

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Everything_the_user_reads_is_available_in_english_and_italian(string type)
    {
        var d = Factory(type).Descriptor;
        AssertTranslated(d.Name, "name of " + type);
        AssertTranslated(d.Description, "description of " + type);
        Assert.NotNull(d.Description);
        foreach (var field in d.Settings)
        {
            AssertFieldTranslated(field, type);
        }
    }

    private static void AssertFieldTranslated(SettingField field, string path)
    {
        var where = path + "/" + field.Key;
        AssertTranslated(field.Label, "label of " + where);
        if (field.Help is not null)
        {
            AssertTranslated(field.Help, "help of " + where);
        }

        foreach (var option in field.Options ?? [])
        {
            AssertTranslated(option.Label, $"option '{option.Value}' of {where}");
        }

        foreach (var column in field.Columns ?? [])
        {
            AssertFieldTranslated(column, where);
        }
    }

    private static void AssertTranslated(LocalizedText? text, string what)
    {
        Assert.NotNull(text);
        Assert.False(string.IsNullOrWhiteSpace(text.En), "English text missing: " + what);
        Assert.False(string.IsNullOrWhiteSpace(text.It), "Italian text missing: " + what);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Setting_keys_are_unique_and_tidy_and_every_setting_has_a_schema_that_makes_sense(string type)
    {
        var d = Factory(type).Descriptor;
        AssertSchema(d.Settings, type);
        Assert.NotEmpty(d.Settings);
    }

    private static void AssertSchema(IReadOnlyList<SettingField> fields, string path)
    {
        Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var field in fields)
        {
            var where = path + "/" + field.Key;
            Assert.Matches(SettingKey(), field.Key);

            if (field.Min is { } min && field.Max is { } max)
            {
                Assert.True(min <= max, "min above max: " + where);
            }

            if (field.Kind == SettingKind.Select)
            {
                Assert.NotEmpty(field.Options ?? []);
                Assert.Equal(field.Options!.Count, field.Options.Select(o => o.Value).Distinct().Count());
            }
            else
            {
                Assert.Null(field.Options);
            }

            if (field.Kind == SettingKind.Table)
            {
                Assert.NotEmpty(field.Columns ?? []);
                AssertSchema(field.Columns!, where);
                Assert.DoesNotContain(field.Columns!, c => c.Kind == SettingKind.Table); // nested tables are beyond the UI
            }
            else
            {
                Assert.Null(field.Columns);
            }

            AssertDefaultIsValid(field, where);
        }
    }

    private static void AssertDefaultIsValid(SettingField field, string where)
    {
        var node = field.Default;
        if (node is null)
        {
            Assert.True(field.Kind is not (SettingKind.Boolean or SettingKind.Select or SettingKind.Table or SettingKind.TextList),
                $"{where} ({field.Kind}) needs a default");
            return;
        }

        var kind = node.GetValueKind();
        switch (field.Kind)
        {
            case SettingKind.Text or SettingKind.Url or SettingKind.Secret or SettingKind.Multiline:
                Assert.Equal(JsonValueKind.String, kind);
                break;
            case SettingKind.Number or SettingKind.Seconds:
                Assert.Equal(JsonValueKind.Number, kind);
                var number = node.GetValue<double>();
                Assert.True(double.IsFinite(number), where);
                if (field.Min is { } lower)
                {
                    Assert.True(number >= lower, $"{where}: default {number} below minimum {lower}");
                }

                if (field.Max is { } upper)
                {
                    Assert.True(number <= upper, $"{where}: default {number} above maximum {upper}");
                }

                if (field.Kind == SettingKind.Seconds)
                {
                    Assert.True(number > 0, where);
                }

                break;
            case SettingKind.Boolean:
                Assert.True(kind is JsonValueKind.True or JsonValueKind.False, where);
                break;
            case SettingKind.Select:
                Assert.Equal(JsonValueKind.String, kind);
                Assert.Contains(node.GetValue<string>(), field.Options!.Select(o => o.Value));
                break;
            case SettingKind.TextList:
                var list = Assert.IsType<JsonArray>(node);
                Assert.All(list, item => Assert.Equal(JsonValueKind.String, item!.GetValueKind()));
                break;
            case SettingKind.Table:
                var rows = Assert.IsType<JsonArray>(node);
                var columns = field.Columns!.ToDictionary(c => c.Key);
                foreach (var row in rows)
                {
                    var cells = Assert.IsType<JsonObject>(row);
                    foreach (var (key, cell) in cells)
                    {
                        Assert.True(columns.ContainsKey(key), $"{where}: row has unknown column '{key}'");
                        AssertDefaultIsValid(columns[key] with { Default = cell?.DeepClone() }, where + "/" + key);
                    }
                }

                break;
        }
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Descriptors_serialise_like_the_api_will_send_them(string type)
    {
        var json = JsonSerializer.Serialize(Factory(type).Descriptor, Json.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("settings").GetArrayLength() > 0);
    }

    // ----- icons -----

    private static HashSet<string>? LoadIconIds()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "src", "VuStudio.Rendering", "Assets", "Icons", "icons.json");
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToHashSet();
            }
        }

        return null;
    }

    [Fact]
    public void Descriptor_icons_and_default_icons_exist_in_the_bundled_icon_set()
    {
        var icons = LoadIconIds();
        Assert.SkipWhen(icons is null, "the icon set is not next to the tests");

        foreach (var factory in ProviderCatalog.Discover(SourcesAssembly))
        {
            Assert.Contains(factory.Descriptor.Icon, icons!);
        }

        foreach (var category in Enum.GetValues<SensorCategory>())
        {
            Assert.Contains(SensorHints.Icon(category, null), icons!);
            foreach (var kind in new[] { SensorKinds.Temperature, SensorKinds.Fan, SensorKinds.Power, SensorKinds.Voltage, SensorKinds.Current, SensorKinds.Latency, SensorKinds.Duration, SensorKinds.Humidity, SensorKinds.Energy, SensorKinds.Count, SensorKinds.Load })
            {
                Assert.Contains(SensorHints.Icon(category, kind), icons!);
            }
        }
    }

    [Fact]
    public async Task Every_icon_a_provider_puts_on_a_sensor_exists_in_the_bundled_icon_set()
    {
        var icons = LoadIconIds();
        Assert.SkipWhen(icons is null, "the icon set is not next to the tests");

        var hub = new SensorHub();
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var services = TestServices.With(s => s.AddSingleton<TimeProvider>(time));

        // Providers that define their sensors up front.
        foreach (var type in new[] { "clock", "demo", "push" })
        {
            var provider = Factory(type).Create(TestConfig.For(type, type == "clock" ? """{"countdowns":[{"id":"a","target":"18:30"}]}""" : "{}"), services);
            if (provider is PollingSensorProvider)
            {
                await ProviderDriver.InitializeAsync(provider, hub.CreateSink(type));
            }
        }

        var ping = new PingProvider("ping", PingSettings.From(new ProviderSettings(TestConfig.For("ping").Settings, Factory("ping").Descriptor)), new FakePinger((_, _) => PingOutcome.NoReply()), new FakeGatewayLocator("10.0.0.1"), NullLogger.Instance);
        await ProviderDriver.InitializeAsync(ping, hub.CreateSink("ping"));
        var process = new ProcessProvider("process", ["chrome"], TimeSpan.FromSeconds(1), new FakeProcessSource(), time, 4, NullLogger.Instance);
        await ProviderDriver.InitializeAsync(process, hub.CreateSink("process"));

        // Collectors that define sensors as they find them.
        using var root = new TempDirectory("icons");
        root.Write("proc/stat", "cpu 1 0 1 8 0 0 0 0\ncpu0 1 0 1 8 0 0 0 0\n");
        root.Write("proc/loadavg", "1 1 1 1/1 1\n");
        root.Write("proc/meminfo", "MemTotal: 100 kB\nMemAvailable: 50 kB\nSwapTotal: 10 kB\nSwapFree: 5 kB\n");
        root.Write("proc/uptime", "10 10\n");
        root.Write("proc/diskstats", " 8 0 sda 1 0 8 0 1 0 8 0 0 1 1 0 0 0 0\n");
        root.CreateDirectory("sys/block/sda");
        root.Write("sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "1000000\n");
        var index = 0;
        foreach (var (chip, files) in new[]
        {
            ("k10temp", new[] { "temp1_input" }), ("nct6798", new[] { "fan1_input", "in0_input", "temp1_input" }),
            ("amdgpu", new[] { "power1_average", "freq1_input", "curr1_input", "temp1_input" }), ("mystery", new[] { "temp1_input", "power1_input" }),
        })
        {
            index++;
            root.Write($"sys/class/hwmon/hwmon{index}/name", chip);
            foreach (var file in files)
            {
                root.Write($"sys/class/hwmon/hwmon{index}/{file}", "30000");
            }
        }

        root.Write("sys/class/drm/card0/device/gpu_busy_percent", "5");
        root.Write("sys/class/drm/card0/device/mem_info_vram_total", "100");
        root.Write("sys/class/drm/card0/device/mem_info_vram_used", "10");
        root.Write("sys/class/power_supply/BAT0/type", "Battery");
        root.Write("sys/class/power_supply/BAT0/capacity", "50");
        root.Write("sys/class/power_supply/BAT0/status", "Full");
        root.Write("sys/class/power_supply/BAT0/power_now", "1000000");

        var registry = new SensorRegistry(hub.CreateSink("system"));
        var context = new SampleContext(TimeSpan.Zero, DateTimeOffset.UnixEpoch);
        var later = new SampleContext(TimeSpan.FromSeconds(1), DateTimeOffset.UnixEpoch);
        using var linux = new VuStudio.Sources.SystemMetrics.Linux.LinuxCollector(root.Path);
        linux.Collect(registry, context);
        linux.Collect(registry, later);

        var network = new FakeNetworkReader { Samples = [new NetworkInterfaceSample("eth0", true, false, false, 1_000_000_000, 0, 0)] };
        using var networkCollector = new NetworkCollector(network);
        networkCollector.Collect(registry, context);
        using var disks = new DiskUsageCollector(new FakeDriveReader { Drives = [new DriveSample("/", null, 100, 50, 50)] });
        disks.Collect(registry, context);
        using var host = new HostCollector();
        host.Collect(registry, context);
        using var windows = new WindowsCollector(new WindowsApiStub());
        windows.Collect(registry, context);
        windows.Collect(registry, later);
        using var nvidia = new NvidiaSmiCollector(() => "nvidia-smi", (_, _) => Task.FromResult(new VuStudio.Sources.Common.ProcessRunResult(0, "1, 2, 3, 4, 5, 6", "", false, TimeSpan.Zero)));
        nvidia.Collect(registry, context);
        await nvidia.Pending!;
        nvidia.Collect(registry, later);

        Assert.True(hub.Count > 60, $"only {hub.Count} sensors were defined");
        foreach (var info in hub.GetCatalog())
        {
            Assert.True(info.Icon is not null && icons!.Contains(info.Icon), $"{info.Id} uses the unknown icon '{info.Icon}'");
        }
    }

    private sealed class WindowsApiStub : IWindowsApi
    {
        private ulong _tick;

        public bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            _tick += 100;
            (idle, kernel, user) = (_tick, _tick * 2, _tick);
            return true;
        }

        public bool TryGetMemory(out WindowsMemory memory)
        {
            memory = new WindowsMemory(100, 50);
            return true;
        }
    }

    // ----- creating -----

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task Create_works_with_an_empty_service_provider(string type)
    {
        var factory = Factory(type);
        var config = new ProviderInstanceConfig { Id = type, Type = type };

        var provider = factory.Create(config, new ServiceCollection().BuildServiceProvider());

        Assert.NotNull(provider);
        Assert.Equal(type, provider.Id);
        await provider.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task Create_does_not_throw_on_nonsense_settings(string type)
    {
        var factory = Factory(type);
        foreach (var garbage in new JsonNode?[] { JsonValue.Create("not a number"), JsonValue.Create(-5), JsonValue.Create(true), new JsonArray(1, "x", null), new JsonObject { ["a"] = 1 } })
        {
            var settings = new JsonObject();
            foreach (var field in factory.Descriptor.Settings)
            {
                settings[field.Key] = garbage?.DeepClone();
            }

            var provider = factory.Create(new ProviderInstanceConfig { Id = type, Type = type, Settings = settings }, TestServices.Empty());
            await provider.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task Create_uses_the_instance_id_and_ignores_unknown_settings(string type)
    {
        var settings = new JsonObject { ["somethingNobodyKnows"] = 1 };
        var provider = Factory(type).Create(new ProviderInstanceConfig { Id = "my-instance", Type = type, Settings = settings }, TestServices.Empty());
        Assert.Equal("my-instance", provider.Id);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task The_provider_manager_runs_them_and_reports_the_script_as_a_user_session_provider()
    {
        var hub = new SensorHub();
        var factories = ProviderCatalog.Discover(SourcesAssembly);
        await using var manager = new ProviderManager(factories, hub, TestServices.Empty(), NullLogger<ProviderManager>.Instance);

        var configs = new List<ProviderInstanceConfig>();
        Assert.True(manager.EnsureAutoCreated(configs));
        var autoCreated = configs.Select(c => c.Type).ToList();
        Assert.Contains("clock", autoCreated);
        Assert.Contains("system", autoCreated);
        Assert.Contains("push", autoCreated);
        Assert.DoesNotContain("demo", autoCreated);

        configs.Add(TestConfig.For("demo", """{"interval":0.05}"""));
        configs.Add(TestConfig.For("script", """{"command":"echo 1"}"""));
        await manager.ApplyAsync(configs, TestContext.Current.CancellationToken);

        var clock = Task.Run(async () =>
        {
            while (hub.GetNumber("clock/hour") is null || hub.GetNumber("demo/sine") is null)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);
        await clock.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var script = manager.GetStatus().Single(s => s.Type == "script");
        Assert.Equal(ProviderHealth.Unavailable, script.Health);
        Assert.Contains("desktop app", script.Message);
        await manager.StopAllAsync();
    }
}

public class FieldSpecTests
{
    private static readonly ProviderDescriptor Descriptor = new HttpJsonProviderFactory().Descriptor;

    private static (IReadOnlyList<FieldSpec> Fields, List<string> Issues) Parse(string fieldsJson)
    {
        var issues = new List<string>();
        var settings = new ProviderSettings(JsonNode.Parse("{\"fields\":" + fieldsJson + "}")!.AsObject(), Descriptor);
        return (FieldSpecs.Parse(settings, issues), issues);
    }

    [Fact]
    public void Defaults_for_missing_columns()
    {
        var (fields, issues) = Parse("""[{"path":"$.a.b"}]""");
        Assert.Empty(issues);
        var field = Assert.Single(fields);
        Assert.Equal("a/b", field.Id.Replace("-", "/")); // derived from the path
        Assert.Equal(field.Id, field.Name);
        Assert.Equal("$.a.b", field.Path);
        Assert.Equal(Units.None, field.Unit);
        Assert.Equal(SensorCategory.Custom, field.Category);
        Assert.Null(field.Kind);
        Assert.Null(field.Min);
        Assert.Null(field.Max);
        Assert.Equal(1, field.Multiplier);
        Assert.False(field.IsText);
    }

    [Fact]
    public void Ids_come_from_the_id_then_the_name_then_the_path_and_are_unique()
    {
        var (fields, _) = Parse("""
            [{"id":"Room/Temp","path":"a"},{"name":"Room Temp","path":"b"},{"id":"room/temp","path":"c"},{"path":"x.y"}]
            """);
        Assert.Equal(["room/temp", "room-temp", "room/temp-2", "x-y"], fields.Select(f => f.Id));
        Assert.All(fields, f => Assert.True(LocalIds.IsValid(f.Id)));
    }

    [Fact]
    public void Categories_are_read_case_insensitively_and_unknown_ones_become_custom()
    {
        var (fields, _) = Parse("""[{"path":"a","category":"weather"},{"path":"b","category":"CPU"},{"path":"c","category":"nonsense"},{"path":"d","category":"99"}]""");
        Assert.Equal([SensorCategory.Weather, SensorCategory.Cpu, SensorCategory.Custom, SensorCategory.Custom], fields.Select(f => f.Category));
    }

    [Fact]
    public void Bad_rows_are_skipped_with_a_message()
    {
        var (fields, issues) = Parse("""[{"path":"ok"},{"id":"nopath"},{"path":"a["},{"path":"[abc]"},{"path":"fine2"}]""");
        Assert.Equal(["ok", "fine2"], fields.Select(f => f.Path));
        Assert.Equal(3, issues.Count);
        Assert.Contains("Field 2", issues[0]);
        Assert.Contains("Field 3", issues[1]);
        Assert.Contains("Field 4", issues[2]);
    }

    [Fact]
    public void Numbers_are_scaled_text_is_kept_and_nothing_is_nothing()
    {
        var (fields, _) = Parse("""[{"path":"n","multiplier":0.5},{"path":"t","text":true},{"path":"z","multiplier":0},{"path":"bad","multiplier":"abc"}]""");
        Assert.Equal(50, fields[0].Convert(JsonNode.Parse("100")).Number);
        Assert.Equal(21.5, fields[0].Convert(JsonNode.Parse("\"43\"")).Number);
        Assert.Equal(1, fields[0].Convert(JsonNode.Parse("true")).Number * 2);
        Assert.False(fields[0].Convert(JsonNode.Parse("\"abc\"")).HasValue);
        Assert.False(fields[0].Convert(JsonNode.Parse("{\"a\":1}")).HasValue);
        Assert.False(fields[0].Convert(null).HasValue);

        Assert.Equal("hello", fields[1].Convert(JsonNode.Parse("\"hello\"")).Text);
        Assert.Equal("42", fields[1].Convert(JsonNode.Parse("42")).Text);
        Assert.Equal("""{"a":1}""", fields[1].Convert(JsonNode.Parse("""{"a":1}""")).Text);
        Assert.Null(fields[1].Convert(JsonNode.Parse("\"x\"")).Number);

        Assert.Equal(0, fields[2].Convert(JsonNode.Parse("7")).Number);
        Assert.Equal(7, fields[3].Convert(JsonNode.Parse("7")).Number); // an unusable multiplier means 1
    }

    [Fact]
    public void Sensor_info_reflects_the_row()
    {
        var (fields, _) = Parse("""
            [{"id":"t","name":"Temp","path":"a","unit":"°C","category":"weather","min":-10,"max":50},
             {"id":"s","path":"b","text":true,"unit":"ignored","min":1,"max":2,"kind":"state"}]
            """);
        var number = fields[0].ToSensorInfo("Device", 90);
        Assert.Equal((SensorCategory.Weather, SensorKinds.Temperature, Units.Celsius, SensorValueKind.Number), (number.Category, number.Kind, number.Unit, number.ValueKind));
        Assert.Equal(-10, number.Min);
        Assert.Equal(50, number.Max);
        Assert.Equal("Device", number.Device);
        Assert.Equal(90, number.StaleAfterSeconds);

        var text = fields[1].ToSensorInfo(null, null);
        Assert.Equal(SensorValueKind.Text, text.ValueKind);
        Assert.Equal("state", text.Kind);
        Assert.Equal(Units.None, text.Unit);
        Assert.Null(text.Min);
        Assert.Null(text.Max);
    }

    [Fact]
    public void Category_options_cover_the_whole_enum_with_camel_case_values()
    {
        var values = SensorOptions.Categories.Select(o => o.Value).ToList();
        Assert.Equal(Enum.GetValues<SensorCategory>().Length, values.Count);
        Assert.Contains("cpu", values);
        Assert.Contains("custom", values);
        foreach (var category in Enum.GetValues<SensorCategory>())
        {
            Assert.Equal(category, SensorOptions.ParseCategory(SensorOptions.ValueOf(category), (SensorCategory)(-1)));
        }
    }

    [Fact]
    public void Kinds_are_inferred_from_units_and_staleness_from_intervals()
    {
        Assert.Equal(SensorKinds.Temperature, SensorHints.KindForUnit(Units.Celsius));
        Assert.Equal(SensorKinds.Throughput, SensorHints.KindForUnit(Units.BytesPerSecond));
        Assert.Equal(SensorKinds.Fan, SensorHints.KindForUnit(Units.Rpm));
        Assert.Null(SensorHints.KindForUnit(""));
        Assert.Null(SensorHints.KindForUnit("furlongs"));
        Assert.Equal(30, SensorHints.StaleAfterSeconds(TimeSpan.FromSeconds(1)));
        Assert.Equal(30, SensorHints.StaleAfterSeconds(TimeSpan.FromSeconds(10)));
        Assert.Equal(90, SensorHints.StaleAfterSeconds(TimeSpan.FromSeconds(30)));
        Assert.Equal(180, SensorHints.StaleAfterSeconds(TimeSpan.FromMinutes(1)));
    }
}
