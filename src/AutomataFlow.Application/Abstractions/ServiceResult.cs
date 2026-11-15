namespace AutomataFlow.Application.Abstractions;

public enum ServiceStatus
{
    Ok,

    /// <summary>Missing OR owned by another organization: the two are indistinguishable to the caller (§7.1 layer 2).</summary>
    NotFound,
    Invalid,
    Conflict,
    Forbidden,
}

/// <summary>A problem detail without values: a JSON path or field, a stable code and a message.</summary>
public sealed record ServiceError(string? Path, string Code, string Message);

#pragma warning disable CA1000 // Factory methods on the result type are its intended API (ServiceResult<T>.Ok(value)).

/// <summary>The outcome of an application use case; controllers map it to HTTP without further logic.</summary>
public sealed record ServiceResult<T>(ServiceStatus Status, T? Value, IReadOnlyList<ServiceError> Errors)
{
    public static ServiceResult<T> Ok(T value) => new(ServiceStatus.Ok, value, []);

    public static ServiceResult<T> Fail(ServiceStatus status, params ServiceError[] errors) => new(status, default, errors);

    public static ServiceResult<T> NotFound() => new(ServiceStatus.NotFound, default, []);
}
#pragma warning restore CA1000
