using System.Collections.Immutable;
using AutomataFlow.Application.Validation;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Collects validation errors and warnings with a hard bound (ADR-061): after
/// <see cref="WorkflowLimits.MaxValidationErrors"/> errors, one final <c>too-many-errors</c> entry is added and further
/// errors are dropped, so a hostile definition cannot make the error list grow with its size.
/// </summary>
internal sealed class DiagnosticBag
{
    private readonly List<ValidationError> _errors = [];
    private readonly List<ValidationError> _warnings = [];
    private bool _truncated;

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<ValidationError> Errors => _errors;

    public ImmutableArray<ValidationError> Warnings => [.. _warnings];

    public void Add(string path, string code, string message)
    {
        if (_errors.Count < WorkflowLimits.MaxValidationErrors)
        {
            _errors.Add(new ValidationError(path, code, message));
        }
        else if (!_truncated)
        {
            _truncated = true;
            _errors.Add(new ValidationError(DiagnosticPath.Root, WorkflowErrorCodes.TooManyErrors,
                $"More than {WorkflowLimits.MaxValidationErrors} errors; further errors are not reported."));
        }
    }

    public void Warn(string path, string code, string message)
    {
        if (_warnings.Count < WorkflowLimits.MaxValidationErrors)
        {
            _warnings.Add(new ValidationError(path, code, message));
        }
    }
}
