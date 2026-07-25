namespace Auxilia.Core.Api.Tests;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that captures the outgoing request and returns a
/// canned response — lets the real Graph / OBO HTTP clients run against a faked boundary.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(ct);
        return responder(request);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over a fixed handler.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
