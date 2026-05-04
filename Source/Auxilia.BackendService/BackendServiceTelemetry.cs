using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Auxilia.BackendService;

/// <summary>
///     OpenTelemetry instrumentation definitions for the BackendService.
///     Provides an <see cref="ActivitySource" /> for tracing request handling and a <see cref="Meter" />
///     for recording identification-request metrics.
/// </summary>
public static class BackendServiceTelemetry
{
    /// <summary>Name of the OpenTelemetry <see cref="ActivitySource" /> used by this service.</summary>
    public const string ActivitySourceName = "Auxilia.BackendService";

    /// <summary>Name of the OpenTelemetry <see cref="Meter" /> used by this service.</summary>
    public const string MeterName = "Auxilia.BackendService";

    /// <summary>Shared <see cref="ActivitySource" /> for tracing handler spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");

    /// <summary>Shared <see cref="Meter" /> for service metrics.</summary>
    public static readonly Meter Meter = new(MeterName, "0.1.0");

    /// <summary>
    ///     Counts incoming identification requests, tagged with the queue name they arrived on.
    /// </summary>
    public static readonly Counter<long> IdentificationRequestsReceived =
        Meter.CreateCounter<long>(
            "identification.requests.received",
            description: "Number of identification requests received");

    /// <summary>
    ///     Records the end-to-end processing duration of each identification request in milliseconds.
    /// </summary>
    public static readonly Histogram<double> IdentificationRequestDuration =
        Meter.CreateHistogram<double>(
            "identification.request.duration.ms",
            unit: "ms",
            description: "Duration of identification request processing in milliseconds");
}

