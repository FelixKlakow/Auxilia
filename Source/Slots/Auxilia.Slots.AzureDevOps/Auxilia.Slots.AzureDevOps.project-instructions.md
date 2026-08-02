# Auxilia.Slots.AzureDevOps

Slot provider `tfs-account` for the `work-items` slot (category `task-source`): backs an
`IWorkItemAccess` with Azure DevOps / on-prem TFS work items over the Work Item Tracking
REST API — cloud and on-prem speak the same API. The SAME `tfs-account` connector that
authenticates repositories binds work items, so the manifest settings mirror the connector's
stored keys exactly: `OrgUrl` (Text, required) and `token` (Secret — the PAT).

Builds `Auxilia.Slots.AzureDevOps.slothandler.dll` with its `*.slothandler.manifest.json`
sidecar — the naming contract of `FileSystemPluginDiscovery`.

## Special Rules

- The PAT travels ONLY as the `Authorization: Basic base64(":" + pat)` header — never in
  error messages, logs, or URLs. Non-success writes throw with status + a body snippet.
- `System.TeamProject` and `System.WorkItemType` are cached per work item id — comment and
  state URLs need them, so those calls resolve the item first.
- `GetAttachmentsAsync` skips a single missing/unreadable attachment instead of failing the
  read; `GetStatesAsync` on an unknown item returns empty; `GetWorkItemAsync` maps 404 → null.
- `MessageHandler` on the handler (and the access-class constructor parameter) is the only
  HTTP seam (`InternalsVisibleTo` `Auxilia.Slots.AzureDevOps.Tests`).

## File / Folder Map
```
Source/Slots/Auxilia.Slots.AzureDevOps/
├── AzureDevOpsWorkItemsSlotHandler.cs                    # ISlotHandler; connector settings → PAT-authenticated IWorkItemAccess
├── AzureDevOpsWorkItemAccess.cs                          # IWorkItemAccess over the WIT REST API (fields mapping, comments, states, attachments)
└── Auxilia.Slots.AzureDevOps.slothandler.manifest.json   # provider descriptor mirroring the tfs-account connector settings
```
