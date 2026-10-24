param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$WorkItemId,

    [Parameter(Mandatory = $true)]
    [string]$TaskId
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$PlannerPrompt = Join-Path $RepoRoot ".agent/prompts/planner.md"
$ProjectFile = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"
$TaskFile = Join-Path $RepoRoot ".agent/tasks/draft/$TaskId.md"
$ReviewFile = Join-Path $RepoRoot ".agent/results/task-reviews/$TaskId/review.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/task-revisions/$TaskId"
$CodexOutput = Join-Path $ResultDir "codex-output.txt"

foreach ($Path in @($PlannerPrompt, $ProjectFile, $TaskFile, $ReviewFile)) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file not found: $Path"
    }
}

$Review = Get-Content -LiteralPath $ReviewFile -Raw

if ($Review -notmatch '(?im)^STATUS:\s*REVISION_REQUIRED\s*$') {
    throw "Latest task review is not REVISION_REQUIRED."
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$Prompt = @"
Act as the Planner Agent defined in:

.agent/prompts/planner.md

You are revising an existing engineering task after independent review.

Project:
$ProjectId

Assigned work item:
$WorkItemId

Approved parent project:
.agent/projects/approved/$ProjectId.md

Existing draft task:
.agent/tasks/draft/$TaskId.md

Independent review:
.agent/results/task-reviews/$TaskId/review.md

==================================================
REVISION RULES
==================================================

Read the approved project, existing draft, complete independent review, and
relevant repository evidence.

Correct every REQUIRED_CHANGES item from the independent review.

Preserve portions of the task that the reviewer found accurate.

Do not expand scope.

Do not invent new product or business requirements.

If the review identifies an issue that actually requires a new human
business/product/security decision, do not guess. Finish the revised task with:

STATUS: NEEDS_CLARIFICATION

Otherwise finish with:

STATUS: READY_FOR_APPROVAL

Write the revised task directly to:

.agent/tasks/draft/$TaskId.md

Do not modify the approved project.
Do not implement application code.
Do not create or switch branches.
Do not commit.
Do not push.
Do not merge.
Do not deploy.
Do not invoke Developer.
Do not invoke QA.
"@

Write-Host ""
Write-Host "DESIGNMODE TASK REVISION"
Write-Host "========================"
Write-Host "Project:   $ProjectId"
Write-Host "Work item: $WorkItemId"
Write-Host "Task:      $TaskId"
Write-Host ""

$Prompt | codex exec `
    -C $RepoRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $CodexOutput `
    -

$ExitCode = $LASTEXITCODE

if ($ExitCode -ne 0) {
    Write-Host "TASK REVISION FAILED"
    exit $ExitCode
}

$Revised = Get-Content -LiteralPath $TaskFile -Raw

if ($Revised -match '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    Write-Host ""
    Write-Host "TASK REVISION READY FOR REVIEW"
    exit 0
}

if ($Revised -match '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {
    Write-Host ""
    Write-Host "TASK REVISION REQUIRES HUMAN CLARIFICATION"
    exit 20
}

throw "Unknown revised task status."