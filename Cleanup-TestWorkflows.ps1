# Removes TESTING leftovers from the dev Core so the steering client starts with a clean catalog:
#  - run configurations of the Workflows.Testing family or tagged 'test'
#  - the testing workflow-type registrations themselves
#  - all finished runs
# Run this after any test/verification session against the dev stack. Real configurations
# (untagged, non-testing types) are never touched.
param(
    [string]$CoreUrl = "http://localhost:5280",
    [string]$ApiKey  = "auxilia-steering-dev-key", # well-known local dev bootstrap key, not a secret
    [string[]]$TestTypes = @(
        "echo-workflow", "echo-choice-workflow", "echo-decision-workflow",
        "sleeping-workflow", "crashing-workflow", "steering-sample")
)

$headers = @{ Authorization = "Bearer $ApiKey" }

$configurations = (Invoke-RestMethod "$CoreUrl/api/configurations?take=200" -Headers $headers).items
foreach ($configuration in $configurations) {
    if ($TestTypes -contains $configuration.workflowType -or $configuration.tags -contains "test") {
        Invoke-RestMethod -Method Delete "$CoreUrl/api/configurations/$($configuration.id)" -Headers $headers | Out-Null
        Write-Host "Deleted configuration '$($configuration.name)' ($($configuration.workflowType))."
    }
}

foreach ($type in $TestTypes) {
    try {
        Invoke-RestMethod -Method Delete "$CoreUrl/api/workflow-types/$type" -Headers $headers -ErrorAction Stop | Out-Null
        Write-Host "Unregistered workflow type '$type'."
    } catch {
        # Not registered — nothing to clean.
    }
}

Invoke-RestMethod -Method Delete "$CoreUrl/api/runs" -Headers $headers | Out-Null
Write-Host "Cleared finished runs."
