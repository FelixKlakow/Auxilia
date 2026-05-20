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

    /// <summary>The slot name for which the operation was denied.</summary>
    public string SlotName { get; }

    public ToolPolicyDeniedException(string capabilityOperation, string slotName)
        : base($"Tool policy denied operation '{capabilityOperation}' for slot '{slotName}'.")
    {
        CapabilityOperation = capabilityOperation;
        SlotName = slotName;
    }

    public ToolPolicyDeniedException(string capabilityOperation, string slotName, string message)
        : base(message)
    {
        CapabilityOperation = capabilityOperation;
        SlotName = slotName;
    }
}
