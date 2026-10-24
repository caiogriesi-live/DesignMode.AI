param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $false)]
    [string]$HistoricalContext = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$PromptFile = Join-Path $RepoRoot ".agent/prompts/project-reconciler.md"
$ApprovedProject = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/project-reconciliation/$ProjectId"
$ResultFile = Join-Path $ResultDir "reconciliation.md"
$CodexOutput = Join-Path $ResultDir "codex-output.txt"

if (!(Test-Path -LiteralPath $PromptFile -PathType Leaf)) {
    throw "Project reconciler prompt not found."
}

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Approved project not found."
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$Prompt = @"
Act as the Project Reconciliation Agent defined in:

.agent/prompts/project-reconciler.md

Project:

$ProjectId

Approved project:

.agent/projects/approved/$ProjectId.md

==================================================
HUMAN HISTORICAL CONTEXT
==================================================

$HistoricalContext

==================================================
INSTRUCTIONS
==================================================

Perform a deep read-only repository reconciliation.

Do not assume the historical context is complete or currently accurate.
Verify it against repository evidence.

Pay particular attention to existing:

- marketing attribution
- UTM capture
- gclid, gbraid and wbraid capture
- campaign, ad group, ad, keyword and acquisition fields
- attribution snapshots
- database persistence
- Stripe purchase measurement
- subscription and AI credit purchase data
- Google measurement/conversion infrastructure
- Google Ads configuration structure
- marketing/dashboard UI
- landing pages and funnel experiences
- social-content targeting or messaging
- tests covering these systems

Determine whether each approved WORK item should be kept, reduced, considered
already complete, replanned, or blocked on configuration.

Write only the reconciliation report to:

.agent/results/project-reconciliation/$ProjectId/reconciliation.md

Do not modify the approved project.
Do not modify engineering tasks.
Do not implement anything.
Do not commit.
Do not push.
Do not merge.
Do not deploy.
"@

Write-Host ""
Write-Host "DESIGNMODE PROJECT RECONCILIATION"
Write-Host "================================="
Write-Host "Project: $ProjectId"
Write-Host ""

$Prompt | codex exec `
    -C $RepoRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $CodexOutput `
    -

$ExitCode = $LASTEXITCODE

if ($ExitCode -ne 0) {
    Write-Host "PROJECT RECONCILIATION FAILED"
    exit $ExitCode
}

if (!(Test-Path -LiteralPath $ResultFile -PathType Leaf)) {
    throw "Reconciliation report was not created."
}

$Result = Get-Content -LiteralPath $ResultFile -Raw

if ($Result -match '(?im)^STATUS:\s*READY_FOR_PROJECT_REVISION\s*$') {
    Write-Host ""
    Write-Host "RECONCILIATION READY FOR PROJECT REVISION"
    exit 0
}

if ($Result -match '(?im)^STATUS:\s*HUMAN_REQUIRED\s*$') {
    Write-Host ""
    Write-Host "RECONCILIATION REQUIRES HUMAN INPUT"
    exit 20
}

throw "Unknown reconciliation status."