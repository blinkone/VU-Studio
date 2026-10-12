using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Script;

namespace VuStudio.Tests.Sources;

public class ScriptProviderTests
{
    private static readonly ProviderDescriptor Descriptor = new ScriptProviderFactory().Descriptor;

    private static ProcessRunResult Ok(string stdout) => new(0, stdout, "", false, TimeSpan.FromMilliseconds(5));

    private static ScriptProvider Provider(string settingsJson, Func<ScriptRequest, ProcessRunResult> run, string? device = null)
    {
        var options = ScriptOptions.From(new ProviderSettings(TestConfig.For("script", settingsJson).Settings, Descriptor));
        return new ScriptProvider("script", options, device, NullLogger.Instance, (request, _) => Task.FromResult(run(request)));
    }

    private static string Settings(string extra = "", string command = "get-it") =>
        "{\"command\":\"" + command + "\",\"interval\":0.05" + extra + "}";

    [Fact]
    public async Task A_single_number_becomes_the_sensor_value()
    {
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(Provider(Settings(), _ => Ok("42.5\n")), hub, () => hub.GetNumber("script/value") == 42.5);

        var info = hub.Get("script/value")!.Info;
        Assert.Equal("value", info.Name);
        Assert.Equal("Script", info.Device);
        Assert.Equal(SensorCategory.Custom, info.Category);
        Assert.False(string.IsNullOrWhiteSpace(info.Icon));
        Assert.True(info.StaleAfterSeconds >= 30);
    }

    [Fact]
    public async Task Json_output_becomes_numeric_and_text_sensors()
    {
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => Ok("""{"cpu":{"temp":61.5,"model":"Ryzen"},"fans":[1200,900]}"""), device: "My box"),
            hub,
            () => hub.GetNumber("script/fans/1") == 900);

        Assert.Equal(61.5, hub.GetNumber("script/cpu/temp"));
        Assert.Equal("Ryzen", hub.GetText("script/cpu/model"));
        Assert.Equal(1200, hub.GetNumber("script/fans/0"));
        Assert.Equal("cpu/temp", hub.Get("script/cpu/temp")!.Info.Name);
        Assert.Equal("My box", hub.Get("script/cpu/temp")!.Info.Device);
        Assert.Equal(SensorValueKind.Text, hub.Get("script/cpu/model")!.Info.ValueKind);
    }

    [Fact]
    public async Task Key_value_output_becomes_one_sensor_per_key_with_normalised_ids()
    {
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => Ok("CPU Temp = 61\nstatus: all good\nUptime (s): 3600")),
            hub,
            () => hub.GetNumber("script/uptime-s") == 3600);

        Assert.Equal(61, hub.GetNumber("script/cpu-temp"));
        Assert.Equal("all good", hub.GetText("script/status"));
        Assert.Equal("CPU Temp", hub.Get("script/cpu-temp")!.Info.Name);
    }

    [Fact]
    public async Task Fields_pick_and_describe_values()
    {
        var fields = """
            ,"fields":[
              {"id":"temp","name":"CPU temperature","path":"$.cpu.temp","unit":"°C","category":"cpu","min":20,"max":100},
              {"id":"model","path":"cpu.model","text":true,"category":"cpu"},
              {"id":"fan","path":"fans[-1]","multiplier":2,"unit":"RPM"},
              {"id":"nope","path":"$.nothing"}
            ]
            """;
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(fields), _ => Ok("""{"cpu":{"temp":61.5,"model":"Ryzen"},"fans":[1200,900],"unused":1}""")),
            hub,
            () => hub.GetNumber("script/fan") == 1800);

        Assert.Equal(61.5, hub.GetNumber("script/temp"));
        Assert.Equal("Ryzen", hub.GetText("script/model"));
        Assert.Null(hub.GetNumber("script/nope"));
        Assert.Null(hub.Get("script/unused")); // only the listed fields are published

        var temp = hub.Get("script/temp")!.Info;
        Assert.Equal("CPU temperature", temp.Name);
        Assert.Equal(Units.Celsius, temp.Unit);
        Assert.Equal(SensorCategory.Cpu, temp.Category);
        Assert.Equal(SensorKinds.Temperature, temp.Kind);
        Assert.Equal(20, temp.Min);
        Assert.Equal(100, temp.Max);
        Assert.Equal(SensorKinds.Fan, hub.Get("script/fan")!.Info.Kind);
    }

    [Fact]
    public async Task Fields_can_use_the_key_of_key_value_output()
    {
        var fields = ""","parse":"keyvalue","fields":[{"id":"t","path":"CPU Temp","unit":"°C"},{"id":"s","path":"status","text":true}]""";
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(fields), _ => Ok("cpu_temp=61\nStatus=fine")),
            hub,
            () => hub.GetText("script/s") == "fine");

        Assert.Equal(61, hub.GetNumber("script/t"));
    }

    [Fact]
    public async Task A_single_number_can_be_mapped_with_a_field_named_value()
    {
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(""","fields":[{"id":"load","path":"value","unit":"%","multiplier":100}]"""), _ => Ok("0.25")),
            hub,
            () => hub.GetNumber("script/load") == 25);
    }

    [Fact]
    public async Task The_request_reflects_the_settings()
    {
        ScriptRequest? seen = null;
        var extra = ",\"shell\":\"bash\",\"timeout\":3,\"workingDirectory\":\"/tmp/work\"";
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(extra, command: "echo 1"), request =>
            {
                seen = request;
                return Ok("1");
            }),
            hub,
            () => seen is not null && hub.GetNumber("script/value") == 1);

        Assert.Equal("echo 1", seen!.Command);
        Assert.Equal(ScriptShell.Bash, seen.Shell);
        Assert.Equal(TimeSpan.FromSeconds(3), seen.Timeout);
        Assert.Equal("/tmp/work", seen.WorkingDirectory);
    }

    [Fact]
    public async Task Values_that_disappear_from_the_output_go_offline()
    {
        var outputs = new Queue<string>(["a=1\nb=2", "a=3"]);
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => Ok(outputs.Count > 1 ? outputs.Dequeue() : outputs.Peek())),
            hub,
            () => hub.GetNumber("script/a") == 3 && hub.GetNumber("script/b") is null);

        Assert.NotNull(hub.Get("script/b"));
    }

    [Fact]
    public async Task A_failing_script_degrades_the_provider_with_the_first_line_of_stderr()
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => new ProcessRunResult(2, "", "\nboom: disk on fire\nsecond line", false, TimeSpan.Zero)),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        var message = status.Last!.Value.Message!;
        Assert.Contains("code 2", message);
        Assert.Contains("boom: disk on fire", message);
        Assert.DoesNotContain("second line", message);
    }

    [Fact]
    public async Task A_script_that_times_out_degrades_the_provider()
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(",\"timeout\":4"), _ => new ProcessRunResult(-1, "", "", true, TimeSpan.FromSeconds(4))),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        Assert.Contains("4 s", status.Last!.Value.Message);
        Assert.Contains("stopped", status.Last!.Value.Message);
    }

    [Theory]
    [InlineData("", "printed nothing")]
    [InlineData("   \n", "printed nothing")]
    public async Task A_script_that_prints_nothing_degrades_the_provider(string stdout, string expected)
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => Ok(stdout)),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);
        Assert.Contains(expected, status.Last!.Value.Message);
    }

    [Theory]
    [InlineData("number", "no digits here", "No number")]
    [InlineData("json", "{broken", "JSON")]
    [InlineData("keyvalue", "plain words", "key=value")]
    public async Task Output_in_the_wrong_format_degrades_the_provider(string parse, string stdout, string expected)
    {
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(",\"parse\":\"" + parse + "\""), _ => Ok(stdout)),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);
        Assert.Contains(expected, status.Last!.Value.Message);
    }

    [Fact]
    public async Task A_missing_command_is_reported_without_running_anything()
    {
        var ran = false;
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(
            Provider("""{"interval":0.05}""", _ =>
            {
                ran = true;
                return Ok("1");
            }),
            new SensorHub(),
            () => status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        Assert.Contains("No command", status.Last!.Value.Message);
        Assert.False(ran);
    }

    [Fact]
    public async Task Unusable_field_rows_are_reported_but_good_ones_still_publish()
    {
        var status = new RecordingStatus();
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(""","fields":[{"id":"ok","path":"a"},{"id":"bad","path":"a["}]"""), _ => Ok("a=5")),
            hub,
            () => hub.GetNumber("script/ok") == 5 && status.Last?.Health == ProviderHealth.Degraded,
            status: status);

        Assert.Contains("Field 2", status.Last!.Value.Message);
    }

    [Fact]
    public async Task Recovers_when_the_script_works_again()
    {
        var calls = 0;
        var status = new RecordingStatus();
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(
            Provider(Settings(), _ => Interlocked.Increment(ref calls) <= 2 ? new ProcessRunResult(1, "", "bad", false, TimeSpan.Zero) : Ok("7")),
            hub,
            () => hub.GetNumber("script/value") == 7 && status.Last?.Health == ProviderHealth.Ok,
            status: status);

        Assert.Contains(status.Reports, r => r.Health == ProviderHealth.Degraded);
    }

    // ----- through the real runner -----

    [Fact]
    public async Task Runs_a_real_command_and_publishes_its_output()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows() && ExecutableLocator.Find("sh") is not null, "needs a POSIX shell");

        var config = TestConfig.For("script", """{"shell":"sh","command":"echo 12","interval":0.2}""");
        var provider = new ScriptProviderFactory().Create(config, TestServices.Empty());
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber("script/value") == 12);
    }

    [Fact]
    public async Task Runs_a_real_command_that_prints_json_and_applies_fields()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows() && ExecutableLocator.Find("sh") is not null, "needs a POSIX shell");

        var config = TestConfig.For("script", """
            {
              "shell": "sh",
              "command": "echo '{\"net\":{\"rx\":1500,\"tx\":300}}'",
              "interval": 0.2,
              "fields": [ { "id": "download", "path": "$.net.rx", "unit": "B/s", "category": "network" } ]
            }
            """);
        var provider = new ScriptProviderFactory().Create(config, TestServices.Empty());
        var hub = new SensorHub();
        await ProviderRunner.RunUntilAsync(provider, hub, () => hub.GetNumber("script/download") == 1500);

        Assert.Equal(SensorCategory.Network, hub.Get("script/download")!.Info.Category);
        Assert.Equal(SensorKinds.Throughput, hub.Get("script/download")!.Info.Kind);
    }

    [Fact]
    public async Task A_real_command_that_fails_degrades_the_provider()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows() && ExecutableLocator.Find("sh") is not null, "needs a POSIX shell");

        var config = TestConfig.For("script", """{"shell":"sh","command":"echo trouble 1>&2; exit 4","interval":0.2}""");
        var provider = new ScriptProviderFactory().Create(config, TestServices.Empty());
        var status = new RecordingStatus();
        await ProviderRunner.RunUntilAsync(provider, new SensorHub(), () => status.Last?.Health == ProviderHealth.Degraded, status: status);

        Assert.Contains("code 4", status.Last!.Value.Message);
        Assert.Contains("trouble", status.Last!.Value.Message);
    }
}
