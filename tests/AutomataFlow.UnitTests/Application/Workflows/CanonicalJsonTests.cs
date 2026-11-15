using System.Text;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 4 step 3: canonical form and hash (docs/PHASE4_PLAN.md §11, §16 D5, ADR-041).</summary>
public class CanonicalJsonTests
{
    /// <summary>
    /// Golden input: unordered properties, whitespace, <c>\/</c>, <c>\u00e9</c>, a surrogate-pair escape, control
    /// characters, DEL, a negative integer, and two keys whose ordinal UTF-16 order (U+1F600 as D83D DE00 before U+FF21)
    /// differs from code-point order.
    /// </summary>
    private const string GoldenInput =
        """
        {
          "steps": [
            {
              "type": "captureResult",
              "id": "done",
              "config": { "outcome": { "status": "succeeded" }, "fields": { "customerId": "{{steps.read-id.value}}" } }
            }
          ],
          "schemaVersion": 1,
          "name": "golden-sample",
          "description": "Caf\u00e9 \/ tab:\t quote:\" emoji:\ud83d\ude00 backslash:\\ line:\nend",
          "allowedHosts": [ "https://crm.example.test" ],
          "timeoutSeconds": 120,
          "inputs": [ { "maxLength": 120, "required": true, "type": "email", "name": "email" } ],
          "setup": [
            {
              "id": "open",
              "type": "delay",
              "config": { "milliseconds": 250, "offset": -12, "\uff21": "fullwidth", "\ud83d\ude00": "astral", "Z": [ true, false, null, 0 ], "ctrl": "\u001f\u0000\b\f\r\u007f" }
            }
          ]
        }
        """;

    /// <summary>Canonical text of <see cref="GoldenInput"/>, computed independently (Python reference canonicalizer).</summary>
    private const string GoldenCanonical =
        "{\"allowedHosts\":[\"https://crm.example.test\"],"
        + "\"description\":\"Caf\u00e9 / tab:\\t quote:\\\" emoji:\U0001F600 backslash:\\\\ line:\\nend\","
        + "\"inputs\":[{\"maxLength\":120,\"name\":\"email\",\"required\":true,\"type\":\"email\"}],"
        + "\"name\":\"golden-sample\",\"schemaVersion\":1,"
        + "\"setup\":[{\"config\":{\"Z\":[true,false,null,0],\"ctrl\":\"\\u001f\\u0000\\b\\f\\r\u007f\",\"milliseconds\":250,\"offset\":-12,"
        + "\"\U0001F600\":\"astral\",\"\uff21\":\"fullwidth\"},\"id\":\"open\",\"type\":\"delay\"}],"
        + "\"steps\":[{\"config\":{\"fields\":{\"customerId\":\"{{steps.read-id.value}}\"},\"outcome\":{\"status\":\"succeeded\"}},\"id\":\"done\",\"type\":\"captureResult\"}],"
        + "\"timeoutSeconds\":120}";

    /// <summary>SHA-256 of the golden canonical bytes, computed independently (Python hashlib).</summary>
    private const string GoldenHash = "sha256:3682ab466134d84fce99285304e23fea7e872a187b8f8e4d6a7946ae8031e943";

    private static byte[] Canonical(string json) => CanonicalJson.Serialize(StrictJsonParser.Parse(Encoding.UTF8.GetBytes(json)).Root!);

    [Fact]
    public void Golden_canonical_bytes_and_hash()
    {
        var workflow = WorkflowJson.Valid(GoldenInput);

        Assert.Equal(567, workflow.CanonicalUtf8.Length);
        Assert.Equal(GoldenCanonical, workflow.CanonicalJson);
        Assert.Equal(Encoding.UTF8.GetBytes(GoldenCanonical), workflow.CanonicalUtf8.ToArray());
        Assert.Equal(GoldenHash, workflow.DefinitionHash);
    }

    [Fact]
    public void Canonical_bytes_have_no_byte_order_mark()
    {
        var bytes = Canonical("{\"a\":1}");

        Assert.Equal((byte)'{', bytes[0]);
    }

    [Theory]
    [InlineData("{\"b\":\"\u00e9\",\"a\":[1,2]}")]
    [InlineData("{ \"a\" : [ 1 , 2 ] , \"b\" : \"\\u00e9\" }")]
    [InlineData("{\"a\":[1,2],\"b\":\"\\u00E9\"}")]
    [InlineData("\n\t{\r\n  \"b\": \"\\u00e9\",\n  \"a\": [\n    1,\n    2\n  ]\n}\n")]
    public void Reordering_whitespace_and_escaping_variants_have_identical_canonical_bytes(string variant)
    {
        Assert.Equal("{\"a\":[1,2],\"b\":\"\u00e9\"}", Encoding.UTF8.GetString(Canonical(variant)));
    }

    [Theory]
    [InlineData("{\"s\":\"\\/\"}", "{\"s\":\"/\"}")]
    [InlineData("{\"s\":\"\\u0041\"}", "{\"s\":\"A\"}")]
    [InlineData("{\"s\":\"\\u0008\\u000c\\u000a\\u000d\\u0009\"}", "{\"s\":\"\\b\\f\\n\\r\\t\"}")]
    [InlineData("{\"s\":\"\\u0001\\u001F\"}", "{\"s\":\"\\u0001\\u001f\"}")]
    [InlineData("{\"s\":\"\\\"\\\\\"}", "{\"s\":\"\\\"\\\\\"}")]
    [InlineData("{\"s\":\"\\u007f\\u2028<>&'\"}", "{\"s\":\"\u007f\u2028<>&'\"}")]
    public void Strings_use_minimal_escaping(string input, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(Canonical(input)));
    }

    [Fact]
    public void Properties_are_ordered_by_ordinal_UTF16_code_units()
    {
        var canonical = Encoding.UTF8.GetString(Canonical("{\"b\":1,\"\uff21\":2,\"\\ud83d\\ude00\":3,\"B\":4,\"a\":5,\"_\":6}"));

        Assert.Equal("{\"B\":4,\"_\":6,\"a\":5,\"b\":1,\"\U0001F600\":3,\"\uff21\":2}", canonical);
    }

    [Fact]
    public void Canonicalization_is_idempotent()
    {
        var once = Canonical(GoldenInput);
        var twice = Canonical(Encoding.UTF8.GetString(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Formatting_does_not_change_the_hash()
    {
        var compact = WorkflowJson.Valid(WorkflowJson.Minimal);
        var pretty = WorkflowJson.Valid(
            """
            {
              "steps": [ { "config": { }, "type": "captureResult", "id": "done" } ],
              "allowedHosts": [ "https://crm.example.test" ],
              "name": "minimal-flow",
              "schemaVersion": 1
            }
            """);

        Assert.Equal(compact.DefinitionHash, pretty.DefinitionHash);
        Assert.Equal(compact.CanonicalUtf8.ToArray(), pretty.CanonicalUtf8.ToArray());
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{"x":1}}]}""")]
    [InlineData("""{"schemaVersion":1,"name":"minimal-flox","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{}}]}""")]
    [InlineData("""{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{}}],"description":"x"}""")]
    [InlineData("""{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"a","type":"delay","config":{}},{"id":"done","type":"captureResult","config":{}}]}""")]
    [InlineData("""{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{}}],"timeoutSeconds":120}""")]
    public void Any_semantic_change_changes_the_hash(string changed)
    {
        // The last case is the documented decision: the hash covers the definition as written, so an explicit default
        // ("timeoutSeconds": 120) is a different definition from an omitted one.
        Assert.NotEqual(WorkflowJson.Valid(WorkflowJson.Minimal).DefinitionHash, WorkflowJson.Valid(changed).DefinitionHash);
    }

    [Fact]
    public void Array_order_is_significant()
    {
        Assert.NotEqual(Canonical("{\"a\":[1,2]}"), Canonical("{\"a\":[2,1]}"));
    }

    [Fact]
    public void The_hash_is_sha256_with_a_prefix_and_lowercase_hex()
    {
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", CanonicalJson.ComputeHash([]));
    }

    [Theory]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", true)]
    [InlineData("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", false)]
    [InlineData("SHA256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    [InlineData("sha512:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85", false)]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855a", false)]
    [InlineData("sha256:g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hash_format_is_strict(string? hash, bool expected)
    {
        Assert.Equal(expected, CanonicalJson.IsWellFormedHash(hash));
    }
}
