# Auxilia — Backlog

> Live tracker of known follow-ups (open items only). Delivered programs and their decision
> records live in `docs/delivered/` and the git history.

## Code review 2026-09-09 — open findings

Full-repo review (six parallel reviewers, findings traced end to end before listing). Grouped by
severity; each item names the file so the fix agent lands in the right place. Strike items
through as they close, same as the other sections.

### Critical — trust boundaries and secrets
- ~~**Unauthenticated `WorkflowStateMessage` terminates any run** — the runner's
  `WorkflowStateHandler` takes terminal messages off the `workflow.state` fanout with no instance
  token; every container holds the bus password, so any run can publish `Success` for another
  instance and the runner consumes its token, marks it terminal, deletes its bind-mounted
  workspace and destroys its pod. Fix: carry the instance token on the message, gate with
  `WorkflowInstanceTokenRegistry.Validate`, and ignore instances this runner does not own.~~ — DONE 2026-09-09: `WorkflowStateMessage` carries `WorkflowName` + `InstanceToken` (SDK fills both at every publish site); `WorkflowStateHandler` validates token + type first and returns before any side effect.
- ~~**Instance token is not bound to the workflow type** — `WorkflowInstanceTokenRegistry` stores
  `WorkflowType` but never compares it; a run of type A can register a manifest naming type B and
  rewrite B's schema (endpoints → egress network, repos, pod limits) Core- and runner-side. Fix:
  `Validate`/`TryBeginRegistration` must require the manifest's `WorkflowName` to equal the
  issued type.~~ — DONE 2026-09-09: `Validate(id, token, claimedWorkflowType)` + `TryBeginRegistration(id, token, claimedWorkflowType)` reject a mismatch (ordinal); registration (audited `workflow-type-mismatch`), announcement, and state handlers pass the claimed name.
- ~~**Approval pipeline approves the CURRENT package, not the reviewed one** —
  `WorkflowTypeRegistryService.ApproveAsync` is keyed by type name only (no Pending check, no
  package-hash/`UpdatedUtc` binding), so re-registering during the verdict run gets the
  replacement auto-signed, and a late Approve overrides a human Deny. Fix: pass the reviewed
  identity into approve/deny and refuse unless still Pending with that identity.~~ — DONE 2026-09-09: registrations carry `PackageHashBase64`; the pipeline uses `ApproveReviewedAsync`/`DenyReviewedAsync` (refused unless still Pending with the reviewed hash + `UpdatedUtc`), human approve/deny refuse non-Pending (409), and the approval download token is bound to type + package hash.
- ~~**Step-up has no throttle** — `POST /auth/step-up` and MCP `step_up` verify the password with
  neither the `auth-login` rate limiter nor `LoginAttemptThrottle`; a leaked bearer + brute force
  = elevation. Fix: apply both, keyed by principal.~~ — DONE 2026-09-09: `/auth/step-up` carries the `auth-login` per-IP policy and both REST + MCP `step_up` run the per-principal `LoginAttemptThrottle` (refused before the secret check, audited `rate-limited`).
- ~~**Inline Secret-kind binding settings stored and echoed in clear** — the editor offers
  provider `Secret` descriptors as inline `SlotBinding.Settings`; `RunConfigurationService`
  persists `SlotBindingsJson` verbatim (no `ISettingsProtector`), GET returns them, and
  `SlotCredentialResolver` uses them as the run's JIT credential. Fix: protect at write, mask
  in DTOs (names only), "leave empty to keep" in the editor — or refuse inline secrets.~~ — DONE 2026-09-09: new `SlotBindingSecrets` (catalog-driven Secret keys) protects on entry (configuration create/update + inline run), masks every read DTO (key present, empty value), keeps the stored value when an update sends it empty/omitted, and unprotects only in `SlotCredentialResolver`; the editor renders a "leave empty to keep" placeholder.
- ~~**Resolution token resolves credentials for 30 days after the run ends** —
  `SlotCredentialResolver.ResolveAsync` checks the digest and bindings but never the run's
  terminal state (the stash is retained for rerun); caller-supplied RSA key → plaintext secrets.
  Fix: reject when the `CoreRunRecord` is terminal (keep the stash for rerun).~~ — DONE 2026-09-09: `SlotCredentialResolver.AuthorizeAsync` (digest + non-terminal `CoreRunRecord`, audited `run-ended` rejection) gates resolve-slot AND the package / environment-layer downloads; stash retained.
- ~~**AdminConsole silently downgrades to the service API key** — when the relayed user bearer
  expires (30 min) `ConsoleCallerTokenProvider` returns null and `CoreCallerTokenHandler`
  attaches `Core:ApiKey`; with the bootstrap admin key the operator gains admin reach and audit
  attribution shifts to the service. Fix: no app-key fallback on user circuits — force
  re-navigation/re-mint.~~ — DONE 2026-09-09: `CoreCallerTokenHandler` lost its app-key fallback (no token = no Authorization header; the console holds no `Core:ApiKey` any more); the prerender relays token + session cookie (server-side one-shot handle), so the circuit re-mints from the cookie on expiry, and a Core-rejected re-mint surfaces as the shell's "session expired — reload" banner (`ConsoleCallerTokenProvider.SessionExpired`).
- ~~**Creating a principal with an existing username hijacks the login** — `PrincipalDirectory`
  upserts `CredentialRecord.IdForPassword(username)` with no existence check. Fix: read first,
  409 on conflict.~~ — DONE 2026-09-09: `CreateHumanAsync` reads the credential first and throws `PrincipalConflictException` (REST 409, MCP error result; the seeder logs and skips instead of hijacking).
- ~~**Rerun skips workspace and configuration visibility gates** — `RunService.RerunAsync`
  re-checks connectors + catalog but replays another principal's personal workspace mounts and
  inline settings; the endpoint only checks `workflow.trigger`. Fix: persist workspace ids in the
  stash and re-gate them; apply `IsVisibleAsync` for personal configurations.~~ — DONE 2026-09-09: the stash carries `WorkspaceIdsJson` + `ConfigurationId`; `RerunAsync` re-gates every workspace (`WorkspaceAccessDeniedException`) and the personal configuration's visibility (`seesAllConfigurations` from the caller's manage permission) against the rerunner.

### High — correctness and resilience
- ~~**Failover redispatch always fails for personal connectors** — `FailoverMonitor` calls
  `RerunAsync(orphan, triggeredBy: null)` and `ConnectorAccessPolicy.CanUseAsync` refuses
  personal connectors for a null principal. Fix: `triggeredBy ?? stash.TriggeredBy`.~~ — DONE 2026-09-09: `FailoverMonitor.RedispatchAsync` reads the stash's `TriggeredByPrincipalId` and reruns as that principal.
- ~~**Dead-runner failover writes `Failed` with plain `SaveAsync`** — no CAS, so every Core node
  fails over + re-dispatches the same orphans, and a run that completed after the scan snapshot
  is overwritten and re-run. Fix: re-read + `TrySaveAsync(current.Version)`, winner re-dispatches
  (the unclaimed sweep already does this).~~ — DONE 2026-09-09: `FailOverAsync` re-reads fresh, skips terminal, CAS via `TrySaveAsync(current.Version)`; only the winner cancels/audits/publishes/re-dispatches.
- ~~**Cancel is lost three ways** — (a) cancelling a still-`Dispatched` run publishes to the
  runner, which drops "unknown instance", so the run executes later; (b) in a runner pool the
  shared `workflow.cancel-commands` queue hands the command to one runner that may not own the
  instance (`WorkflowCancelDispatcher`); (c) MCP `cancel_run` skips the dispatch-id aliasing the
  REST endpoint does. Fix: Core transitions a `Dispatched` record to Cancelled via CAS + drops the
  stash; runner forwards to `workflow-cancel-{id}` unconditionally; MCP uses `run?.RunId ?? id`.~~ — DONE 2026-09-09: `RunService.CancelAsync` settles a `Dispatched` run Core-side (CAS → Cancelled, stash discarded, status event); runner `WorkflowCancelDispatcher` forwards unconditionally (registry dependency dropped); MCP `cancel_run` de-aliases like REST.
- ~~**Late/re-ordered `Dispatched` event resurrects a ghost row** — after the `Received` rekey
  deletes the command-keyed row, a reordered `Dispatched` in `RunTrackingService.TryApplyAsync`
  re-inserts it; the claim sweep then fails the healthy run, dispatches a duplicate and deletes
  the live stash. Fix: never insert `Dispatched` from the bus when an instance row already
  carries that `CommandId`.~~ — DONE 2026-09-09: `RunTrackingService` never applies a `Dispatched` event from the bus (the Run API authors that row synchronously).
- ~~**Non-owning runners corrupt the run type to "unknown"** — every runner receives every
  terminal message (exclusive per-subscriber queue); a runner with no record still publishes a
  status event with type "unknown", which `RunTrackingService` stores verbatim. Fix: return early
  when the record is missing or not owned.~~ — DONE 2026-09-09: `WorkflowStateHandler` returns before publish/audit/cleanup unless the record exists and `OwnerServiceId == CoreRunnerInfo.ServiceId`.
- ~~**Client unary timeout is treated as host shutdown** — `CoreClient` surfaces its
  `UnaryTimeoutSeconds` watchdog as `TaskCanceledException`; `EmailTaskSourceAdapter`,
  `ScheduledTriggerEngine`, `ArtifactChainingEngine` and `EventTriggerEngine` all filter
  `OperationCanceledException` as "host shutdown", so one slow Core call stops the TriggerHost
  (BackgroundService StopHost) or silently kills a scheduler for the process lifetime. Fix:
  translate the watchdog to `TimeoutException` in the client; engines catch by
  `ct.IsCancellationRequested`.~~ — DONE 2026-09-10: every unary helper runs through `CoreClient.UnaryAsync`, which rethrows a watchdog expiry as `TimeoutException` (inner OCE) unless the caller's token is cancelled; the email adapter and all three engines guard with `when (!ct.IsCancellationRequested)`, the scheduler's store read moved inside the per-tick guard, and stream consumers log-and-continue on a failed handle/dispatch.
- ~~**Any cancellation ends a workflow as `Cancelled`, exit 0** — `WorkflowBuilder`'s catch does
  not check its own token, so a provider HttpClient timeout reports as operator cancel. Inverse in
  `SlotActivator`/`ResourceProxyClient`/`PodControlClient`: `WhenAny` with a cancelled delay task
  throws `TimeoutException`, so operator cancel reports as `Failed`. Fix: `when
  (cts.IsCancellationRequested)` in the builder; `ct.ThrowIfCancellationRequested()` after
  `WhenAny` in the clients.~~ — DONE 2026-09-10: builder catch is `when (cts.IsCancellationRequested)` (a foreign OCE is `Failed`/exit 1); the three request clients drop the pending entry and `ThrowIfCancellationRequested()` before treating a completed delay as a timeout.
- ~~**Secondary tmux sessions receive no environment** — `TerminalSessionHost` sets variables on
  the `tmux new-session` client; only the first session (which forks the server) inherits them.
  The reviewer console runs on the author's credential with an empty `-p ""` prompt → every
  console review round is "no verdict file". Fix: `tmux new-session -e KEY=VALUE` per session.~~ — DONE 2026-09-10: `BuildTmuxStartInfo` emits `-e KEY=VALUE` per variable and no longer sets the client environment (the images' Debian base ships tmux 3.3+); failure messages redact `-e` values (`DescribeForLog`).
- ~~**Implementation workflow pushes its exchange files** — with a mount working directory
  `.git` is above `WorkspaceDirectory`, the `.git/info/exclude` is never written, and
  `git add -A` commits `.auxilia/` (story, attachments, verdicts) and `.mcp.json`. Fix: resolve
  the toplevel via `git rev-parse --show-toplevel`, or stage with an excluding pathspec.~~ — DONE 2026-09-10: the exclude lands at `git rev-parse --git-path info/exclude` (resolved against the workspace; covers a workspace below the root AND worktrees, hence `--git-path` over `--show-toplevel`), and staging is `add -A -- :/ :(exclude).auxilia :(exclude).mcp.json` so a missing exclude can never leak them.
- ~~**Versioned Mongo save loops forever on non-concurrency write errors** —
  `MongoDbEfDataAccess.SaveVersionedAsync` catches every `DbUpdateException` and retries with no
  cap/delay/log. Fix: catch `DbUpdateConcurrencyException` only, cap retries. Also `RemoveAsync`
  does not catch the concurrency exception on versioned entities (documented `false` becomes a
  throw).~~ — DONE 2026-09-10: `SaveVersionedAsync` retries only `DbUpdateConcurrencyException` (and a
  duplicate-key insert whose row now exists), max 8 attempts with jittered backoff, everything else
  surfaces; `RemoveAsync` re-reads on a concurrency conflict and returns `false` when the row is gone.
- ~~**`DeterministicGuid.For` joins key parts with no delimiter** — `("a","bc")` ≡ `("ab","c")`;
  affects `CoreRunViewRecord` (view "log1"/seq 2 vs "log"/seq 12 → overwrite), slot configs,
  access entries. Fix: delimiter or length-prefix inside `For`; drop the `""` pseudo-separators.
  Changes every derived id (fine pre-production, invalidates existing stores).~~ — DONE 2026-09-10:
  every part is length-prefixed inside `For` (the old join used an invisible U+001F separator);
  pseudo-separator parts dropped from all `IdFor`s, `EnvironmentBaseRecord.IdFor` passes name and
  version as parts; the committed dev `core-data` ids regenerated. Any other JSON store created before
  this change (untracked runner/trigger-host state) must be wiped — its derived ids no longer match.

### Medium — leaks, retention, lost data
- ~~**Runner pre-flight failure after workspace prep never cleans run roots** — `FailPreFlightAsync`
  leaves `{Workspace,Output}/{id}` on disk, including push-enabled clones with the token in
  `.git/config`. Fix: call `RunRootsCleanup` from it.~~ — DONE 2026-09-10: `FailPreFlightAsync` drops the pending package entry and runs `RunRootsCleanup` (workspace, pod, output root, extracted package); component test `WhenPreFlightFailsAfterWorkspacePreparation_TheRunRootsAreSwept`.
- ~~**Output directory leaks on every terminal path except Success-with-outputs**
  (`WorkflowStateHandler` cleans workspace + pod only; `ArtifactPersister` deletes on success).~~ — DONE 2026-09-10: `WorkflowStateHandler` routes every terminal transition through `RunRootsCleanup` (after artifact persistence; it now returns the companion logs); `ArtifactPersister` no longer deletes anything.
- ~~**ZIP-package extraction dir is never deleted; `PendingWorkflowPackageStore` is keyed by
  type**, so two concurrent dispatches of one type overwrite each other.~~ — DONE 2026-09-10: the extraction dir is deterministic (`auxilia-wf-{instanceId:N}`, `RunRootsCleanup.PackageDirectoryFor`) and deleted by the exit watcher on every container exit, by pre-flight failure, and by the re-adoption sweep (not on the graceful state message — the container may still be exiting); `PendingWorkflowPackageStore` keyed by instance id, the announcement handler consumes by `WorkflowInstanceId`.
- ~~**Warm cache** (`WorkspaceManager`): fetch runs credential-less after the URL strip (private
  repos never refresh), the working tree is never advanced past the first clone, and the cache
  key hashes the tokened URL so each token rotation adds a full clone.~~ — DONE 2026-09-10: cache keyed by the stripped URL; every clone/fetch names the stripped URL and injects the credential per command via `-c url.<tokened>.insteadOf=<stripped>` (no `.git/config` ever holds it; `StripUserInfo` no longer renders a default `:443`); after the fetch the cache checkout is advanced to `refs/remotes/origin/<requested-or-default branch>`; stale cache only on fetch failure (Warning). Component tests `Prepare_SecondInstance_StartsOnTheCommitAddedToOriginSinceTheFirstRun` / `Prepare_RequestedBranch_IsCheckedOutFromTheSharedCacheEntry`, unit `WorkspaceManagerCredentialTests`.
- ~~**View mirror full-scans all view records per message; view rows have no retention**
  (`RunViewTrackingService`).~~ — DONE 2026-09-10: per-run counter seeded once from the store on first sight (re-deliveries do not consume cap; with N nodes the cap is a soft guard), plus an hourly sweep deleting the rows of runs terminal for more than `CoreApi:ViewRetentionDays` (default 30, beside `ResolutionRecordRetentionDays`; live runs never touched). Unit `RunViewTrackingServiceTests`.
- ~~**`/api/runs/{id}/stream` reads the snapshot before subscribing** — contrary to its own
  comment; non-terminal transitions in the gap are lost.~~ — DONE 2026-09-10: the endpoint subscribes first, then resolves the alias + reads the record and emits the snapshot (artifact/event streams have no snapshot and were already subscribe-then-flush). Component `RunStreamSnapshotOrderingTests` injects a transition inside the window via a wrapped run store.
- ~~**Subscribe cancelled during the bind-gate wait unbinds a sibling's keys** —
  `RunStreamPublisher`/`ArtifactStreamPublisher`/`EventStreamPublisher` decrement an audience
  count they never incremented. Fix: refcount before any await.~~ — DONE 2026-09-10: contract change — a subscribe that throws (incl. cancellation at the gate) has rolled back its own refcount/bindings and the three brokers DISCARD the failed subscription without an unsubscribe callback. Unit `RunStreamPublisherBindingTests.SubscribeCancelledWhileWaitingForTheGate_LeavesTheSiblingsKeysBound` + `FilteredStreamPublisherRollbackTests`.
- ~~**RabbitMQ publishes have no publisher confirms**; **subscription dispose leaks the bind
  channel** when `BasicCancelAsync` throws on a closed channel.~~ — DONE 2026-09-10: the publish channel is created with `CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)` (publish awaits the broker ack, `PublishException` on nack); both handles guard the courtesy cancel and release consumer channel / bind channel / gate in nested `finally`; new `Auxilia.Messaging.Tests` pins the dispose paths against Moq'd channels (confirmations need the Docker suite).
- ~~**Chaining/event catch-up skips the gap before the first live event** (`lastSeenUtc` is only
  set from a handled event); same-timestamp page boundaries can also be skipped.~~ — DONE 2026-09-10: the cursor is seeded from the host clock (`TimeProvider`, new engine ctor dependency) at consumer start; catch-up queries under ONE fixed bound `lastSeen − 1 s` (`CatchUpOverlap`) and pages with `Skip` (id dedupe absorbs the overlap).
- ~~**Email trigger filters mark non-matching mail `\Seen`**, stealing it from other triggers on
  the same mailbox.~~ — DONE 2026-09-10: the adapter polls per MAILBOX (triggers grouped by slot instance, one fetch per poll, due when any trigger is due) and evaluates every enabled trigger against each mail; `\Seen` is set once after all matching dispatches were accepted (or immediately when no trigger matches); health stays per trigger.
- ~~**Coding-agent slot handlers kill the CLI only on cancellation** — any other exception (bus
  publish failure) leaves `claude`/`codex`/`copilot` editing and pushing inside the container.~~ — DONE 2026-09-10: all three agents kill the child's process tree in a catch-all (`Kill` is a no-op once exited) and rethrow; Codex/Copilot now run behind the new `ICliProcessFactory` seam (`Auxilia.Workflows.AiAgent.CodingAgent/CliProcess.cs`) so the paths are unit-tested.
- ~~**`ProcessGitRunner` never drains stderr** — push failures report empty text; large stderr
  can deadlock.~~ — DONE 2026-09-10: both pipes are read concurrently; a failing command's `Output` is stdout + stderr (success stays stdout-only so `status`/`diff` parsing is unchanged).
- ~~**Code review is fail-open** — a missing file verdict counts as Reviewed and a silent
  secondary reviewer Approves (`PrimaryReviewOrchestrator`, `TwoEyesPassService`).~~ — DONE 2026-09-10: no file verdict → `Skipped` ("No verdict recorded by the primary reviewer"; `Failed` on a Critical file); no secondary verdict → the finding stays `NotReviewed` with no reviewer attribution (surfaced, never credited as approved — dropping it as Rejected would hide real findings on infrastructure failures). The fail-open was documented as a "conservative default" in the tests; treated as a defect, in line with the Implementation workflow's "a silent reviewer never approves".
- ~~**Group-held roles are missing from claims-based checks** — `LocalIdentityProvider` /
  `ExternalIdentityProvisioner` build `Roles` from direct assignments only, so a group-granted
  admin gets an empty `/auth/me` and visibility-filtered lists.~~ — DONE 2026-09-10: both session producers union first-class-group roles via the new `EffectiveRoles` helper (`GroupRoleResolver`, cached through `PrincipalRoleCache.SetGroupRoles` like the Policy Engine). Unit tests in Governance; component `GroupRoleClaimsTests` (`/auth/me` + configuration listing with a group-held Operator role).
- ~~**Concurrent OAuth refreshes on one connector race** on rotated refresh tokens
  (`ConnectorTokenRefresher`, non-CAS `UpdateAsync`).~~ — DONE 2026-09-10: single-flight per connector (keyed `SemaphoreSlim`); the waiter re-reads after acquiring and skips the exchange when the winner already refreshed. Unit `ConcurrentResolves_OfOneStaleConnector_RefreshOnce_AndBothGetTheRotatedToken`.
- ~~**`FileSystemArtifactStore` assigns lineage versions non-atomically** and orphans payload
  files when the index write fails.~~ — DONE 2026-09-10: versions are assigned under a per-lineage
  in-process gate (the store is one singleton over a local directory); the payload lands in a
  `.pending` file, is moved into place only after the index row is written, and is deleted on any
  failure (a failed move also removes the index row).
- ~~**Rerun's workspace access denial maps to 400 not 403** (`InvalidOperationException` instead
  of `RunAccessDeniedException`).~~ — DONE 2026-09-09: `WorkspaceAccessDeniedException : RunAccessDeniedException` at dispatch and rerun.

### Low
- `SteeringCodec.Decode` throws on a non-string `$type` (contract says never throws).
- Email manifest ships GreenMail test ports 3143/3025 as `DefaultValue`; handler defaults are
  993/587 and the editors persist the manifest values.
- ~~TriggerHost documents `WorkflowClient:*` client options it never binds.~~ — DONE 2026-09-10: `Program.cs` binds the whole `Core:*` section onto `CoreClientOptions` (stream/unary knobs included); the project-instructions list the real keys; new `Auxilia.TriggerHost.Tests` component test observes the bound unary timeout.
- ~~`ConsoleAuthenticationStateProvider` catches only `CoreApiException`; an
  `HttpRequestException` during a Core restart tears the circuit down.~~ — DONE 2026-09-09: mirrors `CoreBackedAuthenticationHandler` — an unreachable Core logs a warning and yields anonymous.
- ~~`/api/configurations/{id}/run?onBehalfOf=` evaluates visibility before the delegation policy
  (visibility oracle).~~ — DONE 2026-09-10: delegation policy first, visibility (for the triggering principal) second. Component `CallerWithoutOnBehalfOf_IsRefusedBeforeVisibility_SoTheTargetsAccessIsNotRevealed`.
- ~~MCP `run_workflow` lets `RunAccessDeniedException` escape as a protocol error.~~ — DONE 2026-09-09: `run_workflow` catches it and returns a tool error like `run_configuration`.
- Push-policy gate is a regex over the shell text (quote-splitting or `$(echo push)` bypass to
  the global mode); the scoped push token remains the real boundary.
- `PluginManifestVerifier` verifies against the key inside the manifest (integrity only, no
  publisher pinning); `WorkflowPackageVerifier` extracts zip entries absent from
  `manifest.Files`.
- ttyd runs `--writable` with no credential on the host loopback; the Core ticket is the only
  auth.
- ~~`Source/Platform/Auxilia.AdminConsole` has no `*.project-instructions.md` (Rule 0).~~ — DONE 2026-09-09: `Auxilia.AdminConsole.project-instructions.md` written (purpose, auth/token relay, invariants, file map).

### Decisions needed
- **Catch-up cursor seed + overlap (Workflows.Client engines).** The seed is the HOST clock at consumer start and the catch-up bound is `lastSeen − 1 s`; a host clock more than 1 s ahead of the Core skips gap items, a host clock behind only re-reads (deduped). Options: (a) keep as is (recommended — zero Core change, documented assumption); (b) change the Core's `createdAfterUtc` to `>=` and seed from a Core-side "server time" header; (c) widen `CatchUpOverlap`. Recommendation: (a).

## Core.Api — client-surface follow-ups
- ~~Per-user bearer hardening~~ — DONE 2026-08-03: the raw bearer no longer rides the
  prerendered page. The relay stashes it server-side (`UserBearerHandleStore`, singleton) and
  persists only a cryptographically random ONE-SHOT handle; the circuit redeems it exactly once,
  unredeemed entries expire after 2 minutes and never outlive the token. No raw-token fallback.
- ~~AdminConsole step-up prompt~~ — DONE 2026-08-03: `elevation-required` now opens an inline
  re-authentication panel on the Principals page; the successful step-up retries the pending
  mutation (the elevation header rides the circuit's client).
  ~~AdminConsole tags administration UI~~ — DONE 2026-08-05: the Principals page carries a
  Tags column (chip per tag with remove, inline add) over `SetPrincipalTagsAsync`, riding the
  same mutation/step-up seam as the other principal actions.
- ~~Environment catalog search~~ — DONE 2026-08-05: `GET /api/environment-layers` and
  `GET /api/environment-bases` accept `?search=` (case-insensitive substring over
  type/name, version(s), base, description), wired through `ICoreClient` and covered by
  component + client-surface system tests.
- ~~External-consumer gap wave~~ — DONE 2026-08-16 (found by the out-of-repo
  `Auxilia.Example` exercise): artifact queries (REST + MCP `list_artifacts`, which also
  gained a `runId` filter) resolve the dispatch↔instance id alias like every other run
  read; `RunStreamEvent.AsStatus()/AsView()` + `RunStates` constants give clients typed
  stream decoding (and the stream stops leaking the Core-internal `TerminalEndpoint`);
  `WorkflowAuthoring` kind-validates input values, refuses per-run inputs in stored
  configurations, and `ConfigureAndRunAsync` carries a per-run context; a schemaless
  `docker://` registration says so in its status reason, the registry UI warns the
  approver, and the SDK ships `IRunInputs` (typed, declared-name-checked input access) +
  `WorkflowInputKinds`; the SDK trio (`Auxilia.Workflows`/`Messaging`/`AI`) is packable at
  0.2.0 with family READMEs.
- **Workflow-type registry administration** — full client surface exists on `ICoreClient`
  (register/approve/deny/unregister), and the steering client ships a registry-administration panel
  (2026-08-02, permission-gated on `workflow-type.manage`/`workflow-type.sign`).
  ~~AdminConsole registry UI~~ — DONE 2026-08-03: `/admin/workflow-types` page (list with
  status/tags/lifetime, registration detail, register by package URI, approve/deny-with-reason
  gated on `workflow-type.sign`, enable/disable/unregister-with-confirm gated on
  `workflow-type.manage`; nav entry appears with either permission). No step-up panel: the Core
  does not elevation-gate registry mutations (only principal admin does).
  ~~AI safety-check approval handler~~ — DONE 2026-08-05 as the semantics-blind
  `verdict-workflow` pipeline handler: `CoreApi:ApprovalVerdictWorkflow` names an ACTIVE
  workflow type that is dispatched over each pending registration (context:
  `approval-workflow-type` / `approval-package-uri` / `approval-publisher-key` /
  `approval-registered-by`); the handler awaits the run and applies its `approval-verdict`
  artifact (`{"decision":"approve|deny","reason":…}`). Everything inconclusive (unconfigured,
  run failed, timeout, no/bad verdict) DEFERS to the human signing authority. The safety-check
  WORKFLOW itself (the AI review over the package) is authored like any other workflow —
  nothing Core-side remains. ~~Note: a `core://`-stored pending package is not yet fetchable by
  the verdict run (no download authorization path); https/docker coordinates work today.~~
  DONE 2026-08-08: the handler mints a scoped download token
  (`PendingPackageDownloadTokenService`, HMAC, bound to the one pending type, valid for the
  evaluation window) and passes `approval-package-download-url` into the verdict run's context;
  the package endpoint honors it only while the type is still Pending.
- **Session terminal remainders** — ~~a console-mode Docker system test (stub CLI under tmux)~~
  DONE 2026-08-07: `EndToEnd/ConsoleSessionTerminalSystemTests` drives the whole loop on real
  Docker — console-mode dispatch (dwelling stub under tmux+ttyd,
  `WorkflowLauncher__TerminalPublishMode=container-network`), `HasTerminal` during the session,
  401 ticketless, ticket mint + proxied ttyd page + path-scoped cookie, run Success, and a
  400 on post-terminal ticket minting. ~~Remaining (found by that test): **first-dispatch schema
  gap** — the runner's terminal decision reads the schema a workflow instance self-registers at
  startup, so the FIRST run of a freshly registered type on a fresh runner silently loses its
  terminal (statically registered types carry no schema Core-side either). The test warms the
  store with one headless run; the product fix is propagating the registry's inspected schema
  to runners at registration/approval time instead of first-run.~~ DONE 2026-08-08 (dispatch-time
  propagation — covers runners that boot after registration, which a registration-time fanout
  cannot): `RunWorkflowCommand.SchemaJson` carries the registry's inspected schema; the runner's
  dispatcher reads its store first and falls back to the command seed (persisting it), so the
  first dispatch decides terminal/network/repository questions from the registry schema.
  Unit-tested on both sides (`WorkflowDispatcherSchemaSeedTests`, `RunServiceTests`).
  ~~AdminConsole terminal surface~~ — DONE 2026-08-05: RunDetail shows "Open terminal" for a
  live terminal-hosting run (`RunStatus.HasTerminal`), mints the short-lived ticket via
  `OpenTerminalAsync`, and opens the Core's ticketed proxy URL in a new tab — the browser
  only ever talks to the Core.

## Platform events + event triggers — delivered 2026-08-08, open remainders
Delivered (ARCHITECTURE §6 "Platform events", workflow-sdk-design "Platform events"): the
`workflow.events` topic exchange + `WorkflowEventMessage`; SDK `DeclaresEvent`/`IEventPublisher`
(reserved `run.` prefix, 64 KB payload cap); Core mirror (`CoreEventRecord`,
`EventTrackingService` + retention sweep), filtered `GET /api/events(/stream)` gated
`event.consume`, `RunLifecycleEventPublisher` (terminal states → `run.*` events, deterministic
ids); `ICoreClient.QueryEventsAsync`/`StreamEventsAsync`; `EventTriggerDefinition` +
`EventTriggerEngine` in `Auxilia.Workflows.Client` (hosted by TriggerHost automatically).
System tests DELIVERED same day: `Messaging/MessageBusTopicRoutingSystemTests` proves the
per-type `workflow.events` bindings on real RabbitMQ (incl. wildcard-neutralized type names
never widening a binding); `CoreClientSurface/EventSurfaceSystemTests` drives the full loop
against the real Dockerized Core — SDK publisher → topic routing → mirror/filtered SSE →
`EventTriggerEngine` → follow-up run in a real container → its `run.succeeded` lifecycle event
(deterministic id, exactly one).
Open:
- ~~`run.*` spoof-hardening~~ — DONE 2026-08-08: both ingest paths (`EventTrackingService`,
  `EventStreamPublisher`) drop reserved-prefix events whose id is not the platform's
  deterministic (run, type) id (`RunLifecycleEventPublisher.IsAuthentic`). Residual risk: a
  spoofer computing the deterministic id for a run that never reached that state can still
  plant one false event — full proofing needs per-container bus credentials (bigger program).
- ~~Run-lifecycle events carry no `WorkItemId`~~ — DONE 2026-08-08: `RunLifecycleEventPublisher`
  enriches each `run.*` event best-effort from the Core's own run record (dispatch context key
  `WorkItemId` in the stored `DispatchCommandJson`, addressable by instance OR command id);
  anything missing/malformed yields empty and never fails the translation, and the
  deterministic event id is untouched (re-delivery may enrich differently — the mirror upsert
  stays idempotent).
- ~~MCP parity~~ — DONE 2026-08-08: `query_events` MCP tool (gated `event.consume`; type /
  work-item / source-run filters, newest first).
- ~~AdminConsole event browse UI~~ — DONE 2026-08-08: `/events` page (filterable table with
  platform-chipped `run.*` rows, expandable payloads, source-run links, follow-live over the
  filtered SSE stream; nav gated `event.consume`) + a RunDetail events strip
  (`SourceRunId == run`, refreshed after terminal). Verified live against the dev stack.
  **Trigger administration is deliberately NOT in the console**: trigger definitions are
  host-side (`ITriggerStore`), the Core stays semantics-blind and stores none — an admin UI
  would first need a TriggerHost REST surface (undecided).

## Governance + signing follow-ups (2026-08-08 console review)
- **Policy allow-once anomaly (2026-08-15, unexplained)**: the E2E mail-trigger dispatch
  (on-behalf-of a plain User, ungranted type, restricted default) was correctly denied
  `workflow-type-default-restricted` in every isolated run — but in one full-suite run the
  FIRST dispatch went through and completed before later polls were denied. Suspect the
  principal/role cache serving a wrong entry right after seeding. The E2E now grants the
  run-as principal trigger access explicitly (the governed path, also the fix for the suite
  being broken since the default-deny wave), which hides the anomaly — worth a targeted
  look at `PrincipalRoleCache` freshness right after principal creation.
- ~~MCP parity for the newer admin surfaces~~ — DONE 2026-08-08: `set_provider_grants`
  (slot providers AND environment layers — one catalog mechanism), `list_platform_settings`,
  `set_platform_setting` (elevation-gated via the EXISTING MCP `step_up` flow, matching the
  REST posture — no read-only compromise was needed), `list_environment_bases` /
  `upsert_environment_base` / `delete_environment_base`, and `list_runners`. All ride the
  same service layer as REST (the component-tested seam), each policy-checked.
- ~~Default-deny resource access~~ — DONE 2026-08-08 (ARCHITECTURE "Default-deny resource
  access"): empty grants now follow the runtime setting `security.default-resource-access`
  (`restricted` = administrators only, the default incl. unset; `open` = the old role-governed
  behavior), enforced at the catalog dispatch gate (`RunService`) and the Policy Engine's
  workflow-trigger fall-through. Administrators always pass (empty AND non-empty lists);
  system dispatches without a principal are exempt; only trigger/bind is posture-gated —
  cancel/observe/approve stay role-governed. The runner's pre-flight re-check deliberately
  evaluates the OPEN posture (the Core owns the setting and already gated the dispatch).
  AdminConsole: `/admin/settings` page (posture + login lifetime, step-up-gated), ungranted
  layers/providers show "admin-only (default)" instead of "everyone". Covered by
  PolicyEngineTests, ResourceGrantsTests (incl. the new restricted-default roundtrip),
  PlatformSettingsTests, and bUnit console tests.
- ~~Environment rights~~ — DONE 2026-08-08: `EnvironmentLayerRecord.GrantsJson` +
  `PUT /api/environment-layers/{type}/grants` (gated `provider-catalog.manage`,
  `ICoreClient.SetEnvironmentLayerGrantsAsync`); empty grants = open, non-empty enforced at
  dispatch when the layer is bound (403 via `RunAccessDeniedException`); upserts preserve
  grants. Component-tested (`ResourceGrantsTests`). ~~AdminConsole grant-editing UI~~ —
  exists (the Sharing drawer on `/admin/environments`), posture-aware since 2026-08-08.
- ~~Workflow-type rights~~ — RESOLVED 2026-08-08: the mechanism ALREADY existed — the Policy
  Engine's per-(type, action) EXCLUSIVE access list (`WorkflowTypeAccessStore`, governance-rbac
  design), enforced at every dispatch — it was just administrable via MCP only. Now exposed
  over REST (`GET/POST /api/workflow-types/{type}/access[.../grant|/revoke]`, gated
  `policy.administer`) and `ICoreClient`
  (`ListWorkflowTypeAccessAsync`/`Grant…`/`RevokeWorkflowTypeAccessAsync`), component-tested
  incl. exclusive dispatch enforcement (`ResourceGrantsTests`). No second grant mechanism was
  added. ~~AdminConsole access-editing UI~~ — exists (`WorkflowTypeAccessEditor` in the
  registry detail row), default-posture-aware since 2026-08-08.
- **Signed-package roundtrip SYSTEM test**: DONE —
  `CoreClientSurface/SigningRoundtripSystemTests` drives the loop over the real Dockerized
  Core: trusted-key upload → auto-Active → dispatch (runner token-downloads the `core://`
  package and passes verification), untrusted upload → Pending → approve → platform re-sign
  (re-signed package verifies at dispatch), tampered upload refused, and an externally hosted
  package swapped post-registration fails its run at the runner's signature verification.
  (Executing a zip payload stays uncovered: the containerized runner's extraction path is not
  daemon-resolvable — see the fixture doc.)
- **Docker packages: pin the digest at approval.** A `docker://` image has no verifiable
  package signature, so trust is the approval act — but the approved TAG is mutable: it can be
  repointed after approval and the runner would run different code under the approved name.
  Resolve the tag to `docker://image@sha256:…` at approval time and dispatch by digest.

## Hardening wave 2026-08-04 — dispatch truth, container re-adoption, stream/UI resilience
Delivered (see ARCHITECTURE §6/§14.2/§15): dispatch-time `Dispatched` run record + claim-timeout
sweep (`dispatch-never-claimed`) + rekey-on-claim + terminal sink; SSE snapshot-first-frame +
keepalives + `createdAfterUtc` artifact catch-up filter; resilient `Auxilia.Core.Client` streams
(`ClientStreamFrame` union, reconnect/backoff/idle-timeout/dedupe inside the client, unary
timeouts replacing `HttpClient.Timeout` — fixed the latent 100s stream-death); AdminConsole
`PagePoller`/`ConnectionBanner` (poll loops survive transport errors; RunDetail reconnects and
refetches; fixed the camelCase payload-decode bug); `ArtifactChainingEngine` catch-up + dedupe;
runner **container re-adoption** on restart (persisted container id + protected instance token,
re-claim before the failover clock, real exit collection, clean-kill fallback,
`ReadoptContainersOnStart` replaces `ReapWorkflowContainersOnStart`). Remaining follow-ups:
- **System tests (Docker + real RabbitMQ)** for the wave — PARTIALLY DONE 2026-08-05: the new
  `CoreClientSurface` fixtures cover the command-id-keyed SSE subscriber over real topic
  routing, snapshot-first, keepalive survival past 100s idle, and reconnect across a real
  Core.Api container restart. They immediately caught and fixed five real defects the fake
  bus can never show: (1) topic-binding RPCs shared the consumer's channel and could wedge it
  (bindings now ride a dedicated channel); (2) the run-stream publisher awaited the bind gate
  on the bus dispatch path (alias binds now drain through a worker); (3) a claim event beating
  a fresh command-keyed subscription orphaned the stream FOREVER — a periodic sweep now
  re-resolves unpaired command-id audiences from the run store and replays the record's
  current state, including terminal states (keepalives otherwise hold the orphan open);
  (4) the client's idle-timeout ABANDONED the in-flight read before disposing the response,
  which could hang the next resubscribe (the read is now properly cancelled); (5) a cancel
  racing the workflow's startup was published UNROUTED and silently dropped — the runner now
  declares the instance cancel queue before publishing, so the "hard stop" parks instead of
  vanishing. ~~Runner kill/restart, exit collection, clean-kill~~ — DONE 2026-08-05, all
  passing on real Docker: `ReAdoptionSystemTests` (runner restart mid-run → the SAME instance
  is re-adopted and completes; container SIGKILLed while the runner is down → Failed with the
  REAL exit code 137; a labeled container no record knows → clean-killed on restart) and
  `DispatchTimeoutSystemTests` (runner heartbeating but not consuming → `Dispatched` visible
  immediately, swept as `dispatch-never-claimed`).
- ~~Live verification against the dev stack~~ — superseded 2026-08-05: every scenario is now
  an automated system test (see above; mid-run Core restart → snapshot resume was already
  covered by the `CoreClientSurface` reconnect fixtures). Residual: a one-glance visual check
  of the RunDetail banner during a real Core restart — falls out of normal dev-stack use.

## Implementation workflow  ·  see `docs/implementation-workflow-design.md`
- **Real Copilot console events** — the Copilot CLI has no hooks; it does support
  `--log-dir`/`--log-level`. Run one real authenticated console session, inspect the log
  shape, then build the tail-based source feeding the existing `CopilotConsoleBridge` wire.
- **First real end-to-end run** against a live Azure DevOps story (requires a real tenant;
  the mocked-HTTP and simulated paths are verified).
- **Context management across shared stages** — summarization-on-demand when the window gets
  tight (today only the gate-idle `/compact` rule); a THIRD agent binding for cross-provider
  stage mixes beyond author+reviewer.
- **Externally configured MCP servers** passed into agent sessions (today only the
  workflow-hosted work-item server) — deferred, complicated.
- **Real-CLI verification** of the workflow-hosted work-item MCP server registration
  (Claude `.mcp.json` / Copilot `mcp-config.json` paths are unit-verified only).

## Coding-agent sessions — open verification
- **Copilot SDK session against the real service** — `CopilotSdkAgent` and the
  session-vocabulary (model + reasoning-effort) path are built and mock-verified; a live run
  needs a Copilot-entitled token.
- **Unexercised interactive paths** — an ask-mode permission card with suggestions, and a
  real-CLI push interception (unit/component-verified; needs a push-enabled repo binding).
- ~~Push-scoped token~~ — DONE 2026-08-05: connectors may carry an optional `push-token`
  secret (declared on `tfs-account`; any git-credential connector setting keyed
  `push-token`/`pushToken`/`push-pat` is honored — the resolver is key-based, no vendor
  logic). For an `AllowPush` mount the runner clones with the full credential and rewrites
  the container-visible origin to carry only the push-scoped token (ARCHITECTURE §9
  write-back control). Without a push token, behavior is unchanged.
- ~~Per-mount working directory in the agent context~~ — DONE 2026-08-02: a single mount's
  `Workflow__WorkspaceMount__<ID>` root (working-directory subpath included) is now the
  authoritative cwd in all three agent workflows; the repository slot's `WorkingPath` remains
  the seam for mount-less providers, and multi-mount runs fall back to the workspace root.

## Client libraries & packaging
- ~~Publish the NuGet packages~~ — DONE 2026-08-06: all four packages
  (`Auxilia.Core.Contracts`, `Auxilia.Core.Client`, `Auxilia.Workflows.Client`,
  `Auxilia.Steering.Codec`) are live on **nuget.org** at v0.1.0 (+snupkg), published from
  tag `v0.1.0` via Trusted Publishing (policy "Auxilia-Publisher", nuget.org user `FelixK`,
  activated by first use). Follow-ups — both closed: (1) ~~the dead tag-PUSH trigger~~ —
  RESOLVED 2026-08-07: it was the GitHub Actions **major outage of 2026-08-06** (event-triggered
  runs never created platform-wide; `workflow_dispatch` worked). After the incident cleared, a
  probe workflow confirmed both branch- and tag-push events fire again (probe tag + workflow
  deleted); the `v*` trigger will run on the next version tag. (2) ~~PackageReadmeFile
  readmes~~ — DONE 2026-08-06: all four packages republished as **0.1.1** with gallery readmes.
- ~~Steering codec extraction~~ — DONE 2026-08-05: `Auxilia.Steering.Codec` is the
  dependency-free wire-protocol library (typed `SteeringFrame` records + tolerant
  `SteeringCodec.Encode/Decode`); `OperatorChannel` and `ConsoleEventViews` now speak it
  instead of private wire records (wire JSON unchanged — locked by literal protocol tests),
  and desktop clients can reference it without the full contracts surface.
- **Go-public pre-flight** (repo is otherwise publish-ready: rewritten noreply-only history,
  single `main`, licenses + pricing incl. free personal tier; licensing contact is EMAIL —
  a public issue would expose the inquirer's company details). **Mailbox VERIFIED 2026-08-06
  (Felix confirmed the mail loop works; the flagged felix.klakow.github@gmail.com account was
  recovered 2026-08-03). Remaining gate: Felix explicitly decides to publish — do NOT flip
  public before that go.** (Pre-flight re-verified 2026-08-03: single `main`, noreply-only
  history, no product-external names in tracked files, no real secrets — only fake test
  tokens; `Start-Presentation.bat` is already retired and the stale "via Studio" DevStand
  string is fixed.) Then: optionally ask GitHub Support
  to GC the pre-rewrite objects, then flip the repository public. On flipping: set the repo
  description ("Self-hosted platform for governed AI agent workflows — signed containers,
  just-in-time scoped credentials, live operator steering, full REST + MCP parity") and
  topics (ai-agents, agentic-ai, workflow-engine, ai-orchestration, coding-agent,
  claude-code, mcp, model-context-protocol, self-hosted, dotnet, csharp, aspnetcore,
  blazor, rabbitmq, docker), register `.github/workflows/publish-nuget.yml` as the
  nuget.org Trusted Publishing workflow (tag-triggered `v*`; verify the nuget.org
  username in the workflow's `user:` input), and walk the repo-settings checklist:
  enable Discussions + private vulnerability reporting (SECURITY.md points there),
  Dependabot alerts, secret-scanning push protection; restrict Actions to the two
  used action publishers + read-only default GITHUB_TOKEN; a main ruleset blocking
  force-push/deletion with admin bypass; disable Wiki/Projects.
- ~~Steering-client desktop per-user sign-in~~ — DONE 2026-08-03 for the password path: the Core's
  `POST /auth/login` (username+password → the same per-user bearer the browser mints, desktop
  lifetime `CoreSecurity:LoginTokenLifetimeMinutes`, default one workday, audited both ways)
  plus the steering client sign-in overlay/header buttons (never a silent fallback to the service key
  once in user mode). Rate limiting on /auth/login DONE 2026-08-03: per-client-IP fixed window
  (`CoreSecurity:LoginRateLimitPermitsPerMinute`, default 5) plus a per-username failed-attempt
  throttle (`LoginFailureLimitPerUsername`/`LoginFailureWindowMinutes`, defaults 5/5) that a
  successful sign-in clears; excess attempts get a 429 and an `auth.login` rate-limited audit.
  Still open: the device-code/OIDC variant for Entra-only principals (SSO-provisioned humans
  have no password).

## Config store — DECIDED 2026-08-01: stays in the Core
The earlier "move the config store out of the Core" direction is reversed by decision, not
inertia: the Core is the one shared authority every client reaches, so Core-resident
configurations give cross-device/multi-client sharing (steering client on any machine, AdminConsole,
MCP agents see the same list) without inventing a product-side persistence service. This does
not violate semantics-blindness — a stored configuration is an opaque, schema-validated
document: the Core validates it against its schema registry, stores it, dispatches it via the
Run API, and never interprets what the workflow means (the same storage-vs-semantics division
as artifacts). **Per-configuration ownership + sharing DELIVERED 2026-08-01**: configurations
carry scope (Personal default, Company opt-in gated by `workflow-configuration.manage`), an
owner, and `AccessGrant`s (principal / first-class group / directory group — the same shared
model as connectors, evaluated by `AccessGrantEvaluator`); reads and the run path are
visibility-filtered, editing/deleting/sharing is owner-or-manager; surfaced in the
AdminConsole (scope column + Sharing editor) and the steering client (visibility choice in the
configure wizard, Sharing… on the workflow row menu). Group entries in workflow-type
access lists, `GET /api/roles`, and the `GET /api/directory/subjects` sharing directory
(pickers in both grant editors) shipped 2026-08-01; access lists are administered over MCP
(`list/grant/revoke_workflow_type_access`, gated `policy.administer`).

## Environment capabilities
- **Windows-container runners** — windows-base layers are stored but never served to Linux
  composition. (Mixed-base selections now fail fast at dispatch — 2026-08-01 — and each
  layer's base rides its catalog entry.)
- **Trust keys in real deployments** — `WorkflowDispatcher__TrustedEnvironmentSigningKeys`
  is empty (permissive) in dev; any real deployment needs the key material story.
- ~~Versioned bases~~ — DONE 2026-08-03 (ARCHITECTURE "Versioned bases"): admin-managed
  environment-base catalog ((name, version) pairs, `/api/environment-bases`, provider-catalog
  gated, full client surface), layers optionally pin a registered base version, the pin rides
  the catalog entry, and dispatch + authoring fail fast on mixed base versions like mixed
  base names. Capability-side versioning needs no mechanism — the open provider-type
  vocabulary already carries it (`dotnet-10` / `dotnet-8` are distinct capabilities).
  Remaining idea (unbuilt): bases carrying a concrete image reference the composition could
  `FROM` — today composition always builds FROM the workflow image, so an image ref on the
  base only becomes meaningful with pre-built environment containers.
- ~~Base-catalog prefill~~ — DONE 2026-08-08 (ARCHITECTURE "Fleet platform advertisement +
  base seeding"): NO online registry lookup by design (registry neutrality, air-gapped
  deployments). Delivered instead: (a) the console's curated seed list on an empty base
  catalog (one-click add, admin stays the curator), and (b) runners probe their Docker
  daemon (OS type + arch, cached; the daemon's answer, not the process OS) and advertise it
  on every `RunnerHeartbeat` — tracked per runner, served over `GET /api/runners` +
  `ICoreClient.ListRunnersAsync` (gated `provider-catalog.manage`), and used to filter the
  seed suggestions to what the fleet can host. Checking "which docker bases are there" only
  becomes meaningful when bases carry a concrete image ref (idea above) — then the RUNNER
  validates by pull at composition, never the Core.
- **Automated base-image download (open, 2026-08-08 — depends on bases carrying an image
  ref).** Runner-side reconcile, never Core-side: each runner compares the registered bases
  for its platform against its local Docker images and background-pulls what's missing
  (triggered by a catalog-change bus message plus a periodic sweep as catch-up), pinned by
  digest at registration so a repointed tag can't swap the code. Pull auth, if a private
  registry needs it, is runner-local configuration (the Core stores no registry credential
  and makes no registry calls). The heartbeat reports per-base readiness so the console can
  show "ready on N/M runners" and surface pull failures; air-gapped deployments load images
  by hand and the reconcile simply finds them present. Until then, nothing to download:
  composition builds FROM the workflow image, which the runner already pulls on demand.
- **Build-time hardening (context, 2026-08-02).** Environment composition runs `docker build`
  on a generated one-file Dockerfile (context = that file only; nothing from the host leaks
  in). The RUN steps execute in ordinary build containers: root inside, NO egress policy
  (the daemon's default build network — layers must download packages), no resource caps.
  The protection is deliberately WHO may author (admin-only + Core signature, verified by
  the runner before the build) rather than what the build may do. Future levers if needed:
  pin the build's NetworkMode to a network that reaches only the package proxy, and pass
  memory/CPU caps in `ImageBuildParameters`.

## Test fabric & agent swarms — P0 implemented, rest designed
See `docs/test-fabric-and-swarm-design.md`. **P0 (declared run pods) IMPLEMENTED 2026-08-15**:
SDK `RequiresCompanion` (+ scale/start-order/pod-volume/placeholder surface), `Companions` in
the signed schema/manifest, registry-gate structural validation (digest pinning, cycles) +
spawn summary (`WorkflowSchemaDto.Companions`/`MaxPodContainers`), runner materialization
(per-run `--internal` pod network, DAG start gated on readiness, counts clamped to signed
bounds, run-minted secrets, `Workflow__Companion__*` announcements, pod volumes under
`/workspace/pod`), teardown on every terminal path (state handler, crash-exit, re-adoption,
orphan sweep) with `companion-log-<name>` artifacts. Unit+component covered. Open:
- **In-network readiness prober** — readiness today = inspect-based (Running + Docker health
  if defined); a declared TCP/HTTP probe needs a prober container on the pod network.
- ~~Docker system tests~~ — DONE 2026-08-15: `PodFabric/PodScenarioSystemTests` runs the
  design doc's WHOLE worked example on the real daemon: pod of rabbit + fleet-manager +
  coordinator + input-scaled machines on the per-run `--internal` network, runtime
  spawn/stop through the pod controller (configuration-pinned base), availability handshake
  2→4→3 asserted via the coordinator, `logs.zip` (from the shared pod volume) + `report.json`
  artifacts verified BY CONTENT, every companion's `companion-log-*` artifact present
  (incl. the runtime-spawned machine; the mid-run-stopped one correctly absent), and pod
  containers/network gone after the run. Fixture trick: a throwaway `registry:2` with a
  DAEMON-assigned port mints real digests for in-test-built companion images (push only —
  the local RepoDigest makes `name@sha256:…` inspectable without any pull). Isolation is
  asserted too (2026-08-16): mid-run the pod network must be `--internal`, and the
  coordinator's egress prober dials REAL TCP from inside the pod — a pod peer answers, the
  platform RabbitMQ and the internet must be CLOSED (unit side: network-create params +
  companions expose no host ports). Re-adoption across a runner restart is covered too
  (2026-08-16, `PodReadoptionSystemTests`): a parked pod-controlled run survives a real
  stop/start and performs its FIRST runtime spawn only afterwards — green.
- ~~Envelope-clamp semantics~~ — RESOLVED 2026-08-16: the envelope caps **runtime spawns
  only** (Felix's call). Runtime companions carry `auxilia.companion-runtime`; the clamp
  and pod-control stop filter on it, so declared templates neither consume the envelope
  nor can be stopped mid-run — total = templates + envelope, exactly as the spawn summary
  advertises.
- ~~Spawn-summary surfaces~~ — DONE 2026-08-16: the registry page's detail row renders a
  prominent warn-panel spawn summary (per-run container cap, declared-companion table with
  scale/start-order/resources, the runtime envelope + purpose + pod volumes); MCP's
  `get_workflow_schema`/`approve_workflow_type` descriptions call the summary out as the
  thing an approval permits.

**POD CONTROLLER also IMPLEMENTED 2026-08-15** (the runtime-spawn half): environment bases
optionally carry a digest-pinned `ImageReference` (validated at upsert — closes the old
"bases carrying a concrete image reference" idea); the run's CONFIGURATION pins its
spawnable subset via context key `pod-bases`, resolved by `RunService` into
`RunWorkflowCommand.PodBaseImagesJson` (default-deny without a selection, snapshot survives
failover redispatch); SDK `RequiresPodControl(max, description, podVolumes)` +
`IPodController` (`PodControlClient` over the authenticated `workflow-pod-control` queue,
resource-proxy pattern); runner `PodControlHandler` + `PodControlRegistry` (base map +
envelope clamp on the live RUNTIME-SPAWNED count, declared-volume-only mounts, per-op audit, refusals
audited); `DockerPodHost` spawn/stop/count; pod network+volumes materialize at launch even
without declared companions. Unit/component covered end to end. Open beyond the shared
items above:
- ~~Runner-restart residual~~ — CLOSED 2026-08-16: re-adoption REBUILDS pod-control state
  (envelope from the schema store, base map from the persisted dispatch command,
  network/volume names re-derived via `PodPlanner`) — no new persistence needed. The system
  test also exposed a startup ordering bug, fixed: re-adoption now runs BEFORE any bus
  handler starts, else token-validating handlers reject messages from legitimately-running
  workflows in the restore gap.
- **`pod-bases` UX**: the context key works through any dispatch surface today; a dedicated
  configuration-editor picker (granted bases only) is unbuilt, as are grants ON bases
  (currently: catalog entry = admin-curated, selection = configuration).
- **P0.5 remainder (design only)**: layer-composed companions + setup scripts, declared
  tunnels, pod-profile catalog; then the swarm primitives (run groups, fan-out/join,
  `workflow.swarm` channel).

## Generic binding pipeline
- **Persistent workspaces — reset semantics (design note, 2026-08-03).** Today nothing
  persists per-run: every run gets an isolated copy materialized from the host-only warm
  repo cache (never bind-mounted) and the run root is deleted at terminal state, so
  cross-run leaks are impossible by construction. If a persistent/reusable workspace ever
  becomes first-class, the leak-proof reset is *re-materialization* (discard the run copy,
  copy afresh from the warm cache), not in-place `git clean`/`reset` — untracked/ignored
  files and hook side effects make in-place cleaning a leak channel. Any persistent
  workspace must be scoped to one owning identity (never shared across principals).
- ~~Post-binding setup scripts~~ — DONE 2026-08-03 (ARCHITECTURE §9): manifest-declared
  `RequiresRepository(..., setupScript:)` plus the mount-bound `setup-script` role; the runner
  announces (`Workflow__WorkspaceMountSetup__<ID>`), the SDK executes inside the container in
  the mount's root before the application, fail-fast, under the run's egress policy. ~~Open: a Docker system
  test exercising a real in-container setup script~~ — DONE 2026-08-07 (and the entry was
  stale: the mount-bound role was already covered by the per-run repository Docker test).
  `RepositoryWorkspaceSystemTests` now additionally proves the empty-workspace + setup-script
  combination end to end (script seeds the scratch directory before the application) and the
  fail-fast contract (script `exit 7` → the run Fails before the application runs).

- ~~Temporary/empty workspaces~~ — DONE 2026-08-03 (ARCHITECTURE §9): the `empty-workspace`
  provider — a data-only catalog descriptor (`mountsIntoWorkspace`, `working-directory` +
  `setup-script` roles, no credential contract; seeded by `Start-DevStack.ps1`) plus the
  runner's non-git materializer: a mount without a `clone-url` role becomes a fresh scratch
  directory under the same `repos/<mount-id>` layout, working directory pre-created, deleted
  with the run root at terminal state (cleanup was already materializer-agnostic). Nothing in
  the Core changed. Deliberately left out: artifact seeding — no natural fit yet (seed via the
  setup script for now; revisit with a dedicated artifact-input role if needed).

## CI validation — Docker system tests (not runnable locally)
- ~~Email slot **plugin-dependency loading**~~ — DONE 2026-08-07 (and runnable locally after
  all): `EndToEnd/EmailPluginDependencySystemTests` dispatches a run bound to the real
  `email-work-items` slot against GreenMail — the bundled MailKit/MimeKit/BouncyCastle closure
  loads inside the workflow container and the SMTP reply (with the `[work-item: …]` marker)
  lands back in the mailbox.
- ~~`/api/audit` response shape~~ — DONE 2026-08-05: `AuditEndpoint_ServesCamelCasePagedResult`
  in the CoreApiDispatch suite (passing locally).
- (2026-08-02: the whole system suite runs locally again. The Failover test reads the owner
  from the claim transition per the preserve-last-non-null contract; the legacy
  `CodeReviewWorkflow` fixture — dead since the seed-subsystem removal in `d582b5d` — was
  replaced by `EndToEnd/CodeReviewDispatchSystemTests` covering inline
  `ProviderType`+`Settings` bindings for happy AND write-back-failure runs; the ClaudeCode
  test dispatches `permission-mode=auto-allow`, since the steering-era ask-operator default
  otherwise blocks an autonomous run on a permission card — that was the "hang".)

## DevStand
- ~~`ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot
  `Auxilia.AdminConsole` in `EndToEndEnvironment`.~~ (DONE 2026-08-03: the AdminConsole now
  runs as a container in `EndToEndEnvironment` — new
  `Source/Platform/Auxilia.AdminConsole/Dockerfile`, `auxilia-admin-console:system-test`
  image, `Core__ApiKey` = a step-up-granted Administrator service key so pages render fully
  authenticated without a browser session — and the harness captures the console's twelve
  pages (`01-dashboard.png` … `12-audit.png`, dashboard live while the mail-triggered run is
  Running); the dev stand prints/opens the console URL again.)
- ~~AdminConsole under `dotnet run` serves broken static assets~~ — FIXED 2026-08-05: the
  project had no `launchSettings.json`, so `dotnet run` started in the Production
  environment where the static-web-assets dev manifest is never loaded — the Debug
  runtime-patching handler then 500'd on packaged `_content` files and served 0-byte
  `app.css`. A Development launch profile (`https://localhost:7299;http://localhost:5299`)
  fixes `dotnet run`; published output was and stays correct. Bonus hardening from the same
  investigation: `CoreBackedAuthenticationHandler` now degrades to anonymous instead of
  500-ing every request (static assets included) while the Core is unreachable.

## Core scalability — path to ~100k simultaneous clients
The shape is right (stateless Core.Api, SSE per node, competing-consumer runners, clients
never on the bus); the 2026-08-01 wave delivered shared-queue tracking mirrors, the auth
cache, signed terminal tickets, and the batched audit writer. **Selective event routing is
DELIVERED (2026-08-02)**: `workflow.status`/`workflow.views`/`workflow.artifacts` topic
exchanges (new names — the retired fanouts could not be redeclared), routing key stamped at
publish (status = `{instanceId}` or `{instanceId}.{commandId}` on the claim; views =
instance id; artifacts = sanitized artifact type), per-node dynamic bindings driven by the
SSE brokers' binding listeners (subscriptions gained Add/RemoveBinding; awaited on subscribe
so the no-event-lost-after-flush guarantee holds), command-id aliases resolved from the run
store for late subscribers, tracking mirrors bind `#`. ASB mapping (topic subscription
rules) remains a note for the ASB backend. Remaining:
- Infra per the ARCHITECTURE §14.4 table (RabbitMQ cluster / ASB, MongoDB backend). Note:
  100k clients ≠ 100k concurrent runs — the run axis is runner-fleet/container capacity plus
  heartbeat/failover-monitor volume, tracked separately.

## Deeper platform consolidation (from the separation plan)
- Core.Api + Core.Runner shared **Core DB tier** (currently separate DBs; several features
  bridge the split over the bus).
- **Endpoint-granular network-policy enforcement** — the resolver computes per-run
  `AllowedEndpoints` and the launcher realizes only the no-egress case at the Docker level
  (`--internal` network); per-endpoint enforcement needs the future EGRESS PROXY (a per-run
  HTTP(S)/DNS forward proxy the container's only route points at, filtering on the allowed
  list) — a design of its own, not an increment.
- ~~Run-API quotas~~ — DONE 2026-08-05: `CoreApi:RunQuotas` — `MaxActiveRuns` (platform-wide
  cap on non-terminal runs) and `MaxDispatchesPerPrincipalPerMinute` (per-principal fixed
  window; system dispatches without a principal — failover redispatch, the approval
  pipeline — are exempt). Enforced at the `RunService` dispatch chokepoint (inline, stored
  configuration, and rerun paths), rejected with 429 + a `run.quota-exceeded` audit entry;
  0 = unlimited (the default).
