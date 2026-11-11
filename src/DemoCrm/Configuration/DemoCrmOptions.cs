namespace DemoCrm.Configuration;

/// <summary>
/// Settings for the fictional DemoCrm portal. The users below are fictional LOCAL-DEMONSTRATION credentials,
/// not secrets: DemoCrm must never be exposed beyond localhost / a private test network.
/// </summary>
public sealed class DemoCrmOptions
{
    public const string SectionName = "DemoCrm";

    public List<DemoUser> Users { get; set; } = [];

    public SimulationOptions Simulation { get; set; } = new();

    public ControlApiOptions ControlApi { get; set; } = new();
}

public sealed class DemoUser
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class SimulationOptions
{
    /// <summary>Master switch for the predictable failure scenarios triggered by e-mail plus-tags.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay applied by the <c>+slow</c> scenario.</summary>
    public int SlowDelayMs { get; set; } = 3000;
}

public sealed class ControlApiOptions
{
    /// <summary>Shared token for <c>/demo/*</c> control endpoints. Empty disables them (404).</summary>
    public string Token { get; set; } = string.Empty;
}
