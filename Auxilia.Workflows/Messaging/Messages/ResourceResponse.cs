namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Result of one <see cref="ResourceRequest"/>, delivered on the instance's canonical
/// response queue and correlated by <see cref="RequestId"/>.
/// </summary>
public sealed record ResourceResponse(
    Guid RequestId,
    bool Success,
    string? ErrorMessage,
    string? ResultJson);
