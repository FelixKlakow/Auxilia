namespace Auxilia.Workflows.Network;

/// <summary>
/// A network endpoint the workflow declares as part of its signed baseline network policy
/// (ARCHITECTURE §10) — the platform resolves the effective policy at dispatch time.
/// </summary>
public sealed record NetworkEndpointDeclaration(string Endpoint, string Purpose);
