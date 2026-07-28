# Auxilia.Slots.AzureDevOps.Tests

Unit tests for `Auxilia.Slots.AzureDevOps` — the Azure DevOps / TFS work-items slot
(`AzureDevOpsWorkItemsSlotHandler` registration and `AzureDevOpsWorkItemAccess` behaviour).

## Special Rules
The HTTP layer is a stub `HttpMessageHandler` speaking real Work Item Tracking REST API
response shapes (mirrors `ConnectorBrowseAzureDevOpsTests` — no official ADO container
exists): tests assert exact URLs, JSON-patch bodies, content types and the PAT basic-auth
header, so a real-tenant verification only has to confirm connectivity, not behavior.

## File / Folder Map
```
Auxilia.Slots.AzureDevOps.Tests/
└── UnitTests/
    ├── AzureDevOpsWorkItemAccessTests.cs        # field mapping (HTML-stripped description, tags), 404→null, comments, states, SetState json-patch, attachments incl. single-failure skip, auth header
    └── AzureDevOpsWorkItemsSlotHandlerTests.cs  # slot registration → IWorkItemAccess; missing OrgUrl/token settings throw; unknown slot throws
```
