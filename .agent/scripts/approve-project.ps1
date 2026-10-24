param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$DraftDir = Join-Path $RepoRoot ".agent/projects/draft"
$ApprovedDir = Join-Path $RepoRoot ".agent/projects/approved"

$DraftProject = Join-Path $DraftDir "$ProjectId.md"
$ApprovedProject = Join-Path $ApprovedDir "$ProjectId.md"

Write-Host ""
Write-Host "DESIGNMODE PROJECT APPROVAL"
Write-Host "==========================="
Write-Host "Project: $ProjectId"
Write-Host ""

# ==================================================
# VALIDATION
# ==================================================

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    throw "ProjectId cannot be empty."
}

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

if (!(Test-Path -LiteralPath $DraftProject -PathType Leaf)) {
    throw "Draft project not found: $DraftProject"
}

if (Test-Path -LiteralPath $ApprovedProject) {
    Write-Host "ERROR: Approved project already exists:"
    Write-Host $ApprovedProject
    exit 10
}

$ProjectText = Get-Content -LiteralPath $DraftProject -Raw

if ($ProjectText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    Write-Host "ERROR: Project is not READY_FOR_APPROVAL."
    Write-Host ""
    Write-Host "Approval requires:"
    Write-Host "STATUS: READY_FOR_APPROVAL"
    exit 20
}

if ($ProjectText -match '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {
    Write-Host "ERROR: Project still requires clarification."
    exit 21
}

# ==================================================
# APPROVE
# ==================================================

New-Item -ItemType Directory -Force -Path $ApprovedDir | Out-Null

Copy-Item `
    -LiteralPath $DraftProject `
    -Destination $ApprovedProject

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Failed to create approved project."
}

$ApprovedText = Get-Content -LiteralPath $ApprovedProject -Raw

if ($ApprovedText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    Remove-Item -LiteralPath $ApprovedProject -Force
    throw "Approved project verification failed."
}

Write-Host ""
Write-Host "========================================"
Write-Host "PROJECT APPROVED"
Write-Host "========================================"
Write-Host ""
Write-Host "Approved project:"
Write-Host $ApprovedProject
Write-Host ""
Write-Host "IMPORTANT:"
Write-Host "Approval does NOT start engineering."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."
Write-Host ""
Write-Host "PROJECT APPROVAL STAGE COMPLETE"