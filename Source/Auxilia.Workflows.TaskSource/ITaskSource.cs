namespace Auxilia.Workflows.TaskSource;

/// <summary>
/// Runtime contract for reading work items inside a workflow.
/// Provider packages implement this interface; method signatures are defined during blueprint planning.
/// Workflow code depends on this contract, not on any specific task-tracker SDK.
/// </summary>
[Obsolete("Use IWorkItemAccess instead.")]
public interface ITaskSource { }
