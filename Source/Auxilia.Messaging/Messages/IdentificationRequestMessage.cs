namespace Auxilia.Messaging.Messages;

/// <summary>
///     Sent to a service's queue to ask it to identify itself.
/// </summary>
public record IdentificationRequestMessage(
    Guid MessageId,
    Guid RequestingServiceId,
    string ResponseTopic);