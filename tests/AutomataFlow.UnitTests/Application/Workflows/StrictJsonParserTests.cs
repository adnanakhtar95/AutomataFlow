using System.Text;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 4 step 3: the fail-closed JSON layer (docs/PHASE4_PLAN.md §1, §13.1 Parser, §16 D5).</summary>
public class StrictJsonParserTests
{
    private static JsonParseResult Parse(string json) => StrictJsonParser.Parse(Encoding.UTF8.GetBytes(json));

    private static void AssertCode(string json, string code, string path = "$")
    {
        var result = Parse(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Root);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(path, result.Error.Path);
    }

    [Fact]
    public void A_valid_document_parses_into_an_immutable_tree_with_sorted_properties()
    {
        var result = Parse("""{ "b": [1, -2, true, false, null, "x"], "a": { "z": 1, "Z": 2 } }""");

        Assert.True(result.IsValid);
        var root = Assert.IsType<JObject>(result.Root);
        Assert.Equal(["a", "b"], root.Properties.Select(p => p.Key));
        var inner = Assert.IsType<JObject>(root.Properties[0].Value);
        Assert.Equal(["Z", "z"], inner.Properties.Select(p => p.Key));
        var array = Assert.IsType<JArray>(root.Properties[1].Value);
        Assert.Equal(-2, Assert.IsType<JInteger>(array.Items[1]).Value);
        Assert.IsType<JNull>(array.Items[4]);
        Assert.Equal("x", Assert.IsType<JString>(array.Items[5]).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1} x")]
    [InlineData("{\"a\":1}{}")]
    [InlineData("{'a':1}")]
    [InlineData("{a:1}")]
    [InlineData("{\"a\":1 /* comment */}")]
    [InlineData("// comment\n{}")]
    [InlineData("{\"a\":NaN}")]
    [InlineData("{\"a\":Infinity}")]
    [InlineData("{\"a\":01}")]
    [InlineData("{\"a\":+1}")]
    [InlineData("{\"a\":\"unterminated}")]
    [InlineData("{\"a\":\"bad \\x escape\"}")]
    [InlineData("{\"a\":\"raw\tcontrol\"}")]
    public void Malformed_json_is_rejected(string json)
    {
        AssertCode(json, "json-malformed");
    }

    [Fact]
    public void A_byte_order_mark_is_rejected()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{}")).ToArray();

        var result = StrictJsonParser.Parse(bytes);

        Assert.Equal("json-bom", result.Error!.Code);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", "$.a")]
    [InlineData("{\"a\":1,\"\\u0061\":2}", "$.a")]
    [InlineData("{\"x\":{\"b\":1,\"c\":2,\"b\":3}}", "$.x.b")]
    [InlineData("{\"x\":[{\"k\":1,\"k\":1}]}", "$.x[0].k")]
    public void Duplicate_properties_are_rejected_after_unescaping(string json, string path)
    {
        AssertCode(json, "json-duplicate-property", path);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("0.5")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("1.5e-3")]
    [InlineData("-0")]
    [InlineData("-0.0")]
    public void Non_integer_numbers_are_rejected(string number)
    {
        AssertCode("{\"n\":" + number + "}", "json-non-integer-number", "$.n");
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("100000000000000000000000")]
    public void Integers_outside_the_64_bit_range_are_rejected(string number)
    {
        AssertCode("{\"n\":" + number + "}", "json-integer-out-of-range", "$.n");
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0", 0L)]
    public void Integers_at_the_64_bit_boundaries_are_accepted(string number, long expected)
    {
        var root = Assert.IsType<JObject>(Parse("{\"n\":" + number + "}").Root);

        Assert.Equal(expected, Assert.IsType<JInteger>(root.Properties[0].Value).Value);
    }

    [Theory]
    [InlineData("{\"s\":\"\\ud800\"}", "$.s")]
    [InlineData("{\"s\":\"\\udc00\"}", "$.s")]
    [InlineData("{\"s\":\"a\\ud800b\"}", "$.s")]
    [InlineData("{\"s\":\"\\udc00\\ud800\"}", "$.s")]
    [InlineData("{\"\\ud800\":1}", "$")]
    public void Strings_that_are_not_valid_UTF16_are_rejected(string json, string path)
    {
        AssertCode(json, "json-invalid-string", path);
    }

    [Fact]
    public void A_valid_surrogate_pair_is_accepted()
    {
        var root = Assert.IsType<JObject>(Parse("{\"s\":\"\\ud83d\\ude00\"}").Root);

        Assert.Equal("\U0001F600", Assert.IsType<JString>(root.Properties[0].Value).Value);
    }

    [Fact]
    public void Invalid_UTF8_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"s\":\"abc\"}");
        bytes[6] = 0xC3; // a lead byte followed by an ASCII byte is an invalid sequence
        bytes[7] = 0x28;

        var result = StrictJsonParser.Parse(bytes);

        Assert.False(result.IsValid);
        Assert.True(result.Error!.Code is "json-invalid-string" or "json-malformed", result.Error.Code);
    }

    [Fact]
    public void The_size_limit_is_checked_before_parsing_and_its_boundary_is_accepted()
    {
        var atLimit = "[" + new string(' ', WorkflowLimits.MaxDefinitionBytes - 2) + "]";
        Assert.Equal(WorkflowLimits.MaxDefinitionBytes, Encoding.UTF8.GetByteCount(atLimit));
        Assert.True(Parse(atLimit).IsValid);

        // One byte over: rejected for its size even though the content (an unterminated array) is also malformed.
        var overLimit = "[" + new string(' ', WorkflowLimits.MaxDefinitionBytes);
        AssertCode(overLimit, "json-too-large");
    }

    [Theory]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    public void Nesting_depth_is_limited(char open, char close)
    {
        static string Nest(char open, char close, int depth)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                builder.Append(open);
                if (open == '{' && i < depth - 1)
                {
                    builder.Append("\"k\":");
                }
            }

            return builder.Append(close, depth).ToString();
        }

        Assert.True(Parse(Nest(open, close, WorkflowLimits.MaxJsonDepth)).IsValid);
        var tooDeep = Parse(Nest(open, close, WorkflowLimits.MaxJsonDepth + 1));
        Assert.Equal("json-too-deep", tooDeep.Error!.Code);
    }

    [Fact]
    public void String_values_are_limited_by_decoded_length()
    {
        var atLimit = new string('a', WorkflowLimits.MaxJsonStringChars);
        Assert.True(Parse("{\"s\":\"" + atLimit + "\"}").IsValid);

        AssertCode("{\"s\":\"" + atLimit + "a\"}", "json-string-too-long", "$.s");

        // 2,048 escaped characters decode to 2,048 characters: accepted, although the raw token is six times longer.
        var escaped = string.Concat(Enumerable.Repeat("\\u0061", WorkflowLimits.MaxJsonStringChars));
        Assert.True(Parse("{\"s\":\"" + escaped + "\"}").IsValid);
    }

    [Fact]
    public void Property_names_are_limited()
    {
        var atLimit = new string('p', WorkflowLimits.MaxJsonPropertyNameChars);
        Assert.True(Parse("{\"" + atLimit + "\":1}").IsValid);

        AssertCode("{\"" + atLimit + "p\":1}", "json-property-name-too-long");
    }

    [Fact]
    public void Whitespace_around_the_root_value_is_allowed()
    {
        Assert.True(Parse(" \r\n\t{}\n ").IsValid);
    }
}
