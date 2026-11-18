using System.Text.Json;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>
/// The containment handshake protocol between a Worker and its process-death supervisor (ADR-075): the Worker sends
/// <see cref="Hello"/>; the supervisor identifies the peer by kernel credentials and answers one JSON <see cref="Reply"/>.
/// </summary>
public static class ContainmentProtocol
{
    public const string Hello = "AF-CONTAINMENT-HELLO v1\n";

    public const int MaxReplyBytes = 4096;

    /// <param name="Status"><c>ok</c> or <c>refused</c>.</param>
    public sealed record Reply(string Status, string? Reason, string? Kind, string? Unit, string? LedgerDirectory);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
