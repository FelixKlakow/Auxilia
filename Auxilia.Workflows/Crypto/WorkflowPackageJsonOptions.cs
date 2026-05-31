using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auxilia.Workflows.Crypto;

public static class WorkflowPackageJsonOptions
{
    public static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
