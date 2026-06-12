# Auxilia.DevStand

Interactive developer stand: boots the same containerized platform stack as the EndToEnd
acceptance test (`Auxilia.SystemTestSuite/EndToEnd/EndToEndEnvironment.cs`) and keeps it
running until quit, so the dashboard can be explored manually in a browser.

## Usage

- Visual Studio: set `Auxilia.DevStand` as startup project and F5, or
- CLI: `dotnet run --project Auxilia.DevStand` (Docker Desktop must be running).

On startup it prints (and opens) the dashboard URL — login `admin` / `e2e-admin-pw` — plus
the mapped GreenMail and MongoDB endpoints. Keys: `m` sends a demo mail that triggers a
Code Review run (the reply is announced when it arrives), `o` re-opens the browser,
`q` (or Ctrl+C) tears everything down.

## Screenshot mode (visual verification harness)

`dotnet run --project Auxilia.DevStand -- --screenshots [outputDir]` (default outputDir:
`artifacts/screenshots` under the repo root, which is gitignored) boots the stack, sends one
demo mail, waits until the triggered Code Review run reaches a terminal state (polled via
Mongo), then drives headless Chromium through Microsoft.Playwright (1600x900 viewport) and
captures full-page PNGs of every dashboard page in navigation order: `01-login.png` through
`12-audit.png` (login is captured anonymously, the rest after logging in through the real
login form). It prints each absolute path, tears the stack down, and exits 0 on success.

Browser provisioning is automatic: before booting containers the harness invokes
`Microsoft.Playwright.Program.Main(["install", "chromium"])`, which downloads Chromium to
`%LOCALAPPDATA%\ms-playwright` on first run and is a cheap no-op afterwards — plain
`dotnet run -- --screenshots` works on a fresh machine. (The equivalent manual command is
`pwsh Auxilia.DevStand/bin/Debug/net10.0/playwright.ps1 install chromium`.)

## Invariants

- This project deliberately reuses `EndToEndEnvironment` verbatim — if the dev stand needs
  something the environment lacks, extend the environment, never fork the topology here.
- It must stay a pure consumer: no test assertions, no `[Test]` fixtures (it references the
  system test suite, but `dotnet test` ignores it because it carries no test SDK).
- First start builds Docker images unless the `.prebuilt-images` marker exists in the repo
  root (same mechanic as the system test suite).
