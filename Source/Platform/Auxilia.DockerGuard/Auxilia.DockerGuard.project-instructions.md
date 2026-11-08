# Auxilia.DockerGuard

The Docker API guard (image `auxilia-docker-guard`): deployed beside a Core.Runner, it alone
mounts the daemon socket and the runner talks to the guard instead
(`WorkflowLauncher:DockerSocketPath=tcp://docker-guard:2375`, or a shared unix socket). A
compromised runner process — e.g. through a git vulnerability hit while cloning an untrusted
repository — can then do with the daemon exactly what a runner does, and nothing that turns the
daemon into host root. Like the egress gateway it knows nothing about workflows or providers:
everything it enforces is data (`DockerGuard` configuration section). Linux daemons, Docker 26
(Engine API 1.45) or newer.

## What passes (DockerApiPolicy)
- Reads: `_ping`, `version`, `info`, container/image/network/volume listings, image, network
  and volume inspection.
- Container create (`ContainerCreatePolicy`): an ownership label (`OwnershipLabels`, default
  `auxilia.workflow=1` / `auxilia.egress=1` / `auxilia.companion=1`), `no-new-privileges` and NO
  other security option (an allowlist: seccomp/AppArmor profiles, SELinux labels, unconfined
  system paths, writable cgroups are all refused), never privileged; the capabilities it ENDS
  UP with — Docker's 14 defaults minus `CapDrop` plus `CapAdd`, both read as the daemon's
  string-slice type reads them (a bare string is a one-element list) — within
  `AllowedCapabilities` (default: the workflow set plus what companions keep; a container that
  drops nothing keeps NET_RAW/MKNOD and is refused); `Runtime` absent unless the guard lists
  `Runtimes` — then one of those, by name (no fallback to runc, no device-injecting runtime);
  no host/foreign namespace (network, pid, ipc, uts, userns, cgroupns, cgroup), no device,
  sysctl, cgroup parent, volume driver, annotations, links, `VolumesFrom`,
  `MaskedPaths`/`ReadonlyPaths` (even empty), no published-all ports, port bindings only on the
  literals `127.0.0.1`/`::1` (the daemon reads an unparsable host address as every interface),
  no log shipping driver; named volumes only owned ones, never with a driver configuration or a
  subpath; no bind-type `Mounts`; networks only owned ones or `OutboundNetworks`, and endpoint
  settings carry `Aliases` only — the daemon attaches to an `EndpointSettings.NetworkID`, not to
  the endpoint's name (same rule for a connect's `EndpointConfig`). Top-level fields outside the Engine API's container `Config` are refused
  (some daemons still read deprecated host fields there).
- **Host binds** (`Binds` entries with a host path, options `ro`/`rw` only) must lie strictly
  below a `BindRoots` entry — and are never forwarded as binds: a bind source follows symlinks,
  and the runner can write into its roots. The guard rewrites each one into a **subpath mount of
  the root's volume** (`auxilia-guard-root-<hash>`, local driver bound to the root, created by the
  guard itself, labelled `auxilia.guard-root=<root>` + `RootVolumeLabels`, never an ownership
  label). The daemon opens every component beneath the root without following a symlink out of
  it and mounts exactly what it validated, so a symlink planted in a run directory fails the
  start ("path concatenation escapes the base directory") instead of mounting the host — no
  race. `NoCopy` keeps bind semantics. A daemon older than API 1.45 (which would ignore the
  subpath) gets no host bind at all.
- Operations on existing objects (inspect, start/stop/kill/wait, logs, archive get/put, remove;
  network remove/connect/disconnect; volume remove) only on owned objects — ownership is the
  DAEMON's answer (`DockerUpstream` lookups), never the caller's claim.
- Networks: owned, `--internal`, bridge driver without options, default IPAM, local scope.
  Volumes: owned, local driver, no driver options (`o=bind,device=/` would be the host).
  Builds: tags `BuildTagPrefix*` only (default `auxilia-env:`), default network only, no cgroup
  parent, never a remote context. Image deletion: `BuildTagPrefix*` only. Pulls: any image from
  its registry (a pull changes only the name it fetches); imports (`fromSrc`), which name their
  own repository and tag, never.
- Everything else is refused (403 + Docker-style `{"message": …}`): exec, attach (connection
  upgrades are never forwarded), commit, update, tag, load, prune, events, swarm, plugins…

## Reading requests like the daemon
- Bodies: the daemon matches JSON field names case-insensitively, with Unicode case folding.
  `JsonBody` therefore refuses a body with a non-ASCII property name or two names differing
  only by case, and looks fields up case-insensitively.
- Query: parsed like Go's `url.ParseQuery` (`DockerApiPolicy.ParseQuery`) — case-sensitive
  keys, first value read. ASP.NET Core's query collection merges keys case-insensitively and
  would let `NetworkMode=x&networkmode=host` hide what the daemon reads.
- Path: no `%` (an encoded slash Kestrel keeps, or a decoded `%25`), no backslash, no dot
  segment — the daemon would decode those into a path the rules never saw. A version prefix
  older than `MinimumApiVersion` (default 1.45) is refused: old versions carry compatibility
  behaviour the policy does not model. The runner's requests are unversioned.

## Configuration (environment, section `DockerGuard`)
- `DockerGuard__Upstream` — the daemon: `unix:///var/run/docker.sock` (default on Linux),
  `npipe://./pipe/docker_engine`, `tcp://host:port`.
- `DockerGuard__BindRoots__N` — absolute host directories (the daemon's view) below which the
  runner binds: where its temp directory (extracted packages), `RunOutputDirectory` and
  `WorkspaceRootDirectory` live on the host. The runner must see them at the same path, or
  translate with `RunOutputHostDirectory` / `WorkspaceRootHostDirectory` (packages are bound by
  the runner's own path). **A root must be the runner's own mount point** (mount the host
  directory into the runner at exactly that path, never a parent of it): the daemon resolves the
  root path whenever it mounts the root volume, so a runner able to rename the root and put a
  symlink in its place would redirect the whole volume — a mount point cannot be renamed.
- `DockerGuard__RootVolumeLabels__<key>` — labels for the guard's root volumes (operator or
  test-session tags).
- `DockerGuard__OutboundNetworks__N` — the operator's egress network (the gateways' outbound leg).
- `DockerGuard__Runtimes__N` — the sandbox runtimes the runner uses (`ContainerRuntime` /
  `PreferredSandboxRuntimes`, e.g. `runsc`). Set it whenever the runner runs sandboxed: then no
  container can run on the shared host kernel. Unset = the daemon's default runtime only.
- `DockerGuard__AllowedCapabilities__N`, `DockerGuard__OwnershipLabels__N`,
  `DockerGuard__BuildTagPrefix`, `DockerGuard__MinimumApiVersion` — only when the runner's
  corresponding settings differ (e.g. a widened `ContainerCapabilities`).
- `ASPNETCORE_URLS` — default `http://+:2375` (a private network shared with the runner only);
  `http://unix:/run/auxilia/docker-guard.sock` for a socket shared through a volume.
- Deployment: the daemon socket mounted, `--read-only --tmpfs /tmp --cap-drop ALL
  --security-opt no-new-privileges` (exactly how `CoreClientEnvironment` runs it), reachable by
  the runner alone. The dev stack's host-process runner (Windows paths) stays unguarded.

## Contract with the runner
`Tests/Platform/Auxilia.DockerGuard.Tests/RunnerCompatibilityTests` serializes the runner's real
create requests (workflow containers, gateways, companions, networks, volumes) exactly as
Docker.DotNet does and runs them through the policy — a runner change the guard would refuse
fails there. A new Docker call in the runner needs a rule here. The system proof is
`CoreClientEnvironment`: its runner reaches Docker only through the guard, its run roots are a
volume mounted at the daemon's own path for it, and `DockerGuardSystemTests` plants a symlink
from inside the runner to show it cannot reach the host.

## File / Folder Map
```
Source/Platform/Auxilia.DockerGuard/
├── Program.cs               # GuardHost: Kestrel + the single request delegate
├── DockerGuardSettings.cs   # The policy's data
├── DockerApiPolicy.cs       # Per-call decision, path/version/query reading; ownership via IDockerObjects
├── ContainerCreatePolicy.cs # What a runner's container must (not) be; host binds → root-volume subpaths
├── JsonBody.cs              # Daemon-equivalent body reading (case-insensitive, unambiguous)
├── DockerUpstream.cs        # The daemon over unix/npipe/tcp; ownership lookups; root volumes
├── GuardProxy.cs            # Decide → forward (streaming; rewritten create bodies) or refuse; GuardLog
└── Dockerfile               # aspnet:10.0, listens on 2375
```
