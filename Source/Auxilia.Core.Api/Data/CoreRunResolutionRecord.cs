using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// Per-run credential resolution context: the run's slot→connector bindings plus the run-scoped
/// resolution token the runner presents to resolve them just-in-time. **No secrets are stored
/// here** — only connector references, resolved against <c>ConnectorService</c> at resolution time.
/// </summary>
public sealed record CoreRunResolutionRecord : IEntity
{
    /// <summary>The run id (= dispatch <c>CommandId</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Opaque token minted at dispatch; the runner must present it to resolve any slot of this run.</summary>
    public required string ResolutionToken { get; init; }

    /// <summary>The run's slot bindings (connector references / inline provider settings), serialized as JSON.</summary>
    public string SlotBindingsJson { get; init; } = "[]";

    public DateTimeOffset CreatedUtc { get; init; }
}
