namespace Auxilia.Workflows.Policy;

/// <summary>
/// Determines whether a capability operation is allowed.
/// </summary>
public interface IToolPolicy
{
    /// <summary>
    /// Returns <c>true</c> if the given capability operation is allowed; <c>false</c> if it is denied.
    /// </summary>
    /// <param name="capabilityOperation">
    /// Operation key in <c>{snake_case_capability}.{snake_case_operation}</c> format,
    /// e.g. <c>source_control.commit</c> or <c>test_runner.run_tests</c>.
    /// </param>
    [Obsolete("Use IsAllowed<TOperation>(TOperation) instead. Will be removed in a subsequent phase.")]
    bool IsAllowed(string capabilityOperation);

    /// <summary>
    /// Returns <c>true</c> if the given typed capability operation is allowed; <c>false</c> if it is denied.
    /// The key is derived by stripping the <c>Operation</c> suffix from the enum type name and converting
    /// both the capability name and the operation name to <c>snake_case</c>.
    /// </summary>
    bool IsAllowed<TOperation>(TOperation op) where TOperation : struct, Enum;
}
