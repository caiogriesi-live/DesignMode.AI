param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$Clarification
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$ProjectManagerPrompt = Join-Path $RepoRoot ".agent/prompts/project-manager.md"
$DraftProject = Join-Path $RepoRoot ".agent/projects/draft/$ProjectId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/projects/$ProjectId"
$RevisionOutput = Join-Path $ResultDir "project-manager-revision-codex-output.txt"

if (!(Test-Path -LiteralPath $ProjectManagerPrompt -PathType Leaf)) {
    throw "Project Manager prompt not found: $ProjectManagerPrompt"
}

if (!(Test-Path -LiteralPath $DraftProject -PathType Leaf)) {
    throw "Draft project not found: $DraftProject"
}

if ([string]::IsNullOrWhiteSpace($Clarification)) {
    throw "Clarification cannot be empty."
}

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$Prompt = @"
Act as the Project Manager Agent defined in:

.agent/prompts/project-manager.md


==================================================
EXISTING PROJECT
==================================================

Project ID:

$ProjectId

Existing draft:

.agent/projects/draft/$ProjectId.md


Read the complete existing draft before revising it.


==================================================
HUMAN CLARIFICATION
==================================================

$Clarification


==================================================
REVISION RULES
==================================================

Update the existing project plan using the human clarification.

Preserve valid repository findings, scope constraints, protected systems,
milestones, risks, and work items unless the clarification requires them
to change.

Resolve clarification questions that the human has answered.

Do not invent additional business rules.

If all material ambiguity has been resolved, remove the
Clarification Required section and finish with:

STATUS: READY_FOR_APPROVAL

If material ambiguity still remains, retain only the unresolved questions
and finish with:

STATUS: NEEDS_CLARIFICATION

Repository inspection remains READ-ONLY.

Do not modify application code.

Do not invoke Planner, Developer, QA, Triage, Repair, or Regression.

Do not create or switch branches.

Do not commit.

Do not push.

Do not merge.

Do not deploy.


==================================================
OUTPUT
==================================================

Rewrite the project specification directly at:

.agent/projects/draft/$ProjectId.md

Do not create another project file.

Do not wait for interactive instructions.
"@

Write-Host ""
Write-Host "DESIGNMODE PROJECT REVISION"
Write-Host "==========================="
Write-Host "Project: $ProjectId"
Write-Host ""

$Prompt | codex exec `
    -C $RepoRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $RevisionOutput `
    -

$ExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Codex exit code: $ExitCode"

if ($ExitCode -ne 0) {
    Write-Host "PROJECT REVISION FAILED"
    exit $ExitCode
}

$ProjectText = Get-Content -LiteralPath $DraftProject -Raw

if ($ProjectText -match '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "PROJECT READY FOR APPROVAL"
    Write-Host "========================================"
}
elseif ($ProjectText -match '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "PROJECT STILL NEEDS CLARIFICATION"
    Write-Host "========================================"
}
else {
    Write-Host ""
    Write-Host "ERROR: Project Manager returned an unknown STATUS."
    exit 21
}

Write-Host ""
Write-Host "Project:"
Write-Host $DraftProject
Write-Host ""
Write-Host "PROJECT REVISION STAGE COMPLETE"