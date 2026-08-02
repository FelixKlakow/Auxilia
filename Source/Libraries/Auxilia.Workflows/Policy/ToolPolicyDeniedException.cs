using System.Text.RegularExpressions;

namespace Auxilia.Workflows.Policy;

/// <summary>
/// Thrown by guarded capability decorators when <see cref="IToolPolicy.IsAllowed"/> returns <c>false</c>.
/// </summary>
public sealed class ToolPolicyDeniedException : Exception
{
    /// <summary>The capability operation key that was denied, e.g. <c>source_control.commit</c>.</summary>
    public string CapabilityOperation { get; }

    /// <summary>Alias for <see cref="CapabilityOperation"/>.</summary>
    public string Key => CapabilityOperation;

    /// <summary>The typed operation value that was denied, if constructed via the typed overload.</summary>
    public object? Operation { get; }

    /// <summary>The slot name for which the operation was denied.</summary>
    public string SlotName { get; }

    public ToolPolicyDeniedException(string capabilityOperation, string slotName)
        : base($"Tool policy denied operation '{capabilityOperation}' for slot '{slotName}'.")
    {
        CapabilityOperation = capabilityOperation;
        SlotName = slotName;
    }

    public ToolPolicyDeniedException(object operation, string slotName)
        : base($"Tool policy denied operation '{ToOperationKey(operation)}' for slot '{slotName}'.")
    {
        Operation = operation;
        CapabilityOperation = ToOperationKey(operation);
        SlotName = slotName;
    }

    public ToolPolicyDeniedException(string capabilityOperation, string slotName, string message)
        : base(message)
    {
        CapabilityOperation = capabilityOperation;
        SlotName = slotName;
    }

    private static string ToOperationKey(object operation)
    {
        if (operation is string s) return s;
        var typeName = operation.GetType().Name;
        if (typeName.EndsWith("Operation", StringComparison.Ordinal))
            typeName = typeName[..^"Operation".Length];
        return $"{ToSnakeCase(typeName)}.{ToSnakeCase(operation.ToString()!)}";
    }

    private static string ToSnakeCase(string pascalCase)
        => Regex.Replace(pascalCase, "([a-z])([A-Z])", "$1_$2").ToLowerInvariant();
}
