namespace Auxilia.DevStand;

/// <summary>
/// Visual verification harness — PARKED for the BackendService retirement (Phase 4).
///
/// Every capture this harness performed was against the BackendService Blazor dashboard, which has
/// been retired. Its UI now lives in <c>Auxilia.AdminConsole</c> (a pure Core.Api client), which is
/// not yet booted by the dev-stand environment. Restoring screenshots is deferred until the
/// AdminConsole container is wired into <c>EndToEndEnvironment</c> (same-origin with Core.Api so the
/// per-user bearer handoff works).
///
/// TODO(Phase 4+): boot the AdminConsole container in <c>EndToEndEnvironment</c>, expose its URL,
/// and re-implement the Playwright page walk against the console's routes (see git history for the
/// previous dashboard-based implementation).
/// </summary>
internal static class ScreenshotHarness
{
    public static Task<int> RunAsync(string? outputDirArg)
    {
        _ = outputDirArg;
        Console.Error.WriteLine(
            "Screenshot capture is parked: the operator dashboard moved from the retired BackendService " +
            "to Auxilia.AdminConsole, which is not yet wired into the dev stand. " +
            "TODO(Phase 4+): boot the AdminConsole container in EndToEndEnvironment and restore the capture walk.");
        return Task.FromResult(0);
    }
}
