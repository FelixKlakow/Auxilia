# Auxilia.Core.Contracts

The dependency-free contract surface of the Core: the request/response DTOs shared by `Auxilia.Core.Api` (server) and `Auxilia.Core.Client` (typed client), and by any other Core client. Pure records, no behaviour.

## Invariants
- **Zero dependencies.** This project references nothing (no messaging, no data access, no ASP.NET). It is the neutral vocabulary both sides compile against — keep it that way so a client never drags in server internals.
- **Never carries secret values.** Connector DTOs expose setting *keys* / references only; the Core keeps secret values encrypted at rest and never returns them over the wire (see `ConnectorService` in `Auxilia.Core.Api`).
- Changes here are wire-contract changes — they hit the REST payloads and the client at once. Keep additions additive (nullable / defaulted) so older clients keep working.

## File / Folder Map
```
Source/Libraries/Auxilia.Core.Contracts/
├── RunContracts.cs           # RunRequest, RunAccepted, RunStatus, RunQuery, PagedResult<T>
├── ConfigurationContracts.cs # SlotBinding, CreateRunConfiguration, RunConfiguration, ConfigurationQuery
└── ConnectorContracts.cs     # CreateConnector, Connector (keys only), ConnectorQuery
```
