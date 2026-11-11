namespace AutomataFlow.Application.Validation;

/// <summary>Outcome of a hand-written validator: success, or a list of precise errors.</summary>
public sealed class ValidationResult
{
    private readonly List<ValidationError> _errors = [];

    public IReadOnlyList<ValidationError> Errors => _errors;

    public bool IsValid => _errors.Count == 0;

    public ValidationResult Add(string path, string code, string message)
    {
        _errors.Add(new ValidationError(path, code, message));
        return this;
    }

    public ValidationResult Merge(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _errors.AddRange(other._errors);
        return this;
    }
}
