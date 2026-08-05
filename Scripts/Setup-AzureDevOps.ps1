# Seeds a REAL Azure DevOps implementation-workflow setup against the running dev Core
# (Scripts/Start-DevStack.ps1 first): one tfs-account connector carrying ONLY the auth
# (OrgUrl + PAT — the repository stays per-run, chosen on the configuration) and one
# "implementation" configuration bound to a real story source + repository.
#
# Prerequisites (free tier is enough — https://dev.azure.com, up to 5 basic users):
#   - an Azure DevOps organization + project with a git repo and at least one work item
#   - a PAT scoped to Work Items (Read & Write) + Code (Read & Write)
#
# Example:
#   ./Scripts/Setup-AzureDevOps.ps1 -OrgUrl https://dev.azure.com/my-org `
#       -Pat <pat> -CloneUrl https://dev.azure.com/my-org/auxilia-e2e/_git/auxilia-e2e
# The Claude account is connected afterwards in the steering client (Connect… on the coding-agent
# slot) unless -ClaudeOAuthToken is passed.
param(
    [Parameter(Mandatory)] [string]$OrgUrl,
    [Parameter(Mandatory)] [string]$Pat,
    [Parameter(Mandatory)] [string]$CloneUrl,
    [string]$Branch = "",
    [string]$ClaudeOAuthToken = "",
    [string]$ConfigurationName = "Implementation (Azure DevOps)"
)

$headers = @{ Authorization = "Bearer auxilia-steering-dev-key" } # well-known local dev bootstrap key, not a secret

try {
    Invoke-RestMethod "http://localhost:5280/api/workflow-types?take=1" -Headers $headers -TimeoutSec 3 | Out-Null
} catch {
    throw "Core.Api is not reachable on http://localhost:5280 - start the stack via Scripts/Start-DevStack.ps1 first."
}

# Availability is deny-by-default - make sure every provider the configuration binds is offered.
$catalogItems = (Invoke-RestMethod "http://localhost:5280/api/provider-catalog" -Headers $headers).items

# git-repository is a DATA-ONLY mount provider (no plugin) - register it here so this script
# works on a fresh stack without the demo script having run first. The role-tagged settings
# are what the runner's workspace materializer interprets.
if ($catalogItems.providerType -notcontains "git-repository") {
    Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog" -Headers $headers -ContentType "application/json" -Body (@{
        providerType = "git-repository"; category = "workspace"
        description = "A git repository materialized into the run's workspace."
        contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess")
        requiredCredentialContract = "git-credential"
        mountsIntoWorkspace = $true
        settings = @(
            @{ key = "CloneUrl"; label = "Repository"; kind = "Text"; required = $true; role = "clone-url" },
            @{ key = "Branch"; label = "Branch"; kind = "Text"; role = "branch" },
            @{ key = "NoCache"; label = "Fresh clone per run"; kind = "Boolean"; role = "no-cache" },
            @{ key = "AllowPush"; label = "Allow pushing"; kind = "Boolean"; role = "allow-push" })
    } | ConvertTo-Json -Depth 5) | Out-Null
    Write-Host "Provider git-repository registered."
    $catalogItems = (Invoke-RestMethod "http://localhost:5280/api/provider-catalog" -Headers $headers).items
}

foreach ($type in "tfs-account", "git-repository", "claude-code-cli", "coding-session-workspace") {
    $entry = $catalogItems | Where-Object providerType -eq $type
    if (-not $entry) { throw "Provider '$type' is not in the catalog - it is registered by the runner's plugin scan on the first run; make sure the dev-stack runner started (Scripts/Start-DevStack.ps1, .devstack/runner.log)." }
    if ($entry.available -ne $true) {
        Invoke-RestMethod -Method Post "http://localhost:5280/api/provider-catalog/$type/availability" `
            -Headers $headers -ContentType "application/json" -Body '{"available":true}' | Out-Null
        Write-Host "Provider $type made available."
    }
}

# ---- The account connector: auth only - the repository is chosen per configuration/run --------
$connectorName = "Azure DevOps ($([uri]::new($OrgUrl).AbsolutePath.Trim('/')))"
$connectors = (Invoke-RestMethod "http://localhost:5280/api/connectors" -Headers $headers).items
$azdo = $connectors | Where-Object name -eq $connectorName
if (-not $azdo) {
    $azdo = Invoke-RestMethod -Method Post "http://localhost:5280/api/connectors" -Headers $headers -ContentType "application/json" -Body (@{
        name = $connectorName; providerType = "tfs-account"
        settings = @{ OrgUrl = $OrgUrl; token = $Pat }
    } | ConvertTo-Json)
    Write-Host "Connector '$connectorName' created."
} else {
    Write-Host "Connector '$connectorName' already exists - reusing it (recreate it in the steering client to rotate the PAT)."
}

# ---- The configuration: same shape as the SIM one, real story source + real repository --------
$repoSettings = @{ CloneUrl = $CloneUrl; NoCache = "true"; AllowPush = "true" }
if ($Branch) { $repoSettings.Branch = $Branch }
$agentSettings = @{}
if ($ClaudeOAuthToken) { $agentSettings.OAuthToken = $ClaudeOAuthToken }

$configurations = (Invoke-RestMethod "http://localhost:5280/api/configurations?take=200" -Headers $headers).items
if ($configurations.name -contains $ConfigurationName) {
    Write-Host "Configuration '$ConfigurationName' already exists - nothing to do."
} else {
    Invoke-RestMethod -Method Post "http://localhost:5280/api/configurations" -Headers $headers -ContentType "application/json" -Body (@{
        name = $ConfigurationName; workflowType = "implementation"; tags = @("azdo")
        context = @{
            "refinement-check" = "true"; "ai-plan-review" = "false"; "ai-code-review" = "false"
            "user-plan-gate" = "true"; "user-code-gate" = "true"; "push-mode" = "prompt"
            "gate-idle-compaction" = "0"
        }
        slotBindings = @(
            @{ slotName = "work-items"; providerType = "tfs-account"; connectorId = $azdo.id },
            @{ slotName = "coding-agent"; providerType = "claude-code-cli"; settings = $agentSettings },
            @{ slotName = "workspace-repo"; providerType = "git-repository"; connectorId = $azdo.id
               settings = $repoSettings },
            @{ slotName = "repository"; providerType = "coding-session-workspace"
               settings = @{ WorkingPath = "/workspace/repos/workspace-repo" } })
    } | ConvertTo-Json -Depth 5) | Out-Null
    Write-Host "Configuration '$ConfigurationName' created."
}

Write-Host ""
Write-Host "Azure DevOps setup ready." -ForegroundColor Green
if (-not $ClaudeOAuthToken) {
    Write-Host "Connect the Claude account in the steering client (edit '$ConfigurationName' -> coding-agent -> Connect...)."
}
Write-Host "Then start '$ConfigurationName' from the steering client with a real work-item id (the numeric story id)."
Write-Host "Flow: refinement -> plan (approval gate) -> implement (code gate) -> push prompt -> story-state gate."
