namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Published by the Core.Runner to a fanout exchange whenever it registers a workflow type's schema
/// (the manifest-authoritative registration). It lets a consumer in another service (e.g. Core.Api)
/// build its own catalog of registered workflow types and their schemas — slots, capabilities,
/// inputs, views — without ever reading the runner's database, preserving the Core/Runner DB split.
/// </summary>
public sealed record WorkflowSchemaPublished(
    string WorkflowType,
    WorkflowSchema Schema,
    DateTimeOffset TimestampUtc)
{
    public const string ExchangeName = "workflow.schema-registrations";
}
