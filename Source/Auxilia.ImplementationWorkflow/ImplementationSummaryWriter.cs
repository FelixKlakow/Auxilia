using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auxilia.ImplementationWorkflow;

public sealed class ImplementationSummaryWriter(ImplementationWorkflowConfiguration configuration)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task WriteAsync(ImplementationSummary summary, CancellationToken cancellationToken = default)
    {
        var dir = configuration.OutputDirectory;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "implementation-summary.json");
        var json = JsonSerializer.Serialize(summary, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}
