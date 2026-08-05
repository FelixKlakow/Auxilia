# Auxilia.DevStand

Interactive developer stand: boots the same containerized platform stack as the EndToEnd
acceptance test (`Tests/System/Auxilia.SystemTestSuite/EndToEnd/EndToEndEnvironment.cs`) —
including the `Auxilia.AdminConsole` operator UI — and keeps it running until quit, so the
console can be explored manually in a browser.

## Usage

- Visual Studio: set `Auxilia.DevStand` as startup project and F5, or
- CLI: `dotnet run --project Tests/System/Auxilia.DevStand` (Docker Desktop must be running).
- `--keep-data` persists Mongo in a named Docker volume (`auxilia-devstand-mongo`), so
  triggers survive restarts (delete the volume for a factory reset).

On startup it prints the AdminConsole URL plus the mapped Core.Api, GreenMail, and MongoDB
endpoints. The console container carries a static Administrator app key (`Core__ApiKey`), so
every page renders fully authenticated as that service principal — no browser sign-in
(production uses the same-origin Core session cookie instead). Keys: `m` sends a demo mail
that triggers a Code Review run (the reply is announced when it arrives), `s` sends a session
mail, `o` opens the console in the browser, `q` (or Ctrl+C) tears everything down.

## Screenshot mode (visual verification harness)

`dotnet run --project Tests/System/Auxilia.DevStand -- --screenshots [outputDir]` (default
outputDir: `artifacts/screenshots` under the repo root, which is gitignored) boots the stack,
sends one demo mail, then drives headless Chromium through Microsoft.Playwright (1600x900
viewport) and captures full-page PNGs of the AdminConsole pages: `01-dashboard.png` through
`12-audit.png` (dashboard, home, runs, run detail, workflows, workflow editor, connectors,
admin principals, provider catalog, identity sources, workflow types, audit).
`01-dashboard.png` is captured while the mail-triggered Code Review run is still RUNNING
(polled via Mongo) so the dashboard's Active now tile has content; the harness then waits for
the terminal state before capturing the remaining pages (`04-run-detail.png` shows that
finished run). It prints each absolute path, tears the stack down, and exits 0 on success.

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
  root AND no build input was written after it — a stale marker triggers an automatic
  rebuild and is renewed by it (`TestImages`, same mechanic as the system test suite).
