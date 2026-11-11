namespace AutomataFlow.Automation.Browser;

public enum BrowserErrorCode
{
    Unknown = 0,
    Timeout,
    SessionTimeout,
    NavigationBlocked,
    ElementNotFound,
    ElementNotUnique,
    InvalidSelector,
    HttpError,
    NetworkError,
    EgressUnavailable,
    BrowserCrashed,
    SessionClosed,
    DownloadRejected,
    PolicyViolation,
    BrowserNotInstalled,
}

/// <summary>
/// Failure of a browser operation. The message is always redacted: it never contains sensitive input values,
/// credentials or URL query values, and the raw Playwright exception is deliberately not attached as an inner
/// exception because its message can echo page data (ADR-032).
/// </summary>
public sealed class BrowserOperationException : Exception
{
    public BrowserOperationException()
    {
    }

    public BrowserOperationException(string message)
        : base(message)
    {
    }

    public BrowserOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BrowserOperationException(BrowserErrorCode code, string message, bool isTransient)
        : base(message)
    {
        Code = code;
        IsTransient = isTransient;
    }

    public BrowserErrorCode Code { get; }

    /// <summary>Whether a retry of the same attempt may succeed (feeds Phase 5 retry policies).</summary>
    public bool IsTransient { get; }
}

/// <summary>Maps Playwright failures to stable error codes without leaking their raw messages.</summary>
public static class ErrorClassifier
{
    public static (BrowserErrorCode Code, bool IsTransient) Classify(Exception exception, bool isNavigation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message;

        if (exception is TimeoutException)
        {
            return isNavigation ? (BrowserErrorCode.Timeout, true) : (BrowserErrorCode.ElementNotFound, true);
        }

        if (Contains(message, "strict mode violation"))
        {
            return (BrowserErrorCode.ElementNotUnique, false);
        }

        if (Contains(message, "net::ERR_PROXY_CONNECTION_FAILED") || Contains(message, "net::ERR_PROXY_AUTH"))
        {
            return (BrowserErrorCode.EgressUnavailable, true);
        }

        if (Contains(message, "net::ERR_BLOCKED_BY_CLIENT") || Contains(message, "net::ERR_TUNNEL_CONNECTION_FAILED")
            || Contains(message, "net::ERR_NAME_NOT_RESOLVED") || Contains(message, "net::ERR_ACCESS_DENIED")
            || Contains(message, "net::ERR_UNSAFE_PORT") || Contains(message, "net::ERR_INVALID_URL"))
        {
            return (BrowserErrorCode.NavigationBlocked, false);
        }

        if (Contains(message, "net::ERR_TIMED_OUT"))
        {
            return (BrowserErrorCode.Timeout, true);
        }

        if (Contains(message, "net::ERR_"))
        {
            return (BrowserErrorCode.NetworkError, true);
        }

        if (Contains(message, "Target page, context or browser has been closed") || Contains(message, "Target crashed"))
        {
            return (BrowserErrorCode.SessionClosed, true);
        }

        if (Contains(message, "Unexpected token") || Contains(message, "is not a valid selector") || Contains(message, "Unknown engine"))
        {
            return (BrowserErrorCode.InvalidSelector, false);
        }

        return (BrowserErrorCode.Unknown, false);
    }

    /// <summary>The <c>net::ERR_*</c> token of a Playwright message, which is safe to log.</summary>
    public static string? NetworkErrorToken(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var start = message.IndexOf("net::ERR_", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < message.Length && (char.IsAsciiLetterOrDigit(message[end]) || message[end] is '_' or ':'))
        {
            end++;
        }

        return message[start..end];
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.Ordinal);
}
