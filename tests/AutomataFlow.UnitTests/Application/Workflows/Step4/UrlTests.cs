using System.Text.Json.Nodes;
using AutomataFlow.Application.Network;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step4;

/// <summary>Phase 4 step 4: canonical origins, the URL template grammar, encoding and final validation (§6, U1–U7).</summary>
public class UrlTests
{
    private static readonly WorkflowDefinition Declarations = Valid(With(Full, r =>
    {
        r["parameters"] = new JsonArray(new JsonObject { ["name"] = "crmBaseUrl", ["type"] = "url" }, new JsonObject { ["name"] = "region", ["type"] = "string" });
        r["inputs"] = new JsonArray(
            new JsonObject { ["name"] = "email", ["type"] = "email" },
            new JsonObject { ["name"] = "term", ["type"] = "string" },
            new JsonObject { ["name"] = "segment", ["type"] = "string" });
    })).Definition;

    private sealed class Values(Dictionary<string, string?> record) : ITemplateValues
    {
        public Guid OperationId => Guid.Empty;

        public bool TryGetRecordValue(string name, out string? value) => record.TryGetValue(name, out value);

        public bool TryGetParameter(string name, out string value)
        {
            value = "eu";
            return name == "region";
        }

        public bool TryGetSecret(string name, out string value) => throw new InvalidOperationException("no secrets in URLs");

        public bool TryGetStepOutput(string stepId, string output, out string value, out bool isSensitive) => throw new InvalidOperationException();
    }

    private static OriginParts Origin(string value) => CanonicalOrigin.TryCanonicalize(value, false, out _)!;

    private static UrlTemplate Url(string text)
    {
        var template = UrlTemplate.Parse(text, out var code, out var message);
        Assert.True(template is not null, $"{code}: {message}");
        return template!;
    }

    private static UrlRenderResult Render(string template, string origin, params (string Name, string? Value)[] record) =>
        Url(template).Render(Origin(origin), new Values(record.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal)), TemplateResolver.InputLookup(Declarations));

    // ----- Canonical origins -----

    [Theory]
    [InlineData("https://crm.example.test")]
    [InlineData("https://crm.example.test:8443")]
    [InlineData("http://crm.example.test")]
    [InlineData("https://xn--bcher-kva.example")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://203.0.113.10:8443")]
    public void Canonical_origins_are_accepted_as_written(string origin)
    {
        Assert.NotNull(CanonicalOrigin.TryParseCanonical(origin, allowWildcard: false, out var error));
        Assert.Null(error);
    }

    /// <summary>U1: a literal origin is never rewritten silently.</summary>
    [Theory]
    [InlineData("https://CRM.example.test")]
    [InlineData("HTTPS://crm.example.test")]
    [InlineData("https://crm.example.test:443")]
    [InlineData("http://crm.example.test:80")]
    [InlineData("https://crm.example.test.")]
    [InlineData("https://crm.example.test/")]
    [InlineData("https://crm.example.test/path")]
    [InlineData("https://bücher.example")]
    [InlineData("https://crm.example.test:08443")]
    public void Non_canonical_literal_origins_are_rejected(string origin)
    {
        Assert.Null(CanonicalOrigin.TryParseCanonical(origin, allowWildcard: false, out _));
    }

    /// <summary>U2: url parameters are canonicalized at bind time.</summary>
    [Theory]
    [InlineData("https://CRM.test:443/", "https://crm.test")]
    [InlineData("HTTPS://crm.test", "https://crm.test")]
    [InlineData("https://crm.test.:8443", "https://crm.test:8443")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("http://127.0.0.1:5080/", "http://127.0.0.1:5080")]
    [InlineData("https://[2001:DB8::1]:443", "https://[2001:db8::1]")]
    public void Origins_canonicalize(string value, string expected)
    {
        Assert.Equal(expected, CanonicalOrigin.TryCanonicalize(value, allowWildcard: false, out _)!.ToString());
    }

    [Theory]
    [InlineData("https://crm.test/path")]
    [InlineData("https://crm.test//")]
    [InlineData("https://crm.test?x=1")]
    [InlineData("https://crm.test#f")]
    [InlineData("https://user@crm.test")]
    [InlineData("https://user:pw@crm.test")]
    [InlineData("ftp://crm.test")]
    [InlineData("javascript://crm.test")]
    [InlineData("crm.test")]
    [InlineData("https://crm.test:0")]
    [InlineData("https://crm.test:65536")]
    [InlineData("https://crm.test:")]
    [InlineData("https://crm .test")]
    [InlineData("https://crm.test\\@evil.test")]
    [InlineData("https://2130706433")]
    [InlineData("https://0x7f.1")]
    [InlineData("https://::1")]
    [InlineData("https://*.crm.test")]
    public void Invalid_origins_are_rejected(string value)
    {
        Assert.Null(CanonicalOrigin.TryCanonicalize(value, allowWildcard: false, out _));
    }

    // ----- Grammar -----

    [Theory]
    [InlineData("https://crm.example.test")]
    [InlineData("https://crm.example.test/")]
    [InlineData("https://crm.example.test/Customers/Search?Term={{record.term}}&Page=1")]
    [InlineData("{{params.crmBaseUrl}}/Customers/{{record.segment}}/Edit")]
    [InlineData("{{params.crmBaseUrl}}")]
    [InlineData("{{params.crmBaseUrl}}?Term=")]
    [InlineData("https://crm.example.test/a/b/")]
    [InlineData("https://crm.example.test/id-{{record.segment}}")]
    [InlineData("https://crm.example.test/~user/a;b=c/@x:y")]
    public void Valid_url_templates_parse(string text)
    {
        Url(text);
    }

    [Theory]
    [InlineData("https://{{record.segment}}.example.test/", UrlErrorCodes.OriginPlaceholder)]
    [InlineData("https://crm.example.test:{{params.region}}/", UrlErrorCodes.OriginPlaceholder)]
    [InlineData("{{record.segment}}/x", UrlErrorCodes.OriginPlaceholder)]
    [InlineData("{{params.crmBaseUrl}}.evil.test/x", UrlErrorCodes.OriginPlaceholder)]
    [InlineData("{{params.crmBaseUrl}}@evil.test/x", UrlErrorCodes.OriginPlaceholder)]
    [InlineData("https://crm.example.test/a?{{record.term}}=1", UrlErrorCodes.InvalidQuery)]
    [InlineData("https://crm.example.test/a?q", UrlErrorCodes.InvalidQuery)]
    [InlineData("https://crm.example.test/a?", UrlErrorCodes.InvalidQuery)]
    [InlineData("https://crm.example.test/a#top", UrlErrorCodes.Fragment)]
    [InlineData("https://crm.example.test/a#{{record.term}}", UrlErrorCodes.Fragment)]
    [InlineData("https://CRM.example.test/a", UrlErrorCodes.InvalidOrigin)]
    [InlineData("https://crm.example.test:443/a", UrlErrorCodes.InvalidOrigin)]
    [InlineData("https://user@crm.example.test/a", UrlErrorCodes.InvalidOrigin)]
    [InlineData("crm.example.test/a", UrlErrorCodes.InvalidOrigin)]
    [InlineData("https://crm.example.test/a\\b", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/a b", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/a\tb", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/café", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/a%2fb", UrlErrorCodes.ForbiddenEncoding)]
    [InlineData("https://crm.example.test/a%5Cb", UrlErrorCodes.ForbiddenEncoding)]
    [InlineData("https://crm.example.test/%2e%2e/admin", UrlErrorCodes.ForbiddenEncoding)]
    [InlineData("https://crm.example.test/a%0Ab", UrlErrorCodes.ForbiddenEncoding)]
    [InlineData("https://crm.example.test/a%zz", UrlErrorCodes.InvalidPercentEncoding)]
    [InlineData("https://crm.example.test/../admin", UrlErrorCodes.DotSegment)]
    [InlineData("https://crm.example.test/a/./b", UrlErrorCodes.DotSegment)]
    [InlineData("https://crm.example.test/a//b", UrlErrorCodes.EmptySegment)]
    [InlineData("https://crm.example.test/a?x=a+b", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/a?x=1=2", UrlErrorCodes.InvalidCharacter)]
    [InlineData("https://crm.example.test/{{record.term", TemplateErrorCodes.Unterminated)]
    public void Url_templates_outside_the_grammar_are_rejected(string text, string code)
    {
        Assert.Null(UrlTemplate.Parse(text, out var actual, out _));
        Assert.Equal(code, actual);
    }

    [Fact]
    public void Url_templates_have_a_length_limit()
    {
        var path = "/" + new string('a', WorkflowLimits.MaxUrlTemplateChars - "https://crm.example.test/".Length);
        Url("https://crm.example.test" + path);
        Assert.Null(UrlTemplate.Parse("https://crm.example.test" + path + "a", out var code, out _));
        Assert.Equal(UrlErrorCodes.TooLong, code);
    }

    // ----- Rendering and encoding -----

    [Fact]
    public void U2_a_bound_url_parameter_is_substituted_in_canonical_form()
    {
        var rendered = Render("{{params.crmBaseUrl}}/Login", "https://CRM.test:443/");

        Assert.Equal("https://crm.test/Login", rendered.Url);
    }

    [Fact]
    public void An_empty_path_renders_as_the_root()
    {
        Assert.Equal("https://crm.test/", Render("{{params.crmBaseUrl}}", "https://crm.test").Url);
        Assert.Equal("https://crm.test/?Term=", Render("{{params.crmBaseUrl}}?Term={{record.term}}", "https://crm.test").Url);
    }

    [Theory]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a#b", "a%23b")]
    [InlineData("a%b", "a%25b")]
    [InlineData("%25", "%2525")]
    [InlineData("a\\b", "a%5Cb")]
    [InlineData("a@b:c", "a%40b%3Ac")]
    [InlineData("a b", "a%20b")]
    [InlineData("&=+", "%26%3D%2B")]
    [InlineData("café", "caf%C3%A9")]
    public void Path_values_are_encoded_once_and_cannot_change_structure(string value, string encoded)
    {
        Assert.Equal("https://crm.test/c/" + encoded + "/e", Render("{{params.crmBaseUrl}}/c/{{record.segment}}/e", "https://crm.test", ("segment", value)).Url);
    }

    [Fact]
    public void U7_plus_and_at_in_query_values_are_encoded()
    {
        var url = Render("{{params.crmBaseUrl}}/Customers/Search?Term={{record.email}}", "https://crm.test", ("email", "name+commit-fail@example.test")).Url;

        Assert.Equal("https://crm.test/Customers/Search?Term=name%2Bcommit-fail%40example.test", url);
    }

    [Theory]
    [InlineData("a&admin=true")]
    [InlineData("x=1")]
    [InlineData("a#frag")]
    public void Query_values_cannot_add_parameters_or_fragments(string value)
    {
        var url = Render("{{params.crmBaseUrl}}/s?q={{record.term}}", "https://crm.test", ("term", value)).Url!;

        Assert.Single(new Uri(url).Query.TrimStart('?').Split('&'));
        Assert.Empty(new Uri(url).Fragment);
    }

    /// <summary>U4 and dot segments: placeholder segments resolving to empty, '.' or '..' (in any spelling) are unsafe.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("%2e")]
    [InlineData("%2E%2e")]
    [InlineData(".%2E")]
    public void Unsafe_path_values_are_rejected(string? value)
    {
        Assert.Equal(EngineErrorCodes.UnsafeUrlValue, Render("{{params.crmBaseUrl}}/a/{{record.segment}}/b", "https://crm.test", ("segment", value)).ErrorCode);
    }

    [Fact]
    public void A_literal_prefix_does_not_make_a_dot_segment_safe()
    {
        Assert.Equal(EngineErrorCodes.UnsafeUrlValue, Render("{{params.crmBaseUrl}}/.{{record.segment}}", "https://crm.test", ("segment", ".")).ErrorCode);
        Assert.Equal("https://crm.test/v.1", Render("{{params.crmBaseUrl}}/v{{record.segment}}", "https://crm.test", ("segment", ".1")).Url);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\u007fb")]
    public void Control_characters_and_lone_surrogates_are_unsafe(string value)
    {
        Assert.Equal(EngineErrorCodes.UnsafeUrlValue, Render("{{params.crmBaseUrl}}/s?q={{record.term}}", "https://crm.test", ("term", value)).ErrorCode);
    }

    /// <summary>Built in code: xUnit serializes [InlineData] strings and would replace lone surrogates with U+FFFD.</summary>
    [Fact]
    public void Lone_surrogates_are_unsafe()
    {
        Assert.Equal(EngineErrorCodes.UnsafeUrlValue, Render("{{params.crmBaseUrl}}/s?q={{record.term}}", "https://crm.test", ("term", "a" + (char)0xD800 + "b")).ErrorCode);
    }

    [Fact]
    public void A_literal_origin_template_refuses_a_different_bound_origin()
    {
        Assert.Equal(EngineErrorCodes.UnsafeUrlValue, Render("https://crm.test/a", "https://evil.test").ErrorCode);
    }

    // ----- Final validation (§6.1 steps 1–6) -----

    private static readonly EgressPolicy Policy = EgressPolicy.Create("https://crm.test");

    private static string? FinalError(string url, string template = "{{params.crmBaseUrl}}/Customers/Search?Term={{record.term}}", string origin = "https://crm.test")
    {
        FinalUrlValidator.Validate(url, Url(template), Origin(origin), Policy, out var error);
        return error;
    }

    [Fact]
    public void A_rendered_url_passes_final_validation()
    {
        var url = Render("{{params.crmBaseUrl}}/Customers/Search?Term={{record.term}}", "https://crm.test", ("term", "a b")).Url!;

        Assert.Null(FinalError(url));
    }

    [Theory]
    [InlineData("https://crm.test/Customers/a/../Search?Term=x", "round-trip")]
    [InlineData("HTTPS://crm.test/Customers/Search?Term=x", "round-trip")]
    [InlineData("https://evil.test/Customers/Search?Term=x", "origin-mismatch")]
    [InlineData("https://crm.test:8443/Customers/Search?Term=x", "origin-mismatch")]
    [InlineData("http://crm.test/Customers/Search?Term=x", "origin-mismatch")]
    [InlineData("https://u@crm.test/Customers/Search?Term=x", "userinfo-or-fragment")]
    [InlineData("https://crm.test/Customers/Search?Term=x#f", "userinfo-or-fragment")]
    [InlineData("https://crm.test/Customers/SearchAdmin?Term=x", "path-prefix")]
    [InlineData("https://crm.test/Admin/Search?Term=x", "path-prefix")]
    [InlineData("https://crm.test/Customers", "path-prefix")]
    public void Final_validation_rejects_anything_but_the_exact_expected_shape(string url, string error)
    {
        Assert.Equal(error, FinalError(url));
    }

    [Fact]
    public void U6_the_first_placeholder_segment_must_keep_its_literal_prefix()
    {
        Assert.Null(FinalError("https://crm.test/c/id-7", "{{params.crmBaseUrl}}/c/id-{{record.segment}}"));
        Assert.Equal("path-prefix", FinalError("https://crm.test/c/xd-7", "{{params.crmBaseUrl}}/c/id-{{record.segment}}"));
    }

    [Fact]
    public void Final_urls_have_a_length_limit_and_must_be_allowed()
    {
        var longUrl = "https://crm.test/Customers/Search?Term=" + new string('a', WorkflowLimits.MaxFinalUrlChars);
        Assert.Equal("too-long", FinalError(longUrl));

        FinalUrlValidator.Validate("https://other.test/Customers/Search?Term=x", Url("{{params.crmBaseUrl}}/Customers/Search?Term={{record.term}}"), Origin("https://other.test"), Policy, out var error);
        Assert.Equal("not-allowed-host", error);
    }
}
