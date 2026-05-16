using System.Text.Json.Serialization;

namespace Auxilia.Workflows.SourceControl;

[JsonConverter(typeof(JsonStringEnumConverter<Permission>))]
public enum Permission
{
    Read,
    Write
}
