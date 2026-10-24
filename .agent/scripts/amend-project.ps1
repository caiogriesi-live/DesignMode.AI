param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$DraftProject = Join-Path $RepoRoot ".agent/projects/draft/$ProjectId.md"
$ApprovedProject = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"
$RunningState = Join-Path $RepoRoot ".agent/projects/running/$ProjectId/state.json"

Write-Host ""
Write-Host "DESIGNMODE PROJECT AMENDMENT"
Write-Host "============================"
Write-Host "Project: $ProjectId"
Write-Host ""

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    throw "ProjectId cannot be empty."
}

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

if (!(Test-Path -LiteralPath $DraftProject -PathType Leaf)) {
    throw "Revised draft project not found: $DraftProject"
}

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Existing approved project not found: $ApprovedProject"
}

$DraftText = Get-Content -LiteralPath $DraftProject -Raw

if ($DraftText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    throw "Revised project is not READY_FOR_APPROVAL."
}

# Do not silently amend a project while engineering is executing.
if (Test-Path -LiteralPath $RunningState -PathType Leaf) {
    $State = Get-Content -LiteralPath $RunningState -Raw | ConvertFrom-Json

    $Executing = @(
        $State.workItems |
            Where-Object {
                $_.status -in @(
                    "PLANNING",
                    "RUNNING"
                )
            }
    )

    if ($Executing.Count -gt 0) {
        Write-Host "ERROR: Project currently has executing work:"
        foreach ($Item in $Executing) {
            Write-Host "$($Item.id): $($Item.status)"
        }
        exit 20
    }
}

# Preserve the previous approved contract before replacement.
$HistoryDir = Join-Path $RepoRoot ".agent/projects/history/$ProjectId"
New-Item -ItemType Directory -Force -Path $HistoryDir | Out-Null

$Timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$PreviousApproved = Join-Path $HistoryDir "$Timestamp.md"

Copy-Item `
    -LiteralPath $ApprovedProject `
    -Destination $PreviousApproved

Copy-Item `
    -LiteralPath $DraftProject `
    -Destination $ApprovedProject `
    -Force

$ApprovedText = Get-Content -LiteralPath $ApprovedProject -Raw

if ($ApprovedText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    Copy-Item `
        -LiteralPath $PreviousApproved `
        -Destination $ApprovedProject `
        -Force

    throw "Amendment verification failed; previous approved project restored."
}

Write-Host ""
Write-Host "========================================"
Write-Host "PROJECT AMENDMENT APPROVED"
Write-Host "========================================"
Write-Host ""
Write-Host "Approved project:"
Write-Host $ApprovedProject
Write-Host ""
Write-Host "Previous approved version:"
Write-Host $PreviousApproved
Write-Host ""
Write-Host "IMPORTANT:"
Write-Host "Existing runtime state has NOT been migrated."
Write-Host "Existing engineering tasks have NOT been approved."
Write-Host "No engineering started."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."