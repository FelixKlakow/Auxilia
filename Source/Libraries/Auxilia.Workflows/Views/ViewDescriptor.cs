namespace Auxilia.Workflows.Views;

public enum ViewRendering { Stream, Log, Table, Chart, Markdown, Custom }

public enum ViewLifecycle { Live, Persisted, LiveAndPersisted }

/// <summary>
/// A named, schema-declared view (ARCHITECTURE §15): the frontend renders any workflow's
/// views purely from these descriptors — no per-workflow frontend changes.
/// </summary>
public sealed record ViewDescriptor(
    string Name,
    /// <summary>JSON schema of one view item.</summary>
    string ItemSchemaJson,
    ViewRendering Rendering,
    ViewLifecycle Lifecycle,
    /// <summary>Dashboard renderer plug-in key; only meaningful for <see cref="ViewRendering.Custom"/>.</summary>
    string? RendererKey = null,
    /// <summary>
    /// Optional packaging-time data of the view (opaque JSON) — presentation content that exists
    /// before any run, e.g. a step-flow's declared steps. Its meaning belongs to the renderer
    /// (keyed by <see cref="RendererKey"/>); the platform only transports it.
    /// </summary>
    string? DeclaredDataJson = null);
