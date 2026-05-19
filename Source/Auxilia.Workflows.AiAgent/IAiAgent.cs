namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Runtime contract for AI invocation inside a workflow.
/// Provider packages implement this interface; method signatures are defined during blueprint planning.
/// Workflow code depends on this contract, not on any specific AI SDK.
/// </summary>
[Obsolete("Use IAiInference instead.")]
public interface IAiAgent { }
