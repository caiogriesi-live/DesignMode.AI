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

$ReviewerPrompt = Join-Path $RepoRoot ".agent/prompts/task-reviewer.md"
$ApprovedProject = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"
$DraftTask = Join-Path $RepoRoot ".agent/tasks/draft/$TaskId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/task-reviews/$TaskId"
$ReviewFile = Join-Path $ResultDir "review.md"
$CodexOutput = Join-Path $ResultDir "codex-output.txt"

if (!(Test-Path -LiteralPath $ReviewerPrompt -PathType Leaf)) {
    throw "Task Reviewer prompt not found."
}

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Approved project not found."
}

if (!(Test-Path -LiteralPath $DraftTask -PathType Leaf)) {
    throw "Draft task not found."
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$Prompt = @"
Act as the independent Engineering Task Reviewer defined in:

.agent/prompts/task-reviewer.md

Review:

Project:
.agent/projects/approved/$ProjectId.md

Assigned work item:
$WorkItemId

Proposed engineering task:
.agent/tasks/draft/$TaskId.md

Inspect the repository independently.

Verify repository claims rather than trusting the Planner.

Do not modify the project or task.

Write only the review to:

.agent/results/task-reviews/$TaskId/review.md

Do not implement anything.
Do not commit.
Do not push.
Do not merge.
Do not deploy.
"@

Write-Host ""
Write-Host "DESIGNMODE TASK REVIEW"
Write-Host "======================"
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
    Write-Host "TASK REVIEW FAILED"
    exit $ExitCode
}

if (!(Test-Path -LiteralPath $ReviewFile -PathType Leaf)) {
    throw "Reviewer did not create review.md."
}

$Review = Get-Content -LiteralPath $ReviewFile -Raw

if ($Review -match '(?im)^STATUS:\s*APPROVABLE\s*$') {
    Write-Host ""
    Write-Host "TASK REVIEW: APPROVABLE"
    exit 0
}

if ($Review -match '(?im)^STATUS:\s*REVISION_REQUIRED\s*$') {
    Write-Host ""
    Write-Host "TASK REVIEW: REVISION REQUIRED"
    exit 10
}

if ($Review -match '(?im)^STATUS:\s*HUMAN_REQUIRED\s*$') {
    Write-Host ""
    Write-Host "TASK REVIEW: HUMAN REQUIRED"
    exit 20
}

throw "Reviewer returned an unknown status."