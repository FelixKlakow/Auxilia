<#
.SYNOPSIS
    Runs the system test suite and opens the Auxilia dashboard in the browser
    as soon as a Backend Service container becomes reachable.

.DESCRIPTION
    Builds the solution, starts the requested system tests, and polls Docker for
    Backend Service containers (image auxilia-backendservice:system-test) that
    publish their HTTP port. Each new one is opened in the default browser, so
    you can watch a run live — e.g. the EndToEnd test's mail-triggered Code
    Review (dashboard login there: admin / e2e-admin-pw).

    Docker images are rebuilt by the suite itself unless the .prebuilt-images
    marker exists in the repository root (see TestStrategy.md).

.PARAMETER Filter
    dotnet test --filter expression. Defaults to the EndToEnd acceptance test,
    which exercises the full dashboard; use 'Category=System' for the whole suite.

.PARAMETER NoBrowser
    Only print the dashboard URLs instead of opening a browser.

.PARAMETER NoBuild
    Skip the solution build (use when binaries are already up to date).

.EXAMPLE
    ./Scripts/Run-SystemTests.ps1
    ./Scripts/Run-SystemTests.ps1 -Filter 'Category=System'
#>
[CmdletBinding()]
param(
    [string] $Filter = 'FullyQualifiedName~EndToEnd',
    [switch] $NoBrowser,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try
{
    if (-not $NoBuild)
    {
        dotnet build Auxilia.slnx
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $test = Start-Process dotnet `
        -ArgumentList 'test', 'Auxilia.SystemTestSuite/', '--filter', $Filter, '--no-build' `
        -NoNewWindow -PassThru

    # While the tests run, surface every Backend Service container that publishes
    # its dashboard port. Containers come and go with their environments, so keep
    # polling until the test process exits.
    $seen = @{}
    while (-not $test.HasExited)
    {
        Start-Sleep -Seconds 2
        $containers = docker ps --filter 'ancestor=auxilia-backendservice:system-test' --format '{{.ID}}' 2>$null
        foreach ($id in @($containers | Where-Object { $_ -and -not $seen.ContainsKey($_) }))
        {
            $mapping = docker port $id 8080 2>$null | Select-Object -First 1
            if ($mapping -match ':(\d+)$')
            {
                $seen[$id] = $true
                $url = "http://localhost:$($Matches[1])"
                Write-Host "`n==> Dashboard reachable: $url  (EndToEnd login: admin / e2e-admin-pw)`n" -ForegroundColor Green
                if (-not $NoBrowser) { Start-Process $url }
            }
        }
    }

    exit $test.ExitCode
}
finally
{
    Pop-Location
}
