# Demo seed: the ONE "implementation" workflow type with BOTH coding agents as the author —
# "[SIM] Implementation (driven stub)" binds claude-code-cli, "[SIM] Implementation (GitHub
# Copilot)" binds github-copilot-cli; both point the CLI at the in-image driven stub, so the
# whole pipeline (completeness, plan gate, implementation, code gate, push prompt, story-state
# gate) runs watchably WITHOUT real AI. Requires the dev stack (Start-DevStack.ps1) and Docker.
# The image rebuild is needed ONCE after pulling the Copilot console-bridge change (the stub
# gained the provider-neutral hook-port fallback); skip it on later re-runs.
param([switch]$SkipImageBuild)

$repo = $PSScriptRoot
$headers = @{ Authorization = "Bearer auxilia-steering-dev-key" }

if (-not $SkipImageBuild) {
    Write-Host "Rebuilding auxilia-implementation-workflow:system-test (driven stub + both CLIs)..."
    docker build -t auxilia-implementation-workflow:system-test `
        -f "$repo\Source\Auxilia.Implementation.Workflow\Dockerfile" $repo
    if ($LASTEXITCODE -ne 0) { throw "The implementation workflow image build failed." }
}

# ---- Core.Api up? ----------------------------------------------------------------------------
$apiUp = $false
foreach ($attempt in 1..15) {
    try {
        Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=100" -Headers $headers -TimeoutSec 3 | Out-Null
        $apiUp = $true; break
    } catch { Start-Sleep 2 }
}
if (-not $apiUp) { throw "Core.Api is not reachable on http://localhost:5280 — run Start-DevStack.ps1 first." }

$types = (Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=100" -Headers $headers).items
if ($types.workflowType -notcontains "implementation") {
    Write-Warning "Workflow type 'implementation' is not registered in the dev Core — register/approve it first."
}

# ---- Sim git server --------------------------------------------------------------------------
if (-not (docker ps --format "{{.Names}}" | Select-String -Quiet "^auxilia-sim-git$")) {
    docker rm -f auxilia-sim-git 2>$null | Out-Null
    docker run -d --name auxilia-sim-git -p 8418:80 auxilia-git-server:system-test | Out-Null
    Write-Host "auxilia-sim-git started (http://localhost:8418, builduser/the-pat)."
}

# ---- Providers (deny-by-default catalog: register if missing, then make available) -----------
$catalogItems = (Invoke-RestMethod "http://localhost:5280/api/provider-catalog" -Headers $headers).items
function Ensure-Provider($body) {
    if ($script:catalogItems.providerType -notcontains $body.providerType) {
        Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog" -Headers $headers `
            -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 5) | Out-Null
        Write-Host "Provider $($body.providerType) registered."
    }
}
Ensure-Provider @{
    providerType = "simulated-work-items"; category = "task-source"
    description = "SIMULATION: scripted TFS stand-in with per-operation delay - live-view testing only."
    contracts = @("Auxilia.Workflows.TaskSource.IWorkItemAccess")
    settings = @(
        @{ key = "Title"; label = "Story title"; kind = "Text"; required = $false },
        @{ key = "Description"; label = "Story description"; kind = "Text"; required = $false },
        @{ key = "DelaySeconds"; label = "Delay per operation (s)"; kind = "Number"; required = $false })
}
Ensure-Provider @{
    providerType = "coding-session-workspace"; category = "coding-session"
    description = "Backs a session with the mounted workspace directory."
    contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess", "Auxilia.Workflows.TaskSource.IWorkItemAccess")
    settings = @(@{ key = "WorkingPath"; label = "Working path"; kind = "Text"; required = $false })
}
foreach ($type in "simulated-work-items", "coding-session-workspace", "claude-code-cli", "github-copilot-cli", "git-repository") {
    $entry = $catalogItems | Where-Object providerType -eq $type
    if ($entry -and $entry.available -ne $true) {
        Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog/$type/availability" `
            -Headers $headers -ContentType "application/json" -Body '{"available":true}' | Out-Null
        Write-Host "Provider $type made available."
    }
}

# ---- The git credential connector ------------------------------------------------------------
$connectors = (Invoke-RestMethod "http://localhost:5280/api/connectors" -Headers $headers).items
$simGit = $connectors | Where-Object name -eq "SIM git server (builduser)"
if (-not $simGit) {
    $simGit = Invoke-RestMethod -Method Post "http://localhost:5280/api/connectors" -Headers $headers -ContentType "application/json" -Body (@{
        name = "SIM git server (builduser)"; providerType = "azure-devops"
        settings = @{ username = "builduser"; token = "the-pat" }
    } | ConvertTo-Json)
    Write-Host "Connector 'SIM git server (builduser)' created."
}

# ---- The two configurations: SAME workflow type, the author slot decides the agent -----------
$configurations = (Invoke-RestMethod "http://localhost:5280/api/configurations?take=200" -Headers $headers).items
function Ensure-Configuration($name, $storyTitle, $agentBinding) {
    if ($script:configurations.name -contains $name) { return }
    Invoke-RestMethod -Method Post "http://localhost:5280/api/configurations" -Headers $headers -ContentType "application/json" -Body (@{
        name = $name; workflowType = "implementation"; tags = @("sim")
        context = @{
            "completeness-check" = "true"; "ai-plan-review" = "false"; "ai-code-review" = "false"
            "user-plan-gate" = "true"; "user-code-gate" = "true"; "push-mode" = "prompt"
            "gate-idle-compaction" = "0"
        }
        slotBindings = @(
            @{ slotName = "work-items"; providerType = "simulated-work-items"
               settings = @{ Title = $storyTitle; DelaySeconds = "3" } },
            $agentBinding,
            @{ slotName = "workspace-repo"; providerType = "git-repository"; connectorId = $simGit.id
               settings = @{ CloneUrl = "http://host.docker.internal:8418/git/test.git"; NoCache = "true"; AllowPush = "true" } },
            @{ slotName = "repository"; providerType = "coding-session-workspace"
               settings = @{ WorkingPath = "/workspace/repos/workspace-repo" } })
    } | ConvertTo-Json -Depth 5) | Out-Null
    Write-Host "Configuration '$name' created."
}
Ensure-Configuration "[SIM] Implementation (driven stub)" `
    "Simulated story: add the implemented marker" `
    @{ slotName = "coding-agent"; providerType = "claude-code-cli"
       settings = @{ OAuthToken = "sim-fake-oauth-token"; CliPath = "/usr/local/bin/driven-stub" } }
Ensure-Configuration "[SIM] Implementation (GitHub Copilot)" `
    "Simulated story: Copilot adds the implemented marker" `
    @{ slotName = "coding-agent"; providerType = "github-copilot-cli"
       settings = @{ token = "sim-fake-gh-token"; CliPath = "/usr/local/bin/driven-stub" } }

Write-Host ""
Write-Host "Demo ready - ONE workflow type, two authors:" -ForegroundColor Green
Write-Host "  [SIM] Implementation (driven stub)      -> author: Claude Code"
Write-Host "  [SIM] Implementation (GitHub Copilot)   -> author: GitHub Copilot"
Write-Host "Start either from the steering client with any work-item id (e.g. SIM-42) and watch:"
Write-Host "  completeness -> plan (approval gate) -> implement (code gate) -> push prompt -> story-state gate."
Write-Host "Turn pacing rides DRIVEN_STUB_DELAY (Start-DevStack sets 4s). Afterwards: Cleanup-TestWorkflows.ps1."
