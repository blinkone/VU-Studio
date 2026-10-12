using VuStudio.Sources.Script;

namespace VuStudio.Tests.Sources;

public class ScriptOutputParserTests
{
    private static string Describe(ScriptOutput output) =>
        string.Join("; ", output.Values.Select(v => $"{v.Key}={(v.Number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "'" + v.Text + "'")}"));

    // ----- auto -----

    [Theory]
    [InlineData("42", 42)]
    [InlineData("42\n", 42)]
    [InlineData("  \r\n 42.5 \r\n", 42.5)]
    [InlineData("-3.5", -3.5)]
    [InlineData("1e3", 1000)]
    [InlineData("42,5", 42.5)]
    [InlineData("\uFEFF17", 17)]
    public void Auto_reads_a_single_number_as_value(string output, double expected)
    {
        var parsed = ScriptOutputParser.Parse(output);
        Assert.Equal(ScriptOutputFormat.Number, parsed.Format);
        var value = Assert.Single(parsed.Values);
        Assert.Equal("value", value.Key);
        Assert.Equal(expected, value.Number);
        Assert.Null(value.Text);
    }

    [Fact]
    public void Auto_flattens_a_json_object_numbers_as_paths_and_strings_as_text()
    {
        var parsed = ScriptOutputParser.Parse("""
            {
              "cpu": 42.5,
              "mem": { "used": 1024, "total": 4096, "label": "RAM" },
              "disks": [ { "name": "sda", "temp": 38 }, { "name": "sdb", "temp": 41 } ],
              "ok": true,
              "off": false,
              "nothing": null,
              "list": [1, null, 3]
            }
            """);

        Assert.Equal(ScriptOutputFormat.Json, parsed.Format);
        Assert.Equal(
            "cpu=42.5; mem/used=1024; mem/total=4096; mem/label='RAM'; disks/0/name='sda'; disks/0/temp=38; disks/1/name='sdb'; disks/1/temp=41; ok=1; off=0; list/0=1; list/2=3",
            Describe(parsed));
        Assert.NotNull(parsed.Json);
    }

    [Fact]
    public void Auto_flattens_a_json_array()
    {
        var parsed = ScriptOutputParser.Parse("[10, 20, \"x\"]");
        Assert.Equal(ScriptOutputFormat.Json, parsed.Format);
        Assert.Equal("0=10; 1=20; 2='x'", Describe(parsed));
    }

    [Theory]
    [InlineData("cpu=45.5\nmem: 70\nname = Ubuntu 24.04\n")]
    [InlineData("cpu=45.5\r\nmem: 70\r\nname = Ubuntu 24.04\r\n")]
    [InlineData("# a comment\n\ncpu=45.5\nthis line has no separator at all\nmem: 70\nname = Ubuntu 24.04")]
    public void Auto_reads_key_value_lines(string output)
    {
        var parsed = ScriptOutputParser.Parse(output);
        Assert.Equal(ScriptOutputFormat.KeyValue, parsed.Format);
        Assert.Equal("cpu=45.5; mem=70; name='Ubuntu 24.04'", Describe(parsed));
    }

    [Fact]
    public void Key_value_lines_split_at_the_first_separator_only()
    {
        var parsed = ScriptOutputParser.Parse("time: 12:30:45\nurl=https://example.test/a?b=c\nequation: x=y");
        Assert.Equal("time='12:30:45'; url='https://example.test/a?b=c'; equation='x=y'", Describe(parsed));
    }

    [Fact]
    public void Key_value_values_are_unquoted_and_numbers_recognised()
    {
        var parsed = ScriptOutputParser.Parse("""
            greeting = "Hello World"
            quoted_number = '5'
            comma = 3,5
            empty =
            """);
        Assert.Equal("greeting='Hello World'; quoted_number=5; comma=3.5", Describe(parsed));
    }

    [Fact]
    public void A_repeated_key_keeps_its_place_and_takes_the_latest_value()
    {
        var parsed = ScriptOutputParser.Parse("a=1\nb=2\nA=3");
        Assert.Equal(["A", "b"], parsed.Values.Select(v => v.Key));
        Assert.Equal([3.0, 2.0], parsed.Values.Select(v => v.Number!.Value));
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("[INFO] cpu=5 warm")]
    [InlineData("== heading ==")]
    public void Prose_and_log_lines_are_not_mistaken_for_keys(string output)
    {
        var parsed = ScriptOutputParser.Parse(output);
        Assert.NotEqual(ScriptOutputFormat.Json, parsed.Format);
        if (parsed.Format == ScriptOutputFormat.KeyValue)
        {
            // "http://example.test" is a legitimate "http" = "//example.test" pair; nothing else may sneak in.
            Assert.Equal("http", Assert.Single(parsed.Values).Key);
        }
    }

    [Fact]
    public void Auto_falls_back_to_the_first_line_as_text()
    {
        var parsed = ScriptOutputParser.Parse("All systems nominal\nsecond line\n");
        Assert.Equal(ScriptOutputFormat.Text, parsed.Format);
        Assert.Equal("value='All systems nominal'", Describe(parsed));
    }

    [Fact]
    public void Auto_treats_broken_json_as_something_else_instead_of_failing()
    {
        var parsed = ScriptOutputParser.Parse("[INFO] starting up");
        Assert.Equal(ScriptOutputFormat.Text, parsed.Format);
        Assert.Equal("value='[INFO] starting up'", Describe(parsed));

        var braces = ScriptOutputParser.Parse("{\"cpu\": 1,");
        Assert.Equal(ScriptOutputFormat.Text, braces.Format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    [InlineData("\uFEFF")]
    public void Empty_output_is_empty_in_every_mode(string? output)
    {
        foreach (var mode in Enum.GetValues<ScriptParseMode>())
        {
            var parsed = ScriptOutputParser.Parse(output, mode);
            Assert.True(parsed.IsEmpty);
            Assert.Equal(ScriptOutputFormat.None, parsed.Format);
        }
    }

    // ----- number -----

    [Theory]
    [InlineData("42", 42)]
    [InlineData("CPU: 42.5 %", 42.5)]
    [InlineData("Temp +45.0°C", 45)]
    [InlineData("7\n8", 7)]
    [InlineData("-0,5", -0.5)]
    public void Number_mode_takes_the_first_number(string output, double expected)
    {
        var parsed = ScriptOutputParser.Parse(output, ScriptParseMode.Number);
        Assert.Equal(ScriptOutputFormat.Number, parsed.Format);
        Assert.Equal(expected, Assert.Single(parsed.Values).Number);
    }

    [Fact]
    public void Number_mode_fails_clearly_without_a_number()
    {
        var error = Assert.Throws<FormatException>(() => ScriptOutputParser.Parse("no digits here", ScriptParseMode.Number));
        Assert.Contains("No number", error.Message);
    }

    // ----- keyvalue -----

    [Fact]
    public void Key_value_mode_reads_pairs_and_ignores_the_rest()
    {
        var parsed = ScriptOutputParser.Parse("a=1\nb: two\njunk\n=nokey", ScriptParseMode.KeyValue);
        Assert.Equal("a=1; b='two'", Describe(parsed));
    }

    [Fact]
    public void Key_value_mode_does_not_look_for_json_or_numbers()
    {
        Assert.Throws<FormatException>(() => ScriptOutputParser.Parse("42", ScriptParseMode.KeyValue));
        Assert.Throws<FormatException>(() => ScriptOutputParser.Parse("""{"a":1}""", ScriptParseMode.KeyValue));
    }

    [Fact]
    public void Key_value_mode_fails_clearly_without_pairs()
    {
        var error = Assert.Throws<FormatException>(() => ScriptOutputParser.Parse("just words", ScriptParseMode.KeyValue));
        Assert.Contains("key=value", error.Message);
    }

    // ----- json -----

    [Fact]
    public void Json_mode_tolerates_comments_and_trailing_commas()
    {
        var parsed = ScriptOutputParser.Parse("""{ /* c */ "a": 1, "b": { "c": "x", }, }""", ScriptParseMode.Json);
        Assert.Equal("a=1; b/c='x'", Describe(parsed));
    }

    [Fact]
    public void Json_mode_accepts_a_bare_scalar_as_value()
    {
        Assert.Equal("value=7", Describe(ScriptOutputParser.Parse("7", ScriptParseMode.Json)));
        Assert.Equal("value='hi'", Describe(ScriptOutputParser.Parse("\"hi\"", ScriptParseMode.Json)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"a\": ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Json_mode_fails_clearly_on_invalid_or_empty_documents(string output)
    {
        var error = Assert.Throws<FormatException>(() => ScriptOutputParser.Parse(output, ScriptParseMode.Json));
        Assert.Contains("JSON", error.Message);
    }

    [Fact]
    public void Json_output_is_limited_in_size_and_depth()
    {
        var many = "{" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"\"k{i}\":{i}")) + "}";
        Assert.Equal(256, ScriptOutputParser.Parse(many).Values.Count);

        // Nesting beyond the depth limit yields nothing: an explicit json parse says so, auto falls back to text.
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 30)) + "1" + new string('}', 30);
        Assert.Throws<FormatException>(() => ScriptOutputParser.Parse(deep, ScriptParseMode.Json));
        Assert.Equal(ScriptOutputFormat.Text, ScriptOutputParser.Parse(deep).Format);

        var shallow = string.Concat(Enumerable.Repeat("{\"a\":", 5)) + "1" + new string('}', 5);
        Assert.Equal("a/a/a/a/a=1", Describe(ScriptOutputParser.Parse(shallow)));
    }

    // ----- helpers -----

    [Fact]
    public void TryParse_reports_the_message_instead_of_throwing()
    {
        Assert.True(ScriptOutputParser.TryParse("42", ScriptParseMode.Auto, out var ok, out var noError));
        Assert.Null(noError);
        Assert.Equal(42, ok.Values[0].Number);

        Assert.False(ScriptOutputParser.TryParse("nope", ScriptParseMode.Json, out var failed, out var error));
        Assert.True(failed.IsEmpty);
        Assert.Contains("JSON", error);
    }

    [Theory]
    [InlineData("auto", ScriptParseMode.Auto)]
    [InlineData("AUTO", ScriptParseMode.Auto)]
    [InlineData("number", ScriptParseMode.Number)]
    [InlineData("keyvalue", ScriptParseMode.KeyValue)]
    [InlineData("key-value", ScriptParseMode.KeyValue)]
    [InlineData(" json ", ScriptParseMode.Json)]
    [InlineData("whatever", ScriptParseMode.Auto)]
    [InlineData(null, ScriptParseMode.Auto)]
    public void ParseMode_understands_the_setting_values(string? text, ScriptParseMode expected) =>
        Assert.Equal(expected, ScriptOutputParser.ParseMode(text));

    [Fact]
    public void Select_finds_values_by_json_path()
    {
        var parsed = ScriptOutputParser.Parse("""{"main":{"temp":21.5},"list":[{"v":"a"},{"v":"b"}]}""");
        Assert.Equal(21.5, parsed.Select("$.main.temp")!.GetValue<double>());
        Assert.Equal("b", parsed.Select("list[1].v")!.GetValue<string>());
        Assert.Null(parsed.Select("$.main.nothing"));
    }

    [Fact]
    public void Select_finds_values_by_flattened_key_ignoring_case_and_punctuation()
    {
        var json = ScriptOutputParser.Parse("""{"main":{"temp":21.5}}""");
        Assert.Equal(21.5, json.Select("main/temp")!.GetValue<double>());
        Assert.Equal(21.5, json.Select("Main/Temp")!.GetValue<double>());

        var pairs = ScriptOutputParser.Parse("cpu_temp=61\nName=Rome");
        Assert.Equal(61, pairs.Select("CPU Temp")!.GetValue<double>());
        Assert.Equal(61, pairs.Select("cpu-temp")!.GetValue<double>());
        Assert.Equal("Rome", pairs.Select("name")!.GetValue<string>());
        Assert.Null(pairs.Select("missing"));

        var single = ScriptOutputParser.Parse("42");
        Assert.Equal(42, single.Select("value")!.GetValue<double>());
        Assert.Equal(42, single.Select("$.value")!.GetValue<double>());
    }
}

public class CommandLineTokenizerTests
{
    [Theory]
    [InlineData("echo a b", new[] { "echo", "a", "b" })]
    [InlineData("  echo   a\tb\n c  ", new[] { "echo", "a", "b", "c" })]
    [InlineData("echo \"a b\" c", new[] { "echo", "a b", "c" })]
    [InlineData("echo 'a b' c", new[] { "echo", "a b", "c" })]
    [InlineData("echo \"it's\" 'say \"hi\"'", new[] { "echo", "it's", "say \"hi\"" })]
    [InlineData("echo \"a b\"c", new[] { "echo", "a bc" })]
    [InlineData("echo \"\" x", new[] { "echo", "", "x" })]
    [InlineData("echo a\\\"b", new[] { "echo", "a\"b" })]
    [InlineData("echo \"a\\\"b\"", new[] { "echo", "a\"b" })]
    [InlineData("\"C:\\Program Files\\app.exe\" /x", new[] { "C:\\Program Files\\app.exe", "/x" })]
    [InlineData("C:\\Tools\\app.exe arg", new[] { "C:\\Tools\\app.exe", "arg" })]
    [InlineData("\\\\server\\share\\x.exe", new[] { "\\\\server\\share\\x.exe" })]
    [InlineData("echo \"unterminated quote", new[] { "echo", "unterminated quote" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void Splits_like_a_command_line(string input, string[] expected) =>
        Assert.Equal(expected, CommandLineTokenizer.Split(input));

    [Fact]
    public void Backslashes_before_a_closing_quote_follow_the_windows_rules()
    {
        // Two backslashes then a quote: one literal backslash, and the quote closes the group.
        Assert.Equal(["C:\\dir\\", "next"], CommandLineTokenizer.Split("\"C:\\dir\\\\\" next"));
    }
}
