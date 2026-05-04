namespace Auxilia.Messaging.Messages;

/// <summary>
///     Response to an <see cref="IdentificationRequestMessage" />.
/// </summary>
public record IdentificationResponseMessage(
    Guid ServiceId,
    string ServicePurpose,
    string Version,
    DateTime StartupTimeUtc);