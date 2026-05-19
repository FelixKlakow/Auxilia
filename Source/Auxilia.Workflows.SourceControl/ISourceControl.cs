namespace Auxilia.Workflows.SourceControl;

/// <summary>
/// Runtime contract for source-control access inside a workflow.
/// Provider packages implement this interface; method signatures are defined during blueprint planning.
/// Workflow code depends on this contract, not on any specific source-control SDK.
/// </summary>
public interface ISourceControl { }
