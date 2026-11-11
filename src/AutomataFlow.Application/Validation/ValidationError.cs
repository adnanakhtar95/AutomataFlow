namespace AutomataFlow.Application.Validation;

/// <param name="Path">Location of the problem, e.g. a JSON path such as <c>steps[2].config.selector</c>.</param>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Human-readable explanation. Must not contain secrets.</param>
public sealed record ValidationError(string Path, string Code, string Message);
