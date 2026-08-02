# Auxilia.DevStand

Interactive developer stand: boots the same containerized platform stack as the EndToEnd
acceptance test (`Tests/System/Auxilia.SystemTestSuite/EndToEnd/EndToEndEnvironment.cs`) and keeps it
running until quit, so the dashboard can be explored manually in a browser.

## Usage

- Visual Studio: set `Auxilia.DevStand` as startup project and F5, or
- CLI: `dotnet run --project Auxilia.DevStand` (Docker Desktop must be running).
- `--presentation` boots without pre-built configurations (configure live in the editor);
  `--keep-data` persists Mongo and the dashboard's cookie-signing keys in named Docker
  volumes (`auxilia-devstand-*`), so slots, workflows, and the login survive restarts
  (delete the volumes for a factory reset).

On startup it prints (and opens) the dashboard URL — login `admin` / `e2e-admin-pw` — plus
the mapped GreenMail and MongoDB endpoints. Keys: `m` sends a demo mail that triggers a
Code Review run (the reply is announced when it arrives), `c` dispatches a Claude Code run
(watch the live agent chat on the dashboard), `o` re-opens the browser, `q` (or Ctrl+C)
tears everything down.

Claude Code runs use the in-image stub CLI by default. Set `ANTHROPIC_API_KEY` in the
environment BEFORE starting the stand and the `coding-agent` slot is re-seeded with the
real `claude` binary baked into the workflow image — `c` then runs a real agent session
(this is the manual test path; system tests always use the stub).

## Screenshot mode (visual verification harness)

`dotnet run --project Auxilia.DevStand -- --screenshots [outputDir]` (default outputDir:
`artifacts/screenshots` under the repo root, which is gitignored) boots the stack, sends one
demo mail, then drives headless Chromium through Microsoft.Playwright (1600x900 viewport) and
captures full-page PNGs of every dashboard page: `01-login.png` through `14-audit.png` plus
`15-runs-filtered.png` (login is captured anonymously, the rest after logging in through the
real login form). `02-dashboard.png` is captured while the mail-triggered Code Review run is
still RUNNING (polled via Mongo) so the dashboard's Live now section has content; the harness
then waits for the terminal state before capturing the remaining pages. Demo data is seeded
beforehand: catalog availability, one demo workflow configuration, and run-history records
(the finished run is adopted into the demo configuration and gains a rerun successor plus a
schedule-dispatched sibling, so `15-runs-filtered.png` shows the trigger-origin column and
`06-run-detail.png` shows lineage chips). `04-workflow-editor.png` is interactive: the
harness walks the workflow-editor create flow (basics filled, work-items slot added, email
provider chosen) so the generated settings form is on screen. It prints each absolute path,
tears the stack down, and exits 0 on success.

Browser provisioning is automatic: before booting containers the harness invokes
`Microsoft.Playwright.Program.Main(["install", "chromium"])`, which downloads Chromium to
`%LOCALAPPDATA%\ms-playwright` on first run and is a cheap no-op afterwards — plain
`dotnet run -- --screenshots` works on a fresh machine. (The equivalent manual command is
`pwsh Tests/System/Auxilia.DevStand/bin/Debug/net10.0/playwright.ps1 install chromium`.)

## Invariants

- This project deliberately reuses `EndToEndEnvironment` verbatim — if the dev stand needs
  something the environment lacks, extend the environment, never fork the topology here.
- It must stay a pure consumer: no test assertions, no `[Test]` fixtures (it references the
  system test suite, but `dotnet test` ignores it because it carries no test SDK).
- First start builds Docker images unless the `.prebuilt-images` marker exists in the repo
  root (same mechanic as the system test suite).
