namespace Auxilia.PlatformData.Artifacts;

/// <summary>
/// Deployment contract for artifact payload storage: every service that reads or writes
/// payloads (the runner writes at run completion, Core.Api serves client downloads) points
/// <see cref="PayloadRoot"/> at the SAME storage location. Unset, each service falls back to
/// its own "{JsonDirectory}/Artifacts" — fine for a writer alone, but cross-service payload
/// reads then miss (metadata still works; downloads return not-found).
/// </summary>
public sealed class ArtifactStoreSettings
{
    public string? PayloadRoot { get; set; }

    public string ResolveRoot(PlatformDataSettings platformData)
        => string.IsNullOrWhiteSpace(PayloadRoot)
            ? Path.Combine(platformData.JsonDirectory, "Artifacts")
            : PayloadRoot;
}
