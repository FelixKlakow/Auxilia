namespace Auxilia.Workflows.Views;

/// <summary>The workflow's declared view descriptors, exposed to its DI container.</summary>
public sealed record DeclaredViews(IReadOnlyList<ViewDescriptor> Views);
