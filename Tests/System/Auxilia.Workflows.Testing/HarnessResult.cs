using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Testing;

public record HarnessResult(WorkflowState? State, string? ErrorMessage, WorkflowSchema? Schema);
