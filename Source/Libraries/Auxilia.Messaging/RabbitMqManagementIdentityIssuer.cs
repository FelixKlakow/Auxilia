using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Messaging;

/// <summary>Where and as whom the RabbitMQ management HTTP API is reached (an administrator-tagged user).</summary>
public sealed record RabbitMqManagementSettings(
    Uri ManagementUri,
    string UserName,
    string Password,
    string VirtualHost = "/");

/// <summary>
///     <see cref="IBrokerIdentityIssuer" /> over the RabbitMQ management HTTP API: a user with no
///     tags (no management access), vhost permissions, and topic permissions. Transient failures
///     are retried here (repo rule); a half-provisioned user is removed before the error surfaces,
///     so a failed issue never leaves a usable credential behind.
/// </summary>
public sealed class RabbitMqManagementIdentityIssuer(
    HttpClient http,
    RabbitMqManagementSettings settings,
    ILogger<RabbitMqManagementIdentityIssuer>? logger = null) : IBrokerIdentityIssuer
{
    private const int MaxAttempts = 6;
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public async Task<string> IssueAsync(
        string userName, BrokerPermissions permissions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var user = Uri.EscapeDataString(userName);
        var vhost = Uri.EscapeDataString(settings.VirtualHost);

        await SendAsync(HttpMethod.Put, $"api/users/{user}",
            new { password, tags = "" }, cancellationToken);
        try
        {
            await SendAsync(HttpMethod.Put, $"api/permissions/{vhost}/{user}",
                new { configure = permissions.Configure, write = permissions.Write, read = permissions.Read },
                cancellationToken);
            foreach (var topic in permissions.TopicPermissions)
                await SendAsync(HttpMethod.Put, $"api/topic-permissions/{vhost}/{user}",
                    new { exchange = topic.Exchange, write = topic.Write, read = topic.Read },
                    cancellationToken);
        }
        catch
        {
            await RevokeAsync(userName, CancellationToken.None);
            throw;
        }

        return password;
    }

    public async Task<bool> RevokeAsync(string userName, CancellationToken cancellationToken = default)
        => await SendAsync(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(userName)}", null,
            cancellationToken, tolerateNotFound: true) != HttpStatusCode.NotFound;

    private async Task<HttpStatusCode> SendAsync(
        HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken,
        bool tolerateNotFound = false)
    {
        var uri = new Uri(EnsureTrailingSlash(settings.ManagementUri), relativePath);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.UserName}:{settings.Password}")));
            if (body is not null)
                request.Content = JsonContent.Create(body);

            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode
                    || (tolerateNotFound && response.StatusCode == HttpStatusCode.NotFound))
                    return response.StatusCode;
                if ((int)response.StatusCode < 500 || attempt >= MaxAttempts)
                    throw new InvalidOperationException(
                        $"RabbitMQ management {method} {relativePath} failed: {(int)response.StatusCode} "
                        + await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(ex,
                    "RabbitMQ management {Method} {Path} failed (attempt {Attempt}); retrying.",
                    method, relativePath, attempt);
            }
            finally
            {
                response?.Dispose();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri)
        => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
