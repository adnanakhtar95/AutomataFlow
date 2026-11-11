using Microsoft.Extensions.Options;

namespace AutomataFlow.Automation.Browser;

public enum ExecutionMode
{
    /// <summary>Local development and tests. Loopback/private exceptions for explicit host:port rules are allowed.</summary>
    Development = 0,

    /// <summary>A single trusted customer's environment. Policy enforcement via the in-process egress proxy.</summary>
    SelfHosted = 1,

    /// <summary>Untrusted multi-tenant hosting. Requires browser-per-session, sandbox, https-only and no private exceptions.</summary>
    HostedMultiTenant = 2,
}

public enum BrowserIsolation
{
    /// <summary>One Chromium process per Worker, a fresh context per session. Contexts isolate state, not exploits.</summary>
    SharedBrowser = 0,

    /// <summary>A dedicated Chromium process per session.</summary>
    BrowserPerSession = 1,
}

public enum ScreenshotPolicy
{
    Never = 0,
    OnFailure = 1,
    OnRequest = 2,
}

public sealed class BrowserTimeouts
{
    public int LaunchSeconds { get; set; } = 30;

    public int NavigationSeconds { get; set; } = 30;

    public int ActionSeconds { get; set; } = 10;

    public int DownloadSeconds { get; set; } = 60;

    public int CloseSeconds { get; set; } = 10;

    public int SessionSeconds { get; set; } = 300;
}

public sealed class DownloadLimits
{
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;

    public int MaxCount { get; set; } = 10;

    /// <summary>Extensions that are never stored (case-insensitive, with leading dot).</summary>
    public List<string> BlockedExtensions { get; set; } =
    [
        ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".jse", ".msi", ".msp", ".scr",
        ".jar", ".sh", ".app", ".dmg", ".pkg", ".deb", ".rpm", ".apk", ".hta", ".lnk", ".reg", ".cpl",
    ];
}

/// <summary>Browser engine configuration (section <c>Automation:Browser</c>), validated at startup.</summary>
public sealed class BrowserEngineOptions
{
    public const string SectionName = "Automation:Browser";

    public ExecutionMode Mode { get; set; } = ExecutionMode.SelfHosted;

    public BrowserIsolation Isolation { get; set; } = BrowserIsolation.SharedBrowser;

    public int MaxConcurrentSessions { get; set; } = 4;

    public bool Headless { get; set; } = true;

    /// <summary>Chromium sandbox. Requires the seccomp profile from ADR-035 in containers. Only Development may disable it.</summary>
    public bool ChromiumSandbox { get; set; } = true;

    public int RecycleAfterSessions { get; set; } = 50;

    public int RecycleAfterMinutes { get; set; } = 30;

    public int MaxPagesPerSession { get; set; } = 5;

    public ScreenshotPolicy Screenshots { get; set; } = ScreenshotPolicy.OnFailure;

    public long MaxScreenshotBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>Extra selectors (CSS) that are always masked in screenshots, in addition to the built-in ones.</summary>
    public List<string> ExtraMaskSelectors { get; set; } = [];

    public int ViewportWidth { get; set; } = 1280;

    public int ViewportHeight { get; set; } = 800;

    public string Locale { get; set; } = "en-US";

    public string TimeZone { get; set; } = "UTC";

    public BrowserTimeouts Timeouts { get; set; } = new();

    public DownloadLimits Downloads { get; set; } = new();
}

/// <summary>Hand-written validation (ADR-016). Security-relevant combinations are rejected, not warned about.</summary>
public sealed class BrowserEngineOptionsValidator : IValidateOptions<BrowserEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        void Range(string field, long value, long min, long max)
        {
            if (value < min || value > max)
            {
                errors.Add($"{field} must be between {min} and {max} (was {value}).");
            }
        }

        if (!Enum.IsDefined(options.Mode))
        {
            errors.Add("Mode is not a known execution mode.");
        }

        if (!Enum.IsDefined(options.Isolation))
        {
            errors.Add("Isolation is not a known isolation mode.");
        }

        Range(nameof(options.MaxConcurrentSessions), options.MaxConcurrentSessions, 1, 64);
        Range(nameof(options.RecycleAfterSessions), options.RecycleAfterSessions, 1, 10_000);
        Range(nameof(options.RecycleAfterMinutes), options.RecycleAfterMinutes, 1, 24 * 60);
        Range(nameof(options.MaxPagesPerSession), options.MaxPagesPerSession, 1, 20);
        Range(nameof(options.MaxScreenshotBytes), options.MaxScreenshotBytes, 64 * 1024, 50L * 1024 * 1024);
        Range(nameof(options.ViewportWidth), options.ViewportWidth, 320, 3840);
        Range(nameof(options.ViewportHeight), options.ViewportHeight, 240, 2160);

        var t = options.Timeouts ?? new BrowserTimeouts();
        Range("Timeouts.LaunchSeconds", t.LaunchSeconds, 1, 120);
        Range("Timeouts.NavigationSeconds", t.NavigationSeconds, 1, 300);
        Range("Timeouts.ActionSeconds", t.ActionSeconds, 1, 120);
        Range("Timeouts.DownloadSeconds", t.DownloadSeconds, 1, 600);
        Range("Timeouts.CloseSeconds", t.CloseSeconds, 1, 60);
        Range("Timeouts.SessionSeconds", t.SessionSeconds, 1, 3600);

        var d = options.Downloads ?? new DownloadLimits();
        Range("Downloads.MaxBytes", d.MaxBytes, 1, 1024L * 1024 * 1024);
        Range("Downloads.MaxCount", d.MaxCount, 0, 100);

        if (options.Mode != ExecutionMode.Development)
        {
            if (!options.Headless)
            {
                errors.Add("Headless must be true outside Development.");
            }

            if (!options.ChromiumSandbox)
            {
                errors.Add("ChromiumSandbox may only be disabled in Development (ADR-035).");
            }
        }

        if (options.Mode == ExecutionMode.HostedMultiTenant)
        {
            if (options.Isolation != BrowserIsolation.BrowserPerSession)
            {
                errors.Add("HostedMultiTenant requires Isolation = BrowserPerSession (ADR-029).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
