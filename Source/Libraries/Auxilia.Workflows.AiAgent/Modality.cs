using System.Text.Json.Serialization;

namespace Auxilia.Workflows.AiAgent;

[JsonConverter(typeof(JsonStringEnumConverter<Modality>))]
public enum Modality
{
    Text,
    Image
}
