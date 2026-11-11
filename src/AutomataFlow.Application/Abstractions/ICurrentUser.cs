namespace AutomataFlow.Application.Abstractions;

/// <summary>
/// The authenticated caller and the organization they are acting in.
/// Application services must take the organization from here and pass it explicitly to every
/// data access; it is never read from client-supplied request bodies.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    Guid OrganizationId { get; }
}
