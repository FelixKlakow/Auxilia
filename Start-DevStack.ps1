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

# The bootstrap key seeds the dev admin principal idempotently (by hash) — without it a fresh
# core-data store would have NO principal matching the scripts' bearer key.
Start-Process pwsh -WorkingDirectory $repo -ArgumentList "-NoExit", "-Command",
    "`$env:ASPNETCORE_URLS='http://localhost:5280'; `$env:CoreSecurity__BootstrapApiKey='auxilia-steering-dev-key'; dotnet run --project Source/Auxilia.Core.Api --no-build"

# ${env:...} braces are REQUIRED: several names carry hyphens (claude-code-cli), which the
# plain $env:name syntax rejects as a parser error — silently killing the spawned window.
$envSetup = ($runnerEnv.GetEnumerator() | ForEach-Object { "`${env:$($_.Key)}='$($_.Value)'" }) -join "; "
Start-Process pwsh -WorkingDirectory $repo -ArgumentList "-NoExit", "-Command",
    "$envSetup; dotnet run --project Source/Auxilia.Core.Runner --no-build"

Write-Host "Core.Api starting on http://localhost:5280; Core.Runner starting (Docker runs enabled)."

# ---- Simulation seed (idempotent) ------------------------------------------------------------
# The sim git server + the dummy pieces behind "[SIM] Implementation (driven stub)": provider
# catalog entries, the git credential connector, and the configuration itself — so a fresh
# stack always comes up simulation-ready.

if (-not (docker ps --format "{{.Names}}" | Select-String -Quiet "^auxilia-sim-git$")) {
    docker rm -f auxilia-sim-git 2>$null | Out-Null
    docker run -d --name auxilia-sim-git -p 8418:80 auxilia-git-server:system-test | Out-Null
    Write-Host "auxilia-sim-git started (http://localhost:8418, builduser/the-pat)."
}

$headers = @{ Authorization = "Bearer auxilia-steering-dev-key" }
$apiUp = $false
foreach ($attempt in 1..30) {
    try {
        Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=1" -Headers $headers -TimeoutSec 3 | Out-Null
        $apiUp = $true; break
    } catch { Start-Sleep 2 }
}
if (-not $apiUp) { Write-Warning "Core.Api did not come up — simulation seed skipped."; return }

$catalogItems = (Invoke-RestMethod "http://localhost:5280/api/provider-catalog" -Headers $headers).items
$catalog = $catalogItems.providerType
if ($catalog -notcontains "simulated-work-items") {
    Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog" -Headers $headers -ContentType "application/json" -Body (@{
        providerType = "simulated-work-items"; category = "task-source"
        description = "SIMULATION: scripted TFS stand-in with per-operation delay - live-view testing only."
        contracts = @("Auxilia.Workflows.TaskSource.IWorkItemAccess")
        settings = @(
            @{ key = "Title"; label = "Story title"; kind = "Text"; required = $false },
            @{ key = "Description"; label = "Story description"; kind = "Text"; required = $false },
            @{ key = "DelaySeconds"; label = "Delay per operation (s)"; kind = "Number"; required = $false })
    } | ConvertTo-Json -Depth 4) | Out-Null
    Write-Host "Provider simulated-work-items registered."
}
if ($catalog -notcontains "coding-session-workspace") {
    Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog" -Headers $headers -ContentType "application/json" -Body (@{
        providerType = "coding-session-workspace"; category = "coding-session"
        description = "Backs a session with the mounted workspace directory."
        contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess", "Auxilia.Workflows.TaskSource.IWorkItemAccess")
        settings = @(@{ key = "WorkingPath"; label = "Working path"; kind = "Text"; required = $false })
    } | ConvertTo-Json -Depth 4) | Out-Null
    Write-Host "Provider coding-session-workspace registered."
}

# Availability is deny-by-default — the editors only offer AVAILABLE providers.
foreach ($type in "simulated-work-items", "coding-session-workspace") {
    if (($catalogItems | Where-Object providerType -eq $type).available -ne $true) {
        Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog/$type/availability" `
            -Headers $headers -ContentType "application/json" -Body '{"available":true}' | Out-Null
        Write-Host "Provider $type made available."
    }
}

$connectors = (Invoke-RestMethod "http://localhost:5280/api/connectors" -Headers $headers).items
$simGit = $connectors | Where-Object name -eq "SIM git server (builduser)"
if (-not $simGit) {
    $simGit = Invoke-RestMethod -Method Post "http://localhost:5280/api/connectors" -Headers $headers -ContentType "application/json" -Body (@{
        name = "SIM git server (builduser)"; providerType = "azure-devops"
        settings = @{ username = "builduser"; token = "the-pat" }
    } | ConvertTo-Json)
    Write-Host "Connector 'SIM git server (builduser)' created."
}

$configurations = (Invoke-RestMethod "http://localhost:5280/api/configurations?take=200" -Headers $headers).items
if ($configurations.name -notcontains "[SIM] Implementation (driven stub)") {
    Invoke-RestMethod -Method Post "http://localhost:5280/api/configurations" -Headers $headers -ContentType "application/json" -Body (@{
        name = "[SIM] Implementation (driven stub)"; workflowType = "implementation"; tags = @("sim")
        context = @{
            "completeness-check" = "true"; "ai-plan-review" = "false"; "ai-code-review" = "false"
            "user-plan-gate" = "true"; "user-code-gate" = "true"; "push-mode" = "prompt"
            "gate-idle-compaction" = "0"
        }
        slotBindings = @(
            @{ slotName = "work-items"; providerType = "simulated-work-items"
               settings = @{ Title = "Simulated story: add the implemented marker"; DelaySeconds = "3" } },
            @{ slotName = "coding-agent"; providerType = "claude-code-cli"
               settings = @{ OAuthToken = "sim-fake-oauth-token"; CliPath = "/usr/local/bin/driven-stub" } },
            @{ slotName = "workspace-repo"; providerType = "git-repository"; connectorId = $simGit.id
               settings = @{ CloneUrl = "http://host.docker.internal:8418/git/test.git"; NoCache = "true"; AllowPush = "true" } },
            @{ slotName = "repository"; providerType = "coding-session-workspace"
               settings = @{ WorkingPath = "/workspace/repos/workspace-repo" } })
    } | ConvertTo-Json -Depth 5) | Out-Null
    Write-Host "Configuration '[SIM] Implementation (driven stub)' created."
}
Write-Host "Simulation ready."
