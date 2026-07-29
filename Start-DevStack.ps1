# Starts the local dev stack the steering client connects to: Core.Api on http://localhost:5280 and a
# Core.Runner wired for real Docker workflow runs (slot plugins, environment layers, dev mode).
# State lives in Source/Auxilia.Core.Api/core-data (JSON backend) and survives restarts.
# Requires: the auxilia-rabbitmq container running, Docker Desktop, a prior solution build
# (pass -Build to build first). Each service opens in its own window; close them to stop.
param([switch]$Build)

$repo = $PSScriptRoot
if ($Build) {
    dotnet build "$repo\Auxilia.slnx"
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$runnerEnv = @{
    WorkflowDispatcher__CoreApiBaseAddress                        = "http://localhost:5280"
    WorkflowLauncher__DockerSocketPath                            = "npipe://./pipe/docker_engine"
    WorkflowLauncher__RabbitMqHost                                = "host.docker.internal"
    WorkflowLauncher__SlotPackages__claude-code-cli               = "$repo\Auxilia.Slots.ClaudeCode\bin\Debug\net10.0\Auxilia.Slots.ClaudeCode.slothandler.dll"
    WorkflowLauncher__SlotPackages__github-copilot-cli            = "$repo\Auxilia.Slots.GitHubCopilot\bin\Debug\net10.0\Auxilia.Slots.GitHubCopilot.slothandler.dll"
    WorkflowLauncher__SlotPackages__tfs-account                   = "$repo\Auxilia.Slots.AzureDevOps\bin\Debug\net10.0\Auxilia.Slots.AzureDevOps.slothandler.dll"
    WorkflowLauncher__SlotPackages__simulated-work-items          = "$repo\Auxilia.Slots.SimulatedWorkItems\bin\Debug\net10.0\Auxilia.Slots.SimulatedWorkItems.slothandler.dll"
    WorkflowLauncher__SlotPackages__coding-session-workspace      = "$repo\Auxilia.Slots.CodingSession\bin\Debug\net10.0\Auxilia.Slots.CodingSession.slothandler.dll"
    WorkflowLauncher__ExtraEnvironmentVariables__DRIVEN_STUB_DELAY = "4"
    WorkflowLauncher__EnvironmentLayers__dotnet-10                = "$repo\Source\Auxilia.Core.Runner\environment-layers\dotnet-10.dockerfile"
    WorkflowLauncher__EnvironmentLayers__node-22                  = "$repo\Source\Auxilia.Core.Runner\environment-layers\node-22.dockerfile"
    WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE = "1"
    WorkflowDispatcher__ApprovedLongLivingWorkflowTypes__0        = "implementation"
}

Start-Process pwsh -WorkingDirectory $repo -ArgumentList "-NoExit", "-Command",
    "`$env:ASPNETCORE_URLS='http://localhost:5280'; dotnet run --project Source/Auxilia.Core.Api --no-build"

$envSetup = ($runnerEnv.GetEnumerator() | ForEach-Object { "`$env:$($_.Key)='$($_.Value)'" }) -join "; "
Start-Process pwsh -WorkingDirectory $repo -ArgumentList "-NoExit", "-Command",
    "$envSetup; dotnet run --project Source/Auxilia.Core.Runner --no-build"

Write-Host "Core.Api starting on http://localhost:5280; Core.Runner starting (Docker runs enabled)."
