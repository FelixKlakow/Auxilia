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
    bool IsAllowed(string capabilityOperation);
}
