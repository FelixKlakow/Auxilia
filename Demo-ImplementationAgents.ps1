# THE single init script for the two-author implementation demo — run it on any machine with
# Docker Desktop and the .NET 10 SDK (side-by-side repo clones as usual) and it takes care of
# everything, idempotently:
#   solution build (fresh machine) -> RabbitMQ container -> git-server image -> dev stack
#   (Start-DevStack.ps1, which also statically registers the "implementation" type) ->
#   workflow image -> providers/connector/configurations.
# Result: two configurations of the ONE implementation workflow type, mocked by the driven stub:
#   [SIM] Implementation (driven stub)      -> author: Claude Code
#   [SIM] Implementation (GitHub Copilot)   -> author: GitHub Copilot
# Re-runs are cheap: pass -SkipImageBuild after the workflow image exists.
param([switch]$SkipImageBuild)

$repo = $PSScriptRoot
$headers = @{ Authorization = "Bearer auxilia-steering-dev-key" }

function Test-CoreApi {
    try {
        Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=1" -Headers $script:headers -TimeoutSec 3 | Out-Null
        return $true
    } catch { return $false }
}

# ---- Docker prerequisites (scoped to Auxilia; never touches foreign containers) --------------
if (-not (docker ps --format "{{.Names}}" | Select-String -Quiet "^auxilia-rabbitmq$")) {
    docker rm -f auxilia-rabbitmq 2>$null | Out-Null
    docker run -d --name auxilia-rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:4-management | Out-Null
    Write-Host "auxilia-rabbitmq started (localhost:5672, guest/guest)."
}
docker image inspect auxilia-git-server:system-test 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Building auxilia-git-server:system-test..."
    docker build -t auxilia-git-server:system-test -f "$repo\Auxilia.SystemTestSuite\GitServer\Dockerfile" $repo
    if ($LASTEXITCODE -ne 0) { throw "The git-server image build failed." }
}

# ---- Dev stack: build + launch only when the Core is not already up --------------------------
if (-not (Test-CoreApi)) {
    Write-Host "Core.Api is not running - building the solution and starting the dev stack..."
    dotnet build "$repo\Auxilia.slnx"
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    & "$repo\Start-DevStack.ps1"
    $apiUp = $false
    foreach ($attempt in 1..30) {
        if (Test-CoreApi) { $apiUp = $true; break }
        Start-Sleep 2
    }
    if (-not $apiUp) { throw "Core.Api did not come up on http://localhost:5280." }
}

# ---- The workflow image the runs execute in --------------------------------------------------
if (-not $SkipImageBuild) {
    Write-Host "Building auxilia-implementation-workflow:system-test (driven stub + both CLIs)..."
    docker build -t auxilia-implementation-workflow:system-test `
        -f "$repo\Source\Auxilia.Implementation.Workflow\Dockerfile" $repo
    if ($LASTEXITCODE -ne 0) { throw "The implementation workflow image build failed." }
}

$types = (Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=100" -Headers $headers).items
if ($types.workflowType -notcontains "implementation") {
    Write-Warning "Workflow type 'implementation' is not registered - restart the stack via Start-DevStack.ps1 (it seeds the type statically)."
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
# The mount provider MUST carry the role-tagged settings - the runner's workspace materializer
# interprets them (clone-url/no-cache/allow-push); without the roles nothing is cloned.
Ensure-Provider @{
    providerType = "git-repository"; category = "workspace"
    description = "A git repository materialized into the run's workspace."
    contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess")
    requiredCredentialContract = "git-credential"
    mountsIntoWorkspace = $true
    settings = @(
        @{ key = "CloneUrl"; label = "Repository"; kind = "Text"; required = $true; role = "clone-url" },
        @{ key = "NoCache"; label = "Fresh clone per run"; kind = "Boolean"; role = "no-cache" },
        @{ key = "AllowPush"; label = "Allow pushing"; kind = "Boolean"; role = "allow-push" })
}
Ensure-Provider @{
    providerType = "claude-code-cli"; category = "coding-agent"
    description = "Runs the Claude Code CLI as the coding agent of a workflow."
    contracts = @("Auxilia.Workflows.AiAgent.CodingAgent.ICodingAgent")
    settings = @(
        @{ key = "OAuthToken"; label = "Claude account"; kind = "Secret"; required = $false },
        @{ key = "ApiKey"; label = "API key (fallback)"; kind = "Secret"; required = $false },
        @{ key = "Model"; label = "Model"; kind = "Text"; required = $false },
        @{ key = "CliPath"; label = "CLI path"; kind = "Text"; required = $false })
}
Ensure-Provider @{
    providerType = "github-copilot-cli"; category = "coding-agent"
    description = "Runs the GitHub Copilot CLI as the coding agent of a workflow."
    contracts = @("Auxilia.Workflows.AiAgent.CodingAgent.ICodingAgent")
    settings = @(
        @{ key = "token"; label = "GitHub account"; kind = "Secret"; required = $true },
        @{ key = "Model"; label = "Model"; kind = "Text"; required = $false },
        @{ key = "CliPath"; label = "CLI path"; kind = "Text"; required = $false },
        @{ key = "UseSdkSession"; label = "Interactive session (SDK)"; kind = "Boolean"; required = $false })
}

# Availability is deny-by-default - the editors only offer AVAILABLE providers.
$catalogItems = (Invoke-RestMethod "http://localhost:5280/api/provider-catalog" -Headers $headers).items
foreach ($type in "simulated-work-items", "coding-session-workspace", "git-repository", "claude-code-cli", "github-copilot-cli") {
    if (($catalogItems | Where-Object providerType -eq $type).available -ne $true) {
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
