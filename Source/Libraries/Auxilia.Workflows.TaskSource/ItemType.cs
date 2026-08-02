using System.Text.Json.Serialization;

namespace Auxilia.Workflows.TaskSource;

[JsonConverter(typeof(JsonStringEnumConverter<ItemType>))]
public enum ItemType
{
    UserStory,
    Bug,
    Feature,
    Epic
}
