using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Tests.Sources;

public class NumberParserTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("  42 \n", 42)]
    [InlineData("-3.5", -3.5)]
    [InlineData("+7", 7)]
    [InlineData("1e3", 1000)]
    [InlineData("2.5E-2", 0.025)]
    [InlineData(".5", 0.5)]
    [InlineData("42.", 42)]
    [InlineData("42,5", 42.5)]
    [InlineData("-0,25", -0.25)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("1,234,567", 1234567)]
    public void Parses_plain_and_grouped_numbers(string text, double expected)
    {
        Assert.True(NumberParser.TryParse(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1.2.3")]
    [InlineData("1,2,3")]
    [InlineData("--1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("0x10")]
    [InlineData("4 2")]
    [InlineData("42%")]
    public void Rejects_everything_else(string? text)
    {
        Assert.False(NumberParser.TryParse(text, out var value));
        Assert.Equal(0, value);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("CPU: 42.5 %", 42.5)]
    [InlineData("-3.5 dB", -3.5)]
    [InlineData("Temp +45.0°C", 45)]
    [InlineData("5 apples and 6 pears", 5)]
    [InlineData("Total: 1,234.5 MB", 1234.5)]
    [InlineData("load 0,75.", 0.75)]
    [InlineData("done in 42.", 42)]
    public void Finds_the_first_number(string text, double expected)
    {
        Assert.True(NumberParser.TryFindFirst(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no digits here")]
    [InlineData("version 1.2.3")]
    public void Finds_nothing_when_there_is_no_clean_number(string? text) =>
        Assert.False(NumberParser.TryFindFirst(text, out _));
}

public class LocalIdTests
{
    [Theory]
    [InlineData("1.1.1.1", "1-1-1-1")]
    [InlineData("Wi-Fi 2", "wi-fi-2")]
    [InlineData("  Ethernet  ", "ethernet")]
    [InlineData("vEthernet (WSL)", "vethernet-wsl")]
    [InlineData("Perché?", "perche")]
    [InlineData("C:\\", "c")]
    [InlineData("2001:4860:4860::8888", "2001-4860-4860-8888")]
    [InlineData("eth0", "eth0")]
    [InlineData("a___b", "a-b")]
    public void Slug_makes_one_clean_segment(string text, string expected) => Assert.Equal(expected, LocalIds.Slug(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("日本語")]
    public void Slug_falls_back_when_nothing_usable_remains(string? text)
    {
        Assert.Equal("item", LocalIds.Slug(text));
        Assert.Equal("fallback", LocalIds.Slug(text, "fallback"));
    }

    [Fact]
    public void Slug_limits_the_length()
    {
        var slug = LocalIds.Slug(new string('a', 500));
        Assert.True(slug.Length <= 64);
        Assert.True(LocalIds.IsValid(slug));
    }

    [Theory]
    [InlineData("Room/Living Temp", "room/living-temp")]
    [InlineData("a//b/", "a/b")]
    [InlineData("/lead/trail/", "lead/trail")]
    [InlineData("CPU Temp", "cpu-temp")]
    [InlineData("temp.outdoor/°C", "temp-outdoor/c")]
    public void SlugPath_keeps_slashes(string text, string expected) => Assert.Equal(expected, LocalIds.SlugPath(text));

    [Fact]
    public void SlugPath_falls_back_when_empty()
    {
        Assert.Equal("item", LocalIds.SlugPath("///"));
        Assert.Equal("x", LocalIds.SlugPath(null, "x"));
    }

    [Theory]
    [InlineData("cpu/load", true)]
    [InlineData("net/eth0/rx", true)]
    [InlineData("1-1-1-1/rtt", true)]
    [InlineData("a", true)]
    [InlineData("0", true)]
    [InlineData("a_b.c-d/e", true)]
    [InlineData("Upper", false)]
    [InlineData("-lead", false)]
    [InlineData("/lead", false)]
    [InlineData("_lead", false)]
    [InlineData(".lead", false)]
    [InlineData("has space", false)]
    [InlineData("bad!", false)]
    [InlineData("tab\t", false)]
    [InlineData("new\nline", false)]
    [InlineData("trailing\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_follows_the_documented_pattern(string? id, bool expected) => Assert.Equal(expected, LocalIds.IsValid(id));

    [Fact]
    public void IsValid_limits_the_length()
    {
        Assert.True(LocalIds.IsValid(new string('a', LocalIds.MaxLength)));
        Assert.False(LocalIds.IsValid(new string('a', LocalIds.MaxLength + 1)));
    }

    [Fact]
    public void Unique_appends_a_counter_and_remembers_the_result()
    {
        var used = new HashSet<string>();
        Assert.Equal("a", LocalIds.Unique("a", used));
        Assert.Equal("a-2", LocalIds.Unique("a", used));
        Assert.Equal("a-3", LocalIds.Unique("a", used));
        Assert.Equal("b", LocalIds.Unique("b", used));
        Assert.Contains("a-3", used);
    }
}

public class CounterRateTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_sample_has_no_rate_then_deltas_are_divided_by_time()
    {
        var rate = new CounterRate();
        Assert.Null(rate.Update("rx", 1000, At(0)));
        Assert.Equal(500, rate.Update("rx", 2000, At(2)));
        Assert.Equal(0, rate.Update("rx", 2000, At(3)));
    }

    [Fact]
    public void A_counter_going_backwards_restarts_the_baseline()
    {
        var rate = new CounterRate();
        rate.Update("rx", 5000, At(0));
        Assert.Null(rate.Update("rx", 100, At(1)));
        Assert.Equal(50, rate.Update("rx", 150, At(2)));
    }

    [Fact]
    public void No_elapsed_time_gives_no_rate_and_keeps_the_baseline()
    {
        var rate = new CounterRate();
        rate.Update("rx", 0, At(1));
        Assert.Null(rate.Update("rx", 100, At(1)));

        // The ignored sample did not move the baseline, so the rate is measured from the first one.
        Assert.Equal(200, rate.Update("rx", 200, At(2)));
    }

    [Fact]
    public void Keys_are_independent_and_can_be_forgotten()
    {
        var rate = new CounterRate();
        rate.Update("a/rx", 0, At(0));
        rate.Update("a/tx", 0, At(0));
        rate.Update("b/rx", 0, At(0));
        rate.ForgetPrefix("a/");
        Assert.Null(rate.Update("a/rx", 100, At(1)));
        Assert.Equal(100, rate.Update("b/rx", 100, At(1)));
        rate.Forget("b/rx");
        Assert.Null(rate.Update("b/rx", 200, At(2)));
    }
}

public class SensorRegistryTests
{
    [Fact]
    public void Defines_once_applies_default_staleness_and_removes_by_prefix()
    {
        var hub = new SensorHub();
        var registry = new SensorRegistry(hub.CreateSink("p"), defaultStaleAfterSeconds: 77);

        registry.Define(new SensorInfo { Id = "net/a/rx", Name = "A" });
        registry.Define(new SensorInfo { Id = "net/a/tx", Name = "A" });
        registry.Define(new SensorInfo { Id = "net/b/rx", Name = "B", StaleAfterSeconds = 5 });
        var catalogVersion = hub.CatalogVersion;

        registry.Define(new SensorInfo { Id = "net/a/rx", Name = "A" }); // identical: ignored
        Assert.Equal(catalogVersion, hub.CatalogVersion);

        Assert.True(registry.IsDefined("net/a/rx"));
        Assert.Equal(77, hub.Get("p/net/a/rx")!.Info.StaleAfterSeconds);
        Assert.Equal(5, hub.Get("p/net/b/rx")!.Info.StaleAfterSeconds);

        registry.Publish("net/a/rx", 12);
        Assert.Equal(12, hub.GetNumber("p/net/a/rx"));

        Assert.Equal(2, registry.RemovePrefix("net/a/"));
        Assert.Null(hub.Get("p/net/a/rx"));
        Assert.NotNull(hub.Get("p/net/b/rx"));
        Assert.False(registry.IsDefined("net/a/tx"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Changed_metadata_is_redefined()
    {
        var hub = new SensorHub();
        var registry = new SensorRegistry(hub.CreateSink("p"));
        registry.Define(new SensorInfo { Id = "x", Name = "X", Max = 10 });
        registry.Define(new SensorInfo { Id = "x", Name = "X", Max = 20 });
        Assert.Equal(20, hub.Get("p/x")!.Info.Max);
    }
}
