using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Resolves group-claim overage by calling Microsoft Graph <c>POST /me/getMemberGroups</c> with the
/// user's saved (delegated) access token. Requires the Entra app registration to request a Graph
/// scope granting <c>GroupMember.Read.All</c> so the token is Graph-capable. When the token is absent
/// or Graph refuses, it logs and returns no groups so sign-in still succeeds deny-by-default rather
/// than failing — an over-quota user then holds only their non-directory roles.
/// </summary>
public sealed class GraphDirectoryGroupResolver(
    IHttpClientFactory httpClientFactory, ILogger<GraphDirectoryGroupResolver> logger) : IDirectoryGroupResolver
{
    private const string MemberGroupsUrl = "https://graph.microsoft.com/v1.0/me/getMemberGroups";

    public async Task<IReadOnlyList<string>> GetGroupIdsAsync(
        string subject, string? accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            logger.LogWarning(
                "Group-claim overage for {Subject} but no access token to query Microsoft Graph; " +
                "no directory roles resolved.", subject);
            return [];
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, MemberGroupsUrl)
            {
                Content = JsonContent.Create(new { securityEnabledOnly = false })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Microsoft Graph getMemberGroups returned {StatusCode} for {Subject}; " +
                    "no directory roles resolved.", (int)response.StatusCode, subject);
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<MemberGroupsResponse>(ct);
            return payload?.Value ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to query Microsoft Graph for group membership of {Subject}; no directory roles resolved.",
                subject);
            return [];
        }
    }

    private sealed record MemberGroupsResponse([property: JsonPropertyName("value")] IReadOnlyList<string> Value);
}
