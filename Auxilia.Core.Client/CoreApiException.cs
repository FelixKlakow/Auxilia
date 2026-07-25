using System.Net;

namespace Auxilia.Core.Client;

/// <summary>
/// Thrown when a Core API call returns a non-success status. Carries the HTTP <see cref="StatusCode"/>
/// and the server's <see cref="ErrorDetail"/> (from the <c>{ "error": ... }</c> body, when present)
/// so an integrating application can distinguish authorization failures, validation errors, etc.
/// </summary>
public sealed class CoreApiException(HttpStatusCode statusCode, string? errorDetail, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ErrorDetail { get; } = errorDetail;
}
