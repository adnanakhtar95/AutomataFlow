namespace AutomataFlow.Api.Auth;

/// <summary>
/// Browser sessions (ADR-077). The dashboard asks for this transport with the <see cref="HeaderName"/> header; the refresh
/// token is then never in a response body or readable by page script. It lives only in an HttpOnly, Secure,
/// SameSite=Strict cookie scoped to the auth endpoints. The cookie is honoured only together with the custom header,
/// which a cross-site form or image request cannot add (the Api has no CORS, so a cross-origin script cannot either).
/// </summary>
public static class BrowserSession
{
    public const string HeaderName = "X-AutomataFlow-Session";

    public const string HeaderValue = "browser";

    /// <summary>The <c>__Secure-</c> prefix makes browsers refuse the cookie unless it is Secure.</summary>
    public const string CookieName = "__Secure-af_refresh";

    public const string CookiePath = "/api/v1/auth";

    public static bool IsRequested(HttpRequest request) =>
        string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal);

    public static string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) && value.Length is > 0 and <= 100 ? value : null;

    public static void Write(HttpResponse response, string refreshToken, DateTimeOffset expiresAt) =>
        response.Cookies.Append(CookieName, refreshToken, Options(expiresAt));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(CookieName, Options(null));

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expiresAt,
        IsEssential = true,
    };
}
