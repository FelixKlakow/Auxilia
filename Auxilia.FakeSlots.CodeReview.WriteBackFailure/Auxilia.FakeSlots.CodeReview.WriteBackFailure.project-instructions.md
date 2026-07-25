# Auxilia.FakeSlots.CodeReview.WriteBackFailure

Fake, test-only slot-handler plugin DLL for the `pull-request-code-review` workflow
(`Auxilia.CodeReview.Workflow`): wires the same slots as the happy fake, but
`IPullRequestAccess.PostCommentAsync` throws `Simulated write-back failure` — it exercises the
review-comment write-back failure path (the reviewers still produce a finding and verdict; the
failure is at posting them back). Registers provider type `fake-code-review-write-back-failure`.

Builds `Auxilia.FakeSlots.CodeReview.WriteBackFailure.slothandler.dll` (see `<AssemblyName>`) with
its `*.slothandler.manifest.json` sidecar — the `FileSystemPluginDiscovery` naming contract.
Unsigned (`Category: test-fake`, blank signature/hash, no `Settings`), so it loads only under
`AUXILIA_DEVELOPER_MODE=1`. Published and bound like the happy fake by `Auxilia.FakeSlots.Tests`
and by the code-review system-test environment, where it is the edge-case provider.

## Special Rules

- Unlike the happy fake, workflow bootstrap lives only in the dedicated `workflow-bootstrap` case
  (TwoEyes on, `MinimumSeverity = Info`); it does not piggy-back the `repository` slot.

## File / Folder Map

```
Auxilia.FakeSlots.CodeReview.WriteBackFailure.csproj                      # net10.0 slothandler DLL; refs the CodeReview workflow + capability libs
CodeReviewWriteBackFailureSlotHandler.cs                                 # ISlotHandler; happy fakes except PostCommentAsync throws
Auxilia.FakeSlots.CodeReview.WriteBackFailure.slothandler.manifest.json  # provider fake-code-review-write-back-failure; test-fake category, no Settings
```
