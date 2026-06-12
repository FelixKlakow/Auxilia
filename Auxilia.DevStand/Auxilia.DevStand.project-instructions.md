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

## Invariants

- This project deliberately reuses `EndToEndEnvironment` verbatim — if the dev stand needs
  something the environment lacks, extend the environment, never fork the topology here.
- It must stay a pure consumer: no test assertions, no `[Test]` fixtures (it references the
  system test suite, but `dotnet test` ignores it because it carries no test SDK).
- First start builds Docker images unless the `.prebuilt-images` marker exists in the repo
  root (same mechanic as the system test suite).
