using System.Text.Json.Serialization;

namespace Auxilia.Workflows;

[JsonConverter(typeof(JsonStringEnumConverter<SourceHostType>))]
public enum SourceHostType
{
    GitHub,
    GitLab
}
