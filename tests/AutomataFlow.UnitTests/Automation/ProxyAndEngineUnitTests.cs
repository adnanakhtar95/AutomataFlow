using System.Text;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Network;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Automation;

public class ProxyRequestParserTests
{
    private static bool Parse(string head, out ProxyRequest? request, out string? error) =>
        ProxyRequestParser.TryParse(Encoding.ASCII.GetBytes(head), out request, out error);

    [Fact]
    public void Connect_and_absolute_form_requests_are_parsed()
    {
        Assert.True(Parse("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nProxy-Authorization: Basic eDp5", out var connect, out _));
        Assert.True(connect!.IsConnect);
        Assert.Equal("Basic eDp5", connect.GetHeader("proxy-authorization"));

        Assert.True(Parse("GET http://example.com/a?b=1 HTTP/1.1\r\nHost: example.com", out var get, out _));
        Assert.Equal(("GET", "http://example.com/a?b=1"), (get!.Method, get.Target));
    }

    [Theory]
    [InlineData("GET http://x/ HTTP/1.1 extra\r\nHost: x", "malformed-request-line")]
    [InlineData("GET  http://x/ HTTP/1.1\r\nHost: x", "malformed-request-line")]
    [InlineData("TRACE http://x/ HTTP/1.1\r\nHost: x", "method-not-allowed")]
    [InlineData("GET http://x/ HTTP/2.0\r\nHost: x", "unsupported-version")]
    [InlineData("GET http://x/ HTTP/1.1\r\n Host: x", "obsolete-line-folding")]
    [InlineData("GET http://x/ HTTP/1.1\r\nHo st: x", "malformed-header-name")]
    [InlineData("GET http://x/ HTTP/1.1\r\nNoColonHere", "malformed-header")]
    [InlineData("GET http://x/ HTTP/1.1\r\nHost: x\nInjected: y", "bare-cr-or-lf")]
    [InlineData("GET http://x/ HTTP/1.1\r\nHost: x\u0000", "invalid-byte-in-head")]
    public void Ambiguous_requests_are_rejected(string head, string expectedError)
    {
        Assert.False(Parse(head, out _, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Too_many_headers_are_rejected()
    {
        var head = "GET http://x/ HTTP/1.1\r\n" + string.Join("\r\n", Enumerable.Range(0, 101).Select(i => $"X-{i}: v"));

        Assert.False(Parse(head, out _, out var error));
        Assert.Equal("too-many-headers", error);
    }

    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("[::1]:8443", "[::1]", 8443)]
    [InlineData("10.0.0.1:80", "10.0.0.1", 80)]
    public void Authorities_are_parsed(string authority, string host, int port)
    {
        Assert.True(ProxyRequestParser.TryParseAuthority(authority, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("example.com:")]
    [InlineData("example.com:99999")]
    [InlineData("example.com:+80")]
    [InlineData("a:b:443")]
    [InlineData("[::1]")]
    public void Invalid_authorities_are_rejected(string authority)
    {
        Assert.False(ProxyRequestParser.TryParseAuthority(authority, out _, out _));
    }
}

public class EgressSessionRegistryTests
{
    [Fact]
    public void Sessions_get_unique_random_credentials_and_authenticate_only_with_them()
    {
        var registry = new EgressSessionRegistry();
        using var a = registry.Register(Guid.NewGuid(), EgressPolicy.Create("https://a.test"));
        using var b = registry.Register(Guid.NewGuid(), EgressPolicy.Create("https://b.test"));

        Assert.NotEqual(a.Username, b.Username);
        Assert.NotEqual(a.Password, b.Password);
        Assert.True(a.Password.Length >= 32);
        Assert.Same(a, registry.Authenticate(a.Username, a.Password));
        Assert.Null(registry.Authenticate(a.Username, b.Password));
        Assert.Null(registry.Authenticate("unknown", a.Password));
        Assert.Null(registry.Authenticate(null, null));
    }

    [Fact]
    public void Revoked_sessions_can_no_longer_authenticate()
    {
        var registry = new EgressSessionRegistry();
        var session = registry.Register(Guid.NewGuid(), EgressPolicy.Create("https://a.test"));

        session.Dispose();

        Assert.True(session.IsRevoked);
        Assert.Null(registry.Authenticate(session.Username, session.Password));
        Assert.Equal(0, registry.Count);
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("https://user:secret@example.com/path?token=abc123&q=1#frag", "https://example.com/path?token=***&q=***")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("https://example.com/?a", "https://example.com/?a=***")]
    [InlineData("data:text/html,<p>secret</p>", "data:[redacted]")]
    [InlineData("javascript:alert('secret')", "javascript:[redacted]")]
    [InlineData("not a url", "[unparseable-url]")]
    public void Urls_lose_credentials_query_values_and_fragments(string url, string expected)
    {
        Assert.Equal(expected, LogRedactor.RedactUrl(url));
    }

    [Fact]
    public void Known_sensitive_values_are_masked_longest_first()
    {
        var text = LogRedactor.RedactValues("user pass=Hunter2! and Hunter2!xyz", ["Hunter2!", "Hunter2!xyz"]);

        Assert.Equal("user pass=*** and ***", text);
    }

    [Theory]
    [InlineData("report.csv", "report.csv")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
    [InlineData("a<b>c:d|e?.txt", "a_b_c_d_e_.txt")]
    [InlineData("_.._.._etc_cron.d_evil.csv", "_._._etc_cron.d_evil.csv")]
    [InlineData("CON.txt", "file")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    public void File_names_are_sanitized(string? input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var name = FileNameSanitizer.Sanitize(new string('x', 300) + ".pdf");

        Assert.Equal(120, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }
}

public class ElementSelectorTests
{
    [Fact]
    public void Typed_selectors_are_created()
    {
        Assert.Equal(SelectorKind.TestId, ElementSelector.TestId("login-submit").Kind);
        Assert.Equal("input[type=password]", ElementSelector.Css("input[type=password]").Value);
        Assert.Equal("Sign in", ElementSelector.Role("button", "Sign in").AccessibleName);
        Assert.Equal(SelectorKind.XPath, ElementSelector.XPath("//button[@id='x']").Kind);
    }

    [Theory]
    [InlineData(SelectorKind.Css, "text=Sign in")]
    [InlineData(SelectorKind.Css, "_react=App")]
    [InlineData(SelectorKind.Css, "internal:role=button")]
    [InlineData(SelectorKind.Css, "div >> text=x")]
    [InlineData(SelectorKind.XPath, "div")]
    [InlineData(SelectorKind.XPath, "//a >> css=b")]
    [InlineData(SelectorKind.Role, "button[name]")]
    [InlineData(SelectorKind.TestId, "a\nb")]
    public void Engine_switching_and_malformed_selectors_are_rejected(SelectorKind kind, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => ElementSelector.Create(kind, value, null));
    }

    [Fact]
    public void Over_long_selectors_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => ElementSelector.Css(new string('a', 501)));
    }

    [Fact]
    public void Sensitive_input_values_never_appear_in_their_text_form()
    {
        var value = InputValue.Sensitive("Hunter2!");

        Assert.DoesNotContain("Hunter2!", value.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2!", InputValue.Plain("Hunter2!").ToString(), StringComparison.Ordinal);
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED at http://x/", BrowserErrorCode.EgressUnavailable, true)]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED at https://x/", BrowserErrorCode.NavigationBlocked, false)]
    [InlineData("net::ERR_BLOCKED_BY_CLIENT at http://x/", BrowserErrorCode.NavigationBlocked, false)]
    [InlineData("net::ERR_CONNECTION_RESET", BrowserErrorCode.NetworkError, true)]
    [InlineData("net::ERR_TIMED_OUT", BrowserErrorCode.Timeout, true)]
    [InlineData("Error: strict mode violation: Locator resolved to 2 elements", BrowserErrorCode.ElementNotUnique, false)]
    [InlineData("Target page, context or browser has been closed", BrowserErrorCode.SessionClosed, true)]
    [InlineData("something else entirely", BrowserErrorCode.Unknown, false)]
    public void Playwright_messages_map_to_stable_codes(string message, BrowserErrorCode code, bool transient)
    {
        Assert.Equal((code, transient), ErrorClassifier.Classify(new Microsoft.Playwright.PlaywrightException(message), isNavigation: true));
    }

    [Fact]
    public void Timeouts_are_navigation_timeouts_or_missing_elements()
    {
        Assert.Equal((BrowserErrorCode.Timeout, true), ErrorClassifier.Classify(new TimeoutException("Timeout 30000ms exceeded."), isNavigation: true));
        Assert.Equal((BrowserErrorCode.ElementNotFound, true), ErrorClassifier.Classify(new TimeoutException("waiting for locator"), isNavigation: false));
    }

    [Fact]
    public void Only_the_net_error_token_is_extracted_from_messages()
    {
        Assert.Equal("net::ERR_TUNNEL_CONNECTION_FAILED", ErrorClassifier.NetworkErrorToken("page.goto: net::ERR_TUNNEL_CONNECTION_FAILED at https://x/?token=secret"));
        Assert.Null(ErrorClassifier.NetworkErrorToken("no network error"));
    }
}

public class BrowserEngineOptionsValidatorTests
{
    private static readonly BrowserEngineOptionsValidator Validator = new();

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validator.Validate(null, new BrowserEngineOptions()).Succeeded);
    }

    [Fact]
    public void Hosted_multi_tenant_requires_a_browser_per_session()
    {
        var result = Validator.Validate(null, new BrowserEngineOptions { Mode = ExecutionMode.HostedMultiTenant });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("BrowserPerSession", StringComparison.Ordinal));
        Assert.True(Validator.Validate(null, new BrowserEngineOptions { Mode = ExecutionMode.HostedMultiTenant, Isolation = BrowserIsolation.BrowserPerSession }).Succeeded);
    }

    [Theory]
    [InlineData(ExecutionMode.SelfHosted)]
    [InlineData(ExecutionMode.HostedMultiTenant)]
    public void Sandbox_and_headless_can_only_be_relaxed_in_development(ExecutionMode mode)
    {
        Assert.True(Validator.Validate(null, new BrowserEngineOptions { Mode = mode, Isolation = BrowserIsolation.BrowserPerSession, ChromiumSandbox = false }).Failed);
        Assert.True(Validator.Validate(null, new BrowserEngineOptions { Mode = mode, Isolation = BrowserIsolation.BrowserPerSession, Headless = false }).Failed);
        Assert.True(Validator.Validate(null, new BrowserEngineOptions { Mode = ExecutionMode.Development, ChromiumSandbox = false, Headless = false }).Succeeded);
    }

    [Fact]
    public void Out_of_range_values_are_rejected()
    {
        var options = new BrowserEngineOptions { MaxConcurrentSessions = 0, MaxPagesPerSession = 99 };
        options.Timeouts.NavigationSeconds = 0;
        options.Downloads.MaxBytes = 0;

        var result = Validator.Validate(null, options);

        Assert.Equal(4, result.Failures!.Count());
    }
}

public class ChromiumLaunchArgumentsTests
{
    [Fact]
    public void Chromium_can_only_resolve_and_reach_the_local_proxy()
    {
        Assert.Contains("--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", ChromiumLaunchArguments.Build());
        Assert.Equal("<-loopback>", ChromiumLaunchArguments.ProxyLoopbackRule);
    }

    [Fact]
    public void Non_proxied_protocols_and_background_traffic_are_disabled()
    {
        var args = ChromiumLaunchArguments.Build();

        Assert.Contains("--disable-quic", args);
        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args);
        Assert.Contains("--disable-background-networking", args);
        Assert.Contains("--disable-extensions", args);
        Assert.DoesNotContain(args, a => a.Contains("no-sandbox", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server", StringComparison.Ordinal));
    }
}
