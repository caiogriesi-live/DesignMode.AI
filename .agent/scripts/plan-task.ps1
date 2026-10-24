param(
    [Parameter(Mandatory = $true)]
    [string]$TaskId,

    [Parameter(Mandatory = $true)]
    [string]$Request
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$PlannerPrompt = Join-Path $RepoRoot ".agent/prompts/planner.md"
$DraftDir = Join-Path $RepoRoot ".agent/tasks/draft"
$DraftTask = Join-Path $DraftDir "$TaskId.md"

$PlannerResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"
$PlannerOutput = Join-Path $PlannerResultDir "planner-codex-output.txt"

# ==================================================
# HEADER
# ==================================================

Write-Host ""
Write-Host "DESIGNMODE AUTO PLANNER"
Write-Host "======================="
Write-Host "Task: $TaskId"
Write-Host ""
Write-Host "Request:"
Write-Host $Request
Write-Host ""

# ==================================================
# VALIDATION
# ==================================================

if (!(Test-Path $PlannerPrompt)) {
    throw "Planner prompt not found: $PlannerPrompt"
}

if ([string]::IsNullOrWhiteSpace($TaskId)) {
    throw "TaskId cannot be empty."
}

if ([string]::IsNullOrWhiteSpace($Request)) {
    throw "Request cannot be empty."
}

# Keep TaskId safe for filenames / orchestration.
if ($TaskId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "TaskId may contain only letters, numbers, dots, underscores, and hyphens."
}

New-Item -ItemType Directory -Force -Path $DraftDir | Out-Null
New-Item -ItemType Directory -Force -Path $PlannerResultDir | Out-Null

# ==================================================
# DO NOT SILENTLY OVERWRITE AN EXISTING DRAFT
# ==================================================

if (Test-Path $DraftTask) {

    Write-Host "ERROR: Draft task already exists:"
    Write-Host $DraftTask
    Write-Host ""
    Write-Host "Delete it or use a different TaskId."

    exit 10
}

# ==================================================
# PROMPT
# ==================================================

$Prompt = @"
Act as the Planner Agent defined in:

.agent/prompts/planner.md


==================================================
HUMAN REQUEST
==================================================

Task ID:

$TaskId


Request:

$Request


==================================================
REPOSITORY CONTEXT
==================================================

Repository root:

$RepoRoot


You may inspect the repository to understand:

- existing architecture
- relevant pages/components
- existing UI patterns
- existing tests
- existing data-testid conventions
- build/test structure
- likely protected systems

Repository inspection is READ-ONLY for planning purposes.

Do NOT modify application code.

Do NOT create a feature branch.

Do NOT commit.

Do NOT push.

Do NOT merge.

Do NOT deploy.


==================================================
PLANNING RULES
==================================================

Convert the human request into a precise engineering task.

The task must describe WHAT needs to be accomplished.

Avoid unnecessary implementation prescriptions.

Create independently testable acceptance criteria.

For UI work, include appropriate desktop and mobile verification.

Use stable data-testid selectors when they materially improve automated QA.

Do not invent product requirements that are not reasonably implied by the
human request.

If an important decision is genuinely ambiguous and cannot safely be inferred,
do NOT guess.

Instead create the draft with:

STATUS: NEEDS_CLARIFICATION

and include a section:

## Clarification Required

containing the specific questions for the human.


==================================================
OUTPUT
==================================================

Write the generated task directly to:

.agent/tasks/draft/$TaskId.md


Follow the task format defined in:

.agent/prompts/planner.md


The final section must contain exactly one of:

STATUS: READY_FOR_APPROVAL

or:

STATUS: NEEDS_CLARIFICATION


Do not execute the task.

Do not move the task to approved.

Do not invoke Developer.

Do not invoke QA.

Do not wait for interactive human instructions.

You are being executed non-interactively by the DesignMode autonomous
engineering planner.
"@

# ==================================================
# RUN PLANNER
# ==================================================

Write-Host "Starting Planner Agent..."
Write-Host ""

$Prompt | codex exec `
    -C $RepoRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $PlannerOutput `
    -

$ExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Codex exit code: $ExitCode"

if ($ExitCode -ne 0) {

    Write-Host ""
    Write-Host "PLANNER AGENT FAILED"

    exit $ExitCode
}

# ==================================================
# VERIFY OUTPUT
# ==================================================

if (!(Test-Path $DraftTask)) {

    Write-Host ""
    Write-Host "ERROR: Planner completed but did not create:"
    Write-Host $DraftTask

    exit 20
}

$DraftText = Get-Content $DraftTask -Raw

# ==================================================
# ROUTE RESULT
# ==================================================

if ($DraftText -match
    '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {

    Write-Host ""
    Write-Host "========================================"
    Write-Host "TASK READY FOR APPROVAL"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "Draft:"
    Write-Host $DraftTask
    Write-Host ""
    Write-Host "Review the draft before approving it."

}
elseif ($DraftText -match
    '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {

    Write-Host ""
    Write-Host "========================================"
    Write-Host "PLANNER NEEDS CLARIFICATION"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "Review:"
    Write-Host $DraftTask
    Write-Host ""
    Write-Host "Answer the questions before execution."

}
else {

    Write-Host ""
    Write-Host "ERROR: Planner returned an unknown STATUS."
    Write-Host ""
    Write-Host "Review:"
    Write-Host $DraftTask

    exit 21
}

Write-Host ""
Write-Host "PLANNER STAGE COMPLETE"