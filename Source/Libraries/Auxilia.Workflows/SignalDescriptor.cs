namespace Auxilia.Workflows;

public sealed record SignalDescriptor(
    string Name,
    string PayloadTypeName,
    string PayloadSchema,
    string? Description);
