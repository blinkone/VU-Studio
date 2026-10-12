using System.Text.Json.Nodes;
using VuStudio.Sources.Common;

namespace VuStudio.Tests.Sources;

public class JsonPathLiteTests
{
    private const string Document = """
        {
          "a": {
            "b": 42,
            "c d": "space",
            "e.f": "dot",
            "g[0]": "bracket",
            "list": [10, 20, 30]
          },
          "items": [
            { "name": "first", "tags": ["x", "y"] },
            { "name": "second", "tags": [] }
          ],
          "matrix": [[1, 2], [3, 4]],
          "nothing": null,
          "flag": true,
          "text": "hello",
          "it's": "apostrophe",
          "say \"hi\"": "quoted",
          "back\\slash": "slash",
          "0": "zero-key",
          "$ref": "dollar",
          "ünï": "unicode"
        }
        """;

    private static JsonNode Root => JsonNode.Parse(Document)!;

    private static string? Json(string path, JsonNode? root = null) =>
        JsonPathLite.Select(root ?? Root, path)?.ToJsonString();

    [Theory]
    [InlineData("$.a.b", "42")]
    [InlineData("a.b", "42")]
    [InlineData(".a.b", "42")]
    [InlineData("$['a']['b']", "42")]
    [InlineData("$[\"a\"].b", "42")]
    [InlineData("$.a['b']", "42")]
    [InlineData("['a'].b", "42")]
    [InlineData("$.text", "\"hello\"")]
    [InlineData("$.flag", "true")]
    [InlineData("$.ünï", "\"unicode\"")]
    public void Selects_properties(string path, string expected) => Assert.Equal(expected, Json(path));

    [Theory]
    [InlineData("$.a['c d']", "\"space\"")]
    [InlineData("$.a.c d", "\"space\"")]
    [InlineData("$.a['e.f']", "\"dot\"")]
    [InlineData("$.a['g[0]']", "\"bracket\"")]
    [InlineData("$['it\\'s']", "\"apostrophe\"")]
    [InlineData("$[\"it's\"]", "\"apostrophe\"")]
    [InlineData("$['say \"hi\"']", "\"quoted\"")]
    [InlineData("$[\"say \\\"hi\\\"\"]", "\"quoted\"")]
    [InlineData("$['back\\\\slash']", "\"slash\"")]
    [InlineData("$['a']['list'][1]", "20")]
    public void Selects_properties_with_awkward_names(string path, string expected) => Assert.Equal(expected, Json(path));

    [Theory]
    [InlineData("$.a.list[0]", "10")]
    [InlineData("$.a.list[2]", "30")]
    [InlineData("$.a.list[-1]", "30")]
    [InlineData("$.a.list[-3]", "10")]
    [InlineData("a.list[1]", "20")]
    [InlineData("a.list.1", "20")]
    [InlineData("a.list.-1", "30")]
    [InlineData("$.a.list[ 1 ]", "20")]
    [InlineData("$.items[0].name", "\"first\"")]
    [InlineData("items[1].name", "\"second\"")]
    [InlineData("$.items[0].tags[-1]", "\"y\"")]
    [InlineData("$.items[1].tags", "[]")]
    [InlineData("$.matrix[1][0]", "3")]
    [InlineData("matrix[0][-1]", "2")]
    [InlineData("$.matrix[-1][-1]", "4")]
    public void Selects_array_elements(string path, string expected) => Assert.Equal(expected, Json(path));

    [Theory]
    [InlineData("$.a.list[3]")]
    [InlineData("$.a.list[-4]")]
    [InlineData("$.a.list[100]")]
    [InlineData("$.missing")]
    [InlineData("$.a.missing.deeper")]
    [InlineData("$.nothing")]
    [InlineData("$.nothing.deeper")]
    [InlineData("$.text.length")]
    [InlineData("$.text[0]")]
    [InlineData("$.flag.x")]
    [InlineData("$.a.b.c")]
    [InlineData("$.items.name")]
    [InlineData("$.items[5].name")]
    [InlineData("$.A.B")]
    public void Returns_null_when_nothing_matches(string path) => Assert.Null(Json(path));

    [Fact]
    public void Dollar_empty_and_blank_paths_return_the_root()
    {
        var root = Root;
        Assert.Same(root, JsonPathLite.Select(root, "$"));
        Assert.Same(root, JsonPathLite.Select(root, ""));
        Assert.Same(root, JsonPathLite.Select(root, "   "));
    }

    [Fact]
    public void Numeric_names_index_arrays_and_address_object_keys()
    {
        Assert.Equal("\"zero-key\"", Json("$['0']"));
        Assert.Equal("\"zero-key\"", Json("$.0"));
        Assert.Equal("\"zero-key\"", Json("$[0]")); // an index on an object falls back to the key "0"
        Assert.Equal("\"dollar\"", Json("$ref"));
        Assert.Equal("\"dollar\"", Json("$.$ref"));
    }

    [Fact]
    public void Root_may_be_an_array()
    {
        var array = JsonNode.Parse("[1, 2, 3]")!;
        Assert.Equal("1", Json("$[0]", array));
        Assert.Equal("3", Json("[-1]", array));
        Assert.Equal("2", Json("$.1", array));
        Assert.Null(Json("$[3]", array));
        Assert.Null(Json("$.name", array));
    }

    [Fact]
    public void Surrounding_and_inner_whitespace_is_tolerated()
    {
        Assert.Equal("42", Json("  $.a . b  "));
        Assert.Equal("42", Json(" a.b "));
        Assert.Equal("42", Json("$ ['a'] [ 'b' ]"));
    }

    [Fact]
    public void A_null_root_has_no_values()
    {
        Assert.Null(JsonPathLite.Select(null, "$.a"));
        Assert.Null(JsonPathLite.Select(null, "$"));
    }

    [Theory]
    [InlineData("$.a[")]
    [InlineData("$.a[0")]
    [InlineData("$['a")]
    [InlineData("$['a'")]
    [InlineData("$[\"a']")]
    [InlineData("$.a[]")]
    [InlineData("$.a[ ]")]
    [InlineData("$.a[abc]")]
    [InlineData("$.a[1.5]")]
    [InlineData("$.a[--1]")]
    [InlineData("$.a[99999999999]")]
    [InlineData("$.a.")]
    [InlineData("$.a]")]
    [InlineData("$.a[0]b")]
    [InlineData("$.a['b']c")]
    public void Rejects_malformed_paths(string path)
    {
        Assert.Throws<FormatException>(() => JsonPathLite.Parse(path));
        Assert.Throws<FormatException>(() => JsonPathLite.Select(Root, path));
        Assert.False(JsonPathLite.TryParse(path, out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("$..a", "recursive")]
    [InlineData("$.a..b", "recursive")]
    [InlineData("$.*", "wildcards")]
    [InlineData("$.a.*", "wildcards")]
    [InlineData("$.a[*]", "wildcards")]
    [InlineData("$.a[0:2]", "slices")]
    [InlineData("$.a[:2]", "slices")]
    [InlineData("$.a[0,1]", "unions")]
    [InlineData("$.a[?(@.b > 1)]", "filters")]
    public void Rejects_unsupported_jsonpath_features_with_a_clear_message(string path, string mention)
    {
        var error = Assert.Throws<FormatException>(() => JsonPathLite.Parse(path));
        Assert.Contains(mention, error.Message);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void Parse_null_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => JsonPathLite.Parse(null!));
        Assert.False(JsonPathLite.TryParse(null, out _));
    }

    [Fact]
    public void TrySelect_reports_found_missing_and_invalid()
    {
        Assert.True(JsonPathLite.TrySelect(Root, "$.a.b", out var node));
        Assert.Equal(42, node!.GetValue<int>());

        Assert.False(JsonPathLite.TrySelect(Root, "$.nope", out node));
        Assert.Null(node);

        Assert.False(JsonPathLite.TrySelect(Root, "$.a[", out node));
        Assert.Null(node);

        Assert.False(JsonPathLite.TrySelect(Root, null, out _));
    }

    [Fact]
    public void A_parsed_path_can_be_evaluated_against_many_documents()
    {
        var path = JsonPathLite.Parse("$.items[0].value");
        Assert.Equal("$.items[0].value", path.Expression);
        Assert.Equal("$.items[0].value", path.ToString());

        Assert.Equal(1, path.Evaluate(JsonNode.Parse("""{"items":[{"value":1}]}"""))!.GetValue<int>());
        Assert.Equal(2, path.Evaluate(JsonNode.Parse("""{"items":[{"value":2},{"value":3}]}"""))!.GetValue<int>());
        Assert.Null(path.Evaluate(JsonNode.Parse("""{"items":[]}""")));
    }

    [Fact]
    public void Returns_the_node_itself_not_a_copy()
    {
        var root = Root;
        var list = JsonPathLite.Select(root, "$.a.list");
        Assert.IsType<JsonArray>(list);
        Assert.Same(root["a"]!["list"], list);
    }
}
