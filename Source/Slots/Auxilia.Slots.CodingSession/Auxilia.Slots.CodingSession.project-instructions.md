# Auxilia.Slots.CodingSession

Slot provider `coding-session-workspace`: backs a live coding session's slots with the mounted
workspace directory. The `repository` slot exposes the session's working directory as
`ISourceControlAccess`; the optional `work-items` slot is an inert `IWorkItemAccess` stand-in
for dashboard-triggered sessions that carry no originating work item.

Builds `Auxilia.Slots.CodingSession.slothandler.dll` with its `*.slothandler.manifest.json`
sidecar — the naming contract of `FileSystemPluginDiscovery`. The manifest declares no settings;
`WorkingPath` defaults to `/workspace`.

## Special Rules

- Project references are limited to assemblies baked into the coding-session workflow image
  (`Auxilia.Workflows`, `.SourceControl`, `.TaskSource`) so every type in the shipped DLL
  resolves when the plugin loader scans it — do not add references outside that image.
- Both backing services are deliberately inert: file listing/reading and diffs return empty,
  work-item lookups return null. Only `WorkingPath` carries real state.

## File / Folder Map
```
Source/Slots/Auxilia.Slots.CodingSession/
├── CodingSessionWorkspaceSlotHandler.cs                    # ISlotHandler; wires "repository" + "work-items" slots
└── Auxilia.Slots.CodingSession.slothandler.manifest.json   # provider descriptor (no settings)
```
