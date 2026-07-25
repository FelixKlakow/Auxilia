namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Builds a credentialed clone URL by injecting userinfo into an http(s) git URL. Non-http URLs
/// (ssh/git) are returned unchanged — they carry credentials out of band. Any existing userinfo on
/// the URL is replaced. The Workspace Manager strips this back out of the mounted working copy.
/// </summary>
public static class RepositoryCloneUrl
{
    public static string WithCredentials(string cloneUrl, string? username, string token)
    {
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return cloneUrl;

        var userInfo = string.IsNullOrEmpty(username)
            ? Uri.EscapeDataString(token)
            : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(token)}";
        var host = uri.GetComponents(UriComponents.Host, UriFormat.UriEscaped);
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var pathAndQuery = uri.GetComponents(
            UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped);
        return $"{uri.Scheme}://{userInfo}@{host}{port}{pathAndQuery}";
    }
}
