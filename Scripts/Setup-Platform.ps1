<#
.SYNOPSIS
    One-shot platform setup: seeds a running Core with the standard catalog — base boxes,
    environment layers, slot providers/connectors, and the default workflow types.

.DESCRIPTION
    Works against ANY reachable Core (a local evaluation stack started with
    Start-DevStack.ps1 or a real deployment) and is idempotent: everything is
    "register if absent / upsert", so re-running converges instead of duplicating.

    What it registers:
      * Environment BASES (the (name, version) vocabulary): common linux AND windows boxes.
      * Environment LAYERS (dotnet 8/9/10, node 20/22, python, java, go, rust, C++ toolchain)
        — linux setup scripts always; windows variants of the same layers with -IncludeWindows
        (layers are multi-base: one capability name, one script per base).
      * Slot PROVIDERS: the three coding-agent CLIs (claude-code-cli, github-copilot-cli,
        codex-cli — all three bind into the ONE coding-session workflow), the source-control
        account providers (github-account, tfs-account for TFS/Azure DevOps), and the
        workspace providers (git-repository, empty-workspace, coding-session-workspace).
      * The default WORKFLOW TYPES (with -BuildImages the images are docker-built first):
        coding-session, claude-code, github-copilot, implementation,
        pull-request-code-review, session-notifier.

    NOT done here (deliberate): connectors (they carry YOUR credentials — create them in the
    console or via Setup-AzureDevOps.ps1), platform settings (security.default-resource-access
    needs a step-up elevation — the restricted default is the safe posture), and grants
    (under the restricted default, new layers/providers are administrators-only until shared).

.EXAMPLE
    ./Scripts/Setup-Platform.ps1                          # evaluation: local dev Core
    ./Scripts/Setup-Platform.ps1 -BuildImages             # + build & register workflow images
    ./Scripts/Setup-Platform.ps1 -CoreUrl https://core.example.com -ApiKey $env:AUXILIA_KEY `
        -BuildImages -ImageTag 0.2.0 -IncludeWindows      # real deployment
#>
param(
    [string]$CoreUrl = "http://localhost:5280",
    [string]$ApiKey = "auxilia-steering-dev-key",
    [string]$ImageTag = "latest",
    [switch]$BuildImages,
    [switch]$SkipWorkflows,
    [switch]$IncludeWindows
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$headers = @{ Authorization = "Bearer $ApiKey" }
$CoreUrl = $CoreUrl.TrimEnd('/')

function Invoke-Core {
    param([string]$Method, [string]$Path, $Body = $null)
    $arguments = @{
        Method  = $Method
        Uri     = "$CoreUrl$Path"
        Headers = $headers
    }
    if ($null -ne $Body) {
        $arguments.ContentType = "application/json"
        $arguments.Body = ($Body | ConvertTo-Json -Depth 8)
    }
    Invoke-RestMethod @arguments
}

# --- 0. Reachability + key check -------------------------------------------------------------
Write-Host "Checking $CoreUrl ..." -ForegroundColor Cyan
try { Invoke-RestMethod "$CoreUrl/health" | Out-Null }
catch { throw "The Core at $CoreUrl is not reachable ($_). Start it first (Start-DevStack.ps1 for a local stack)." }
try { Invoke-Core GET "/api/workflow-types?take=1" | Out-Null }
catch { throw "The Core is up but the API key was rejected — pass a valid administrator key via -ApiKey." }
Write-Host "Core is up, key accepted." -ForegroundColor Green

# --- 1. Environment bases (common base boxes for linux AND windows) --------------------------
$bases = @(
    @{ name = "linux";   version = "ubuntu-24.04"; description = "Ubuntu 24.04 LTS" }
    @{ name = "linux";   version = "ubuntu-22.04"; description = "Ubuntu 22.04 LTS" }
    @{ name = "linux";   version = "debian-12";    description = "Debian 12 (bookworm)" }
    @{ name = "windows"; version = "server-2025";  description = "Windows Server 2025" }
    @{ name = "windows"; version = "server-2022";  description = "Windows Server 2022" }
)
$existingBases = @(Invoke-Core GET "/api/environment-bases")
foreach ($base in $bases) {
    if ($existingBases | Where-Object { $_.name -eq $base.name -and $_.version -eq $base.version }) {
        Write-Host "  base $($base.name)/$($base.version) already registered"
        continue
    }
    Invoke-Core POST "/api/environment-bases" $base | Out-Null
    Write-Host "  base $($base.name)/$($base.version) registered" -ForegroundColor Green
}

# --- 2. Environment layers (multi-base: linux always, windows variants opt-in) ---------------
# Each entry: capability name, software version, description, linux script, windows script.
# Scripts run at image-BUILD time with fail-fast semantics; keep them idempotent.
$layers = @(
    @{
        type = "dotnet-10"; version = "10.0"
        description = "The .NET 10 SDK preinstalled — build, test, and run .NET projects inside the session."
        linux = @'
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm /tmp/dotnet-install.sh
dotnet --list-sdks
'@
        windows = @'
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir 'C:\Program Files\dotnet'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\Program Files\dotnet', 'Machine')
& 'C:\Program Files\dotnet\dotnet' --list-sdks
'@
    }
    @{
        type = "dotnet-9"; version = "9.0"
        description = "The .NET 9 SDK preinstalled."
        linux = @'
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm /tmp/dotnet-install.sh
dotnet --list-sdks
'@
        windows = @'
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 9.0 -InstallDir 'C:\Program Files\dotnet'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\Program Files\dotnet', 'Machine')
& 'C:\Program Files\dotnet\dotnet' --list-sdks
'@
    }
    @{
        type = "dotnet-8"; version = "8.0"
        description = "The .NET 8 (LTS) SDK preinstalled."
        linux = @'
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm /tmp/dotnet-install.sh
dotnet --list-sdks
'@
        windows = @'
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir 'C:\Program Files\dotnet'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\Program Files\dotnet', 'Machine')
& 'C:\Program Files\dotnet\dotnet' --list-sdks
'@
    }
    @{
        type = "node-22"; version = "22"
        description = "Node.js 22 with npm preinstalled — build and run JavaScript/TypeScript projects inside the session."
        linux = @'
curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
apt-get install -y --no-install-recommends nodejs
rm -rf /var/lib/apt/lists/*
node --version && npm --version
'@
        windows = @'
Invoke-WebRequest https://nodejs.org/dist/v22.14.0/node-v22.14.0-win-x64.zip -OutFile $env:TEMP\node.zip
Expand-Archive $env:TEMP\node.zip -DestinationPath 'C:\'
Rename-Item 'C:\node-v22.14.0-win-x64' 'C:\nodejs'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\nodejs', 'Machine')
Remove-Item $env:TEMP\node.zip
& C:\nodejs\node.exe --version
'@
    }
    @{
        type = "node-20"; version = "20"
        description = "Node.js 20 (LTS) with npm preinstalled."
        linux = @'
curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
apt-get install -y --no-install-recommends nodejs
rm -rf /var/lib/apt/lists/*
node --version && npm --version
'@
        windows = @'
Invoke-WebRequest https://nodejs.org/dist/v20.18.3/node-v20.18.3-win-x64.zip -OutFile $env:TEMP\node.zip
Expand-Archive $env:TEMP\node.zip -DestinationPath 'C:\'
Rename-Item 'C:\node-v20.18.3-win-x64' 'C:\nodejs'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\nodejs', 'Machine')
Remove-Item $env:TEMP\node.zip
& C:\nodejs\node.exe --version
'@
    }
    @{
        type = "python-3.12"; version = "3.12"
        description = "Python 3.12 with pip and venv preinstalled — run Python tooling and projects inside the session."
        linux = @'
apt-get update
apt-get install -y --no-install-recommends python3 python3-pip python3-venv
rm -rf /var/lib/apt/lists/*
ln -sf /usr/bin/python3 /usr/local/bin/python
python --version && pip3 --version
'@
        windows = @'
Invoke-WebRequest https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe -OutFile $env:TEMP\python-setup.exe
Start-Process -Wait $env:TEMP\python-setup.exe -ArgumentList '/quiet','InstallAllUsers=1','PrependPath=1','Include_test=0'
Remove-Item $env:TEMP\python-setup.exe
'@
    }
    @{
        type = "java-21"; version = "21"
        description = "Eclipse Temurin JDK 21 preinstalled — build and run JVM projects inside the session."
        linux = @'
apt-get update && apt-get install -y --no-install-recommends wget apt-transport-https gpg
wget -qO - https://packages.adoptium.net/artifactory/api/gpg/key/public | gpg --dearmor -o /etc/apt/trusted.gpg.d/adoptium.gpg
echo "deb https://packages.adoptium.net/artifactory/deb $(. /etc/os-release && echo $VERSION_CODENAME) main" > /etc/apt/sources.list.d/adoptium.list
apt-get update && apt-get install -y --no-install-recommends temurin-21-jdk
rm -rf /var/lib/apt/lists/*
java --version
'@
        windows = @'
Invoke-WebRequest 'https://api.adoptium.net/v3/installer/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse' -OutFile $env:TEMP\temurin21.msi
Start-Process -Wait msiexec -ArgumentList '/i',"$env:TEMP\temurin21.msi",'/quiet','ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJavaHome'
Remove-Item $env:TEMP\temurin21.msi
'@
    }
    @{
        type = "go-1.23"; version = "1.23"
        description = "Go 1.23 toolchain preinstalled."
        linux = @'
curl -fsSL https://go.dev/dl/go1.23.4.linux-amd64.tar.gz -o /tmp/go.tar.gz
tar -C /usr/local -xzf /tmp/go.tar.gz
ln -sf /usr/local/go/bin/go /usr/local/bin/go
rm /tmp/go.tar.gz
go version
'@
        windows = @'
Invoke-WebRequest https://go.dev/dl/go1.23.4.windows-amd64.zip -OutFile $env:TEMP\go.zip
Expand-Archive $env:TEMP\go.zip -DestinationPath 'C:\'
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';C:\go\bin', 'Machine')
Remove-Item $env:TEMP\go.zip
'@
    }
    @{
        type = "rust-stable"; version = "stable"
        description = "Rust stable toolchain (rustup, cargo) preinstalled."
        linux = @'
apt-get update && apt-get install -y --no-install-recommends curl ca-certificates gcc libc6-dev
rm -rf /var/lib/apt/lists/*
curl --proto '=https' --tlsv1.2 -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable
ln -sf /root/.cargo/bin/* /usr/local/bin/
cargo --version
'@
        windows = @'
Invoke-WebRequest https://win.rustup.rs/x86_64 -OutFile $env:TEMP\rustup-init.exe
& $env:TEMP\rustup-init.exe -y --profile minimal --default-toolchain stable
[Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';' + $env:USERPROFILE + '\.cargo\bin', 'Machine')
Remove-Item $env:TEMP\rustup-init.exe
'@
    }
    @{
        type = "cpp-toolchain"; version = $null
        description = "C/C++ build toolchain: gcc, make, cmake, ninja."
        linux = @'
apt-get update
apt-get install -y --no-install-recommends build-essential cmake ninja-build
rm -rf /var/lib/apt/lists/*
gcc --version && cmake --version
'@
        windows = $null  # a full MSVC install is too heavy for a build fragment — curate manually.
    }
)
foreach ($layer in $layers) {
    Invoke-Core POST "/api/environment-layers" @{
        providerType    = $layer.type
        description     = $layer.description
        setupScript     = $layer.linux
        baseEnvironment = "linux"
        version         = $layer.version
    } | Out-Null
    Write-Host "  layer $($layer.type) (linux) upserted" -ForegroundColor Green
    if ($IncludeWindows -and $layer.windows) {
        Invoke-Core POST "/api/environment-layers" @{
            providerType    = $layer.type
            description     = $layer.description
            setupScript     = $layer.windows
            baseEnvironment = "windows"
            version         = $layer.version
        } | Out-Null
        Write-Host "  layer $($layer.type) (windows) upserted" -ForegroundColor Green
    }
}

# --- 3. Slot providers (register if absent, then make available) -----------------------------
# Mirrors the plugin manifests (Source/Slots/*/**.slothandler.manifest.json) plus the
# data-only providers nothing else creates. Registration is deny-by-default; availability
# is the curation act.
$providers = @(
    @{
        providerType = "claude-code-cli"; category = "coding-agent"
        description = "Runs the Claude Code CLI as the coding agent of a workflow; credentials reach the agent container just-in-time."
        contracts = @("Auxilia.Workflows.AiAgent.CodingAgent.ICodingAgent")
        settings = @(
            @{ key = "OAuthToken"; label = "Claude account"; kind = "Secret"; connectFlow = "anthropic-claude"; helpText = "The connected Claude account the CLI runs with." }
            @{ key = "OAuthRefreshToken"; label = "Refresh token"; kind = "Secret"; helpText = "Captured by Connect - lets the Core refresh the access token at delivery time." }
            @{ key = "OAuthExpiresAt"; label = "Token expiry (internal)"; kind = "Text"; helpText = "Unix millis; maintained automatically by the Core on each refresh." }
            @{ key = "ApiKey"; label = "Anthropic API key (fallback)"; kind = "Secret"; helpText = "Fallback when no Claude account is connected." }
            @{ key = "Model"; label = "Model"; kind = "Text"; browse = "models"; helpText = "Optional model override." }
            @{ key = "CliPath"; label = "CLI path (advanced)"; kind = "Text"; defaultValue = "claude" }
        )
        oAuthRefresh = @{
            tokenEndpoint = "https://console.anthropic.com/v1/oauth/token"
            clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
            accessTokenKey = "OAuthToken"; refreshTokenKey = "OAuthRefreshToken"; expiresAtKey = "OAuthExpiresAt"
        }
        modelCatalog = @{
            endpoint = "https://api.anthropic.com/v1/models?limit=100"
            itemsPath = "data"; idField = "id"; labelField = "display_name"
            headers = @{ "anthropic-version" = "2023-06-01" }
            apiKeyHeader = "x-api-key"; apiKeySettingKey = "ApiKey"; bearerSettingKey = "OAuthToken"
            bearerHeaders = @{ "anthropic-beta" = "oauth-2025-04-20" }
        }
    }
    @{
        providerType = "github-copilot-cli"; category = "coding-agent"
        description = "Runs the GitHub Copilot CLI as the coding agent of a workflow; the token reaches the agent container just-in-time."
        contracts = @("Auxilia.Workflows.AiAgent.CodingAgent.ICodingAgent")
        settings = @(
            @{ key = "token"; label = "GitHub account"; kind = "Secret"; required = $true; connectFlow = "github"; helpText = "A GitHub token with Copilot access." }
            @{ key = "UseSdkSession"; label = "Interactive session (SDK)"; kind = "Boolean"; helpText = "Full interactivity via the Copilot SDK. Off = plain headless mode." }
            @{ key = "Model"; label = "Model"; kind = "Text"; browse = "models" }
            @{ key = "CliPath"; label = "CLI path (advanced)"; kind = "Text"; defaultValue = "copilot" }
        )
    }
    @{
        providerType = "codex-cli"; category = "coding-agent"
        description = "Runs the OpenAI Codex CLI as the coding agent of a workflow; the API key reaches the agent container just-in-time."
        contracts = @("Auxilia.Workflows.AiAgent.CodingAgent.ICodingAgent")
        settings = @(
            @{ key = "ApiKey"; label = "OpenAI API key"; kind = "Secret"; required = $true; helpText = "An OpenAI API key with Codex access." }
            @{ key = "Model"; label = "Model"; kind = "Text" }
            @{ key = "CliPath"; label = "CLI path (advanced)"; kind = "Text"; defaultValue = "codex" }
        )
    }
    @{
        providerType = "github-account"; category = "account"
        description = "Your GitHub account. One connection serves ALL its repositories: live repository/branch lists, just-in-time clone auth - the credential never enters a container."
        contracts = @("git-credential")
        settings = @(
            @{ key = "token"; label = "GitHub account"; kind = "Secret"; required = $true; connectFlow = "github" }
        )
    }
    @{
        providerType = "tfs-account"; category = "account"
        description = "A TFS / Azure DevOps account (personal access token). One connection serves all repositories the account can reach; resolved just-in-time, never inside a container."
        contracts = @("git-credential")
        settings = @(
            @{ key = "token"; label = "Personal access token"; kind = "Secret"; required = $true; helpText = "A TFS/Azure DevOps PAT with code-read (and code-write if the workflow pushes)." }
            @{ key = "OrgUrl"; label = "Organization / collection URL"; kind = "Text"; required = $true; helpText = "e.g. https://dev.azure.com/my-org or https://tfs.company.com/tfs/DefaultCollection" }
            @{ key = "username"; label = "Username (optional)"; kind = "Text" }
        )
    }
    @{
        providerType = "git-repository"; category = "workspace"
        description = "A git repository the run works on: cloned into the workspace before launch, authenticated by a connected account, picked live from that account."
        contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess")
        requiredCredentialContract = "git-credential"
        mountsIntoWorkspace = $true
        settings = @(
            @{ key = "CloneUrl"; label = "Repository"; kind = "Text"; required = $true; role = "clone-url"; browse = "repositories" }
            @{ key = "Branch"; label = "Branch"; kind = "Text"; role = "branch"; browse = "branches"; browseDependsOn = "CloneUrl" }
            @{ key = "WorkingDirectory"; label = "Working directory"; kind = "Text"; role = "working-directory" }
            @{ key = "NoCache"; label = "Fresh clone every run"; kind = "Boolean"; role = "no-cache" }
            @{ key = "AllowPush"; label = "Allow the agent to push"; kind = "Boolean"; role = "allow-push" }
            @{ key = "CommitName"; label = "Commit author name"; kind = "Text"; role = "commit-name" }
            @{ key = "CommitEmail"; label = "Commit author e-mail"; kind = "Text"; role = "commit-email" }
        )
    }
    @{
        providerType = "empty-workspace"; category = "workspace"
        description = "A fresh, empty scratch directory materialized into the run's workspace - no clone, no credential."
        contracts = @()
        mountsIntoWorkspace = $true
        settings = @(
            @{ key = "WorkingDirectory"; label = "Working directory"; kind = "Text"; role = "working-directory" }
            @{ key = "SetupScript"; label = "Setup script"; kind = "Text"; role = "setup-script" }
        )
    }
    @{
        providerType = "coding-session-workspace"; category = "coding-session"
        description = "Backs a session with the mounted workspace directory."
        contracts = @("Auxilia.Workflows.SourceControl.ISourceControlAccess", "Auxilia.Workflows.TaskSource.IWorkItemAccess")
        settings = @(
            @{ key = "WorkingPath"; label = "Working path"; kind = "Text" }
        )
    }
)
$catalog = @((Invoke-Core GET "/api/provider-catalog?take=200").items)
foreach ($provider in $providers) {
    $existing = $catalog | Where-Object providerType -eq $provider.providerType
    if (-not $existing) {
        Invoke-Core POST "/api/provider-catalog" $provider | Out-Null
        Write-Host "  provider $($provider.providerType) registered" -ForegroundColor Green
    }
    else {
        Write-Host "  provider $($provider.providerType) already registered"
    }
    if (-not $existing -or $existing.available -ne $true) {
        Invoke-Core POST "/api/provider-catalog/$($provider.providerType)/availability" @{ available = $true } | Out-Null
        Write-Host "  provider $($provider.providerType) made available" -ForegroundColor Green
    }
}

# --- 4. Default workflow types ---------------------------------------------------------------
if (-not $SkipWorkflows) {
    # One CLI-session workflow serves ALL THREE agents: the coding-agent slot decides at
    # configuration time whether the session runs Claude Code, Copilot, or Codex.
    $workflows = @(
        @{ type = "coding-session";           project = "Auxilia.CodingSession.Workflow";  image = "auxilia-coding-session-workflow" }
        @{ type = "claude-code";              project = "Auxilia.ClaudeCode.Workflow";     image = "auxilia-claude-code-workflow" }
        @{ type = "github-copilot";           project = "Auxilia.Copilot.Workflow";        image = "auxilia-copilot-workflow" }
        @{ type = "implementation";           project = "Auxilia.Implementation.Workflow"; image = "auxilia-implementation-workflow" }
        @{ type = "pull-request-code-review"; project = "Auxilia.CodeReview.Workflow";     image = "auxilia-code-review-workflow" }
        @{ type = "session-notifier";         project = "Auxilia.SessionNotifier.Workflow"; image = "auxilia-session-notifier-workflow" }
    )
    if ($BuildImages) {
        foreach ($workflow in $workflows) {
            Write-Host "Building $($workflow.image):$ImageTag ..." -ForegroundColor Cyan
            docker build -t "$($workflow.image):$ImageTag" `
                -f "$repo\Source\Workflows\$($workflow.project)\Dockerfile" $repo
            if ($LASTEXITCODE -ne 0) { throw "docker build failed for $($workflow.project)" }
        }
    }
    $registered = @((Invoke-Core GET "/api/workflow-types?take=200").items)
    foreach ($workflow in $workflows) {
        if ($registered | Where-Object workflowType -eq $workflow.type) {
            Write-Host "  workflow type $($workflow.type) already registered"
            continue
        }
        $result = Invoke-Core POST "/api/workflow-types" @{
            workflowType = $workflow.type
            packageUri   = "docker://$($workflow.image):$ImageTag"
        }
        if ($result.status -eq "Pending") {
            # A docker:// coordinate carries no verifiable signature — the registering
            # administrator IS the signing authority here, so approve in the same breath.
            Invoke-Core POST "/api/workflow-types/$($workflow.type)/approve" @{} | Out-Null
        }
        Write-Host "  workflow type $($workflow.type) registered ($($workflow.image):$ImageTag)" -ForegroundColor Green
    }
}

# --- 5. What is left for the operator --------------------------------------------------------
Write-Host ""
Write-Host "Setup complete. Remaining manual steps:" -ForegroundColor Cyan
Write-Host "  * Create connectors for your accounts (GitHub / TFS / Azure DevOps / Claude / Copilot / Codex)"
Write-Host "    in the AdminConsole - they carry credentials, so they are never scripted."
Write-Host "  * Under the restricted default posture, new layers and providers are administrators-only:"
Write-Host "    grant them to groups/users via Sharing, or set security.default-resource-access=open"
Write-Host "    in /admin/settings (needs step-up elevation)."
if (-not $BuildImages -and -not $SkipWorkflows) {
    Write-Host "  * Workflow types were registered against docker://<image>:$ImageTag - make sure those"
    Write-Host "    images exist on every runner host (re-run with -BuildImages to build them here)."
}
