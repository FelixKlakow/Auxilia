# Auxilia.FakeSlots.CodeReview.Happy

Fake, test-only slot-handler plugin DLL that fills every slot of the `pull-request-code-review`
workflow (`Auxilia.CodeReview.Workflow`) with in-memory fakes — the happy path: the primary
reviewer records one Medium style finding plus a *Reviewed* file verdict, the secondary reviewer
*Approves*, and all write-backs succeed. Registers provider type `fake-code-review-happy`.

Builds `Auxilia.FakeSlots.CodeReview.Happy.slothandler.dll` (see `<AssemblyName>`) with its
`*.slothandler.manifest.json` sidecar — the naming contract of `FileSystemPluginDiscovery`. The
manifest is unsigned (`Category: test-fake`, blank signature/hash, no `Settings`), so it loads
only under `AUXILIA_DEVELOPER_MODE=1`. `Auxilia.FakeSlots.Tests` publishes then resolves it; the
code-review and end-to-end system-test environments publish it into the container plugins dir and
bind each slot to `fake-code-review-happy` via `RegisterSlotProviderCommand`.

## Special Rules

- The `repository` case also runs `RegisterWorkflowBootstrap` (TwoEyes on, write-back with
  `PostSummaryToWorkItems`): the container JIT-activation path registers handlers only for slots
  the workflow DECLARES, and `pull-request-code-review` declares no `workflow-bootstrap` slot — so
  the dedicated `workflow-bootstrap` case runs only on the eager/unit-test path.
- `PrimaryReviewerSession` publishes a scripted `AgentChatPublisher` conversation for the
  dashboard agent-chat renderer; findings/verdicts reach the workflow through the typed
  `CodeReviewResultSinkMcpTools` result sink, never structured text.
- `FakePullRequestAccess.GetLinkedWorkItemsAsync` surfaces `WORKFLOW_CONTEXT__WORKITEMID` when
  present so the work-item summary write-back path runs end-to-end.

## File / Folder Map

```
Auxilia.FakeSlots.CodeReview.Happy.csproj                      # net10.0 slothandler DLL; refs the CodeReview workflow + capability libs
CodeReviewHappySlotHandler.cs                                  # ISlotHandler; per-slot canned happy fakes + workflow bootstrap
Auxilia.FakeSlots.CodeReview.Happy.slothandler.manifest.json   # provider fake-code-review-happy; contracts, test-fake category, no Settings
```
