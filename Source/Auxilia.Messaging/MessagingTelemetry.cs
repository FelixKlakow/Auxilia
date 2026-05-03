using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Auxilia.Messaging;

/// <summary>
///     Shared OpenTelemetry instrumentation definitions for the Auxilia messaging layer.
///     Provides an <see cref="ActivitySource" /> for distributed tracing and a <see cref="Meter" />
///     for recording publish/receive metrics.
/// </summary>
public static class MessagingTelemetry
{
    /// <summary>Name of the OpenTelemetry <see cref="ActivitySource" /> used by this library.</summary>
    public const string ActivitySourceName = "Auxilia.Messaging";

    /// <summary>Name of the OpenTelemetry <see cref="Meter" /> used by this library.</summary>
    public const string MeterName = "Auxilia.Messaging";

    /// <summary>Shared <see cref="ActivitySource" /> for RabbitMQ publish/consume spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");

    /// <summary>Shared <see cref="Meter" /> for messaging metrics.</summary>
    public static readonly Meter Meter = new(MeterName, "0.1.0");

    /// <summary>Counts messages published to the bus, tagged with the destination topic.</summary>
    public static readonly Counter<long> PublishCounter =
        Meter.CreateCounter<long>(
            "messaging.publish.count",
            description: "Number of messages published to the message bus");

    /// <summary>Counts messages received from the bus, tagged with the source queue.</summary>
    public static readonly Counter<long> ReceiveCounter =
        Meter.CreateCounter<long>(
            "messaging.receive.count",
            description: "Number of messages received from the message bus");
}

