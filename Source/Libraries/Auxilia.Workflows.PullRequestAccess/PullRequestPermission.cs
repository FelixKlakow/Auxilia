using System.Text.Json.Serialization;

namespace Auxilia.Workflows.PullRequestAccess;

[JsonConverter(typeof(JsonStringEnumConverter<PullRequestPermission>))]
public enum PullRequestPermission
{
    Read,
    Write
}
