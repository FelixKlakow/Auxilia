using Auxilia.Workflows;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Resolves whether one run gets its workflow's interactive terminal. A declared gate is
/// evaluated as data: the run's context value for the gate's input (falling back to the
/// input's declared default) must equal the enabling value — the Core never interprets what
/// either value means.
/// </summary>
internal static class InteractiveTerminalResolver
{
    public static int? ResolvePort(WorkflowSchema? schema, IReadOnlyDictionary<string, string> context)
    {
        if (schema?.InteractiveTerminalPort is not { } port)
            return null;
        if (schema.InteractiveTerminalGate is not { } gate)
            return port;

        var value = context
                        .FirstOrDefault(entry =>
                            string.Equals(entry.Key, gate.InputName, StringComparison.OrdinalIgnoreCase))
                        .Value
                    ?? schema.Inputs
                        .FirstOrDefault(input =>
                            string.Equals(input.Name, gate.InputName, StringComparison.OrdinalIgnoreCase))
                        ?.DefaultValue;

        return string.Equals(value?.Trim(), gate.EnabledValue, StringComparison.OrdinalIgnoreCase)
            ? port
            : null;
    }
}
