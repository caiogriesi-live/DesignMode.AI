param(
    [string]$TaskId = "orchestrator-test",
    [int]$Attempt = 1
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')
$RepairPrompt = Join-Path $RepoRoot ".agent/prompts/repair.md"
$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"
$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"
$DevelopmentResult = Join-Path $ResultDir "development.md"
$QaResult = Join-Path $ResultDir "qa.md"
$TriageResult = Join-Path $ResultDir "triage.md"
$RepairResult = Join-Path $ResultDir "repair-$Attempt.md"
$CodexOutput = Join-Path $ResultDir "repair-$Attempt-codex-output.txt"

Write-Host ""
Write-Host "DESIGNMODE AUTO REPAIR"
Write-Host "======================"
Write-Host "Task: $TaskId"
Write-Host "Attempt: $Attempt"
Write-Host ""

# ==================================================
# SAFETY
# ==================================================

if ($Attempt -lt 1) {
    throw "Repair attempt must be >= 1."
}

if ($Attempt -gt 3) {
    Write-Host "Maximum autonomous repair attempts reached."
    Write-Host "HUMAN_REQUIRED"
    exit 30
}

if ([string]::IsNullOrWhiteSpace($TaskId) -or $TaskId -match '[\\/]') {
    throw "TaskId must be a non-empty task identifier without path separators."
}

# ==================================================
# PRECONDITIONS
# ==================================================

$RequiredFiles = @(
    @{ Label = "Repair prompt"; Path = $RepairPrompt },
    @{ Label = "Approved task"; Path = $Task },
    @{ Label = "Developer result"; Path = $DevelopmentResult },
    @{ Label = "QA result"; Path = $QaResult },
    @{ Label = "Triage result"; Path = $TriageResult }
)

foreach ($RequiredFile in $RequiredFiles) {
    if (-not (Test-Path -LiteralPath $RequiredFile.Path -PathType Leaf)) {
        throw "$($RequiredFile.Label) not found: $($RequiredFile.Path)"
    }
}

# ==================================================
# VERIFY TRIAGE AUTHORIZED REPAIR
# ==================================================

$TriageText = Get-Content -LiteralPath $TriageResult -Raw
$RepairAuthorized = [regex]::IsMatch(
    $TriageText,
    '(?im)^\s*REPAIR_REQUIRED\s*:\s*YES\s*$'
)
$RepairIsNextAction = [regex]::IsMatch(
    $TriageText,
    '(?im)^\s*NEXT_ACTION\s*:\s*REPAIR\s*$'
)

if (-not ($RepairAuthorized -and $RepairIsNextAction)) {
    Write-Host "Triage has NOT authorized repair."
    Write-Host "Expected both of these lines (case-insensitive):"
    Write-Host "REPAIR_REQUIRED: YES"
    Write-Host "NEXT_ACTION: REPAIR"
    Write-Host ""
    Write-Host "Stopping safely."
    exit 10
}

# ==================================================
# GIT STATE
# ==================================================

$FeatureBranchFromResult = Get-PipelineStageResultField -Text (Get-Content -LiteralPath $DevelopmentResult -Raw) -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"
$ImplementationRoot = Get-PipelineImplementationRoot -RepoRoot $RepoRoot -Branch $FeatureBranchFromResult
if (!$ImplementationRoot) { throw "Could not locate implementation worktree for '$FeatureBranchFromResult'." }
$ImplementationTask = Join-Path $ImplementationRoot ".agent/tasks/approved/$TaskId.md"
Copy-PipelineArtifact -Source $Task -Destination $ImplementationTask | Out-Null
foreach ($artifact in @($DevelopmentResult, $QaResult, $TriageResult)) {
    Copy-PipelineArtifact -Source $artifact -Destination (Join-Path $ImplementationRoot ".agent/results/$TaskId/$([IO.Path]::GetFileName($artifact))") | Out-Null
}

Push-Location $ImplementationRoot
try {
    $FeatureBranch = (git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($FeatureBranch)) {
        throw "Unable to determine current branch."
    }

    if ($FeatureBranch -eq "master-caio") {
        throw "Refusing to run Repair Agent directly on master-caio."
    }

    $BeforeRepairHead = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($BeforeRepairHead)) {
        throw "Unable to determine current HEAD."
    }

    $BaseCommit = (git merge-base master-caio HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($BaseCommit)) {
        throw "Unable to determine merge-base of master-caio and HEAD."
    }
}
finally {
    Pop-Location
}

Write-Host "Feature branch: $FeatureBranch"
Write-Host "Current HEAD:   $BeforeRepairHead"
Write-Host "Merge-base:     $BaseCommit"
Write-Host ""

# ==================================================
# REPAIR PROMPT
# ==================================================

$Prompt = @"
Act as the Repair Agent defined in:

.agent/prompts/repair.md

Treat the following files as the authoritative evidence for this task. Read
them directly before acting:

APPROVED TASK:
.agent/tasks/approved/$TaskId.md

DEVELOPMENT RESULT:
.agent/results/$TaskId/development.md

INDEPENDENT QA RESULT:
.agent/results/$TaskId/qa.md

TRIAGE RESULT:
.agent/results/$TaskId/triage.md

Repair attempt: $Attempt of 3
Current feature branch: $FeatureBranch
HEAD before repair: $BeforeRepairHead
Merge-base with master-caio: $BaseCommit

Diagnose the root cause from the authoritative task and evidence. Choose the
smallest safe repair supported by that evidence. Do not assume a particular
defect or prescribe a specific code change.

Preserve all passing criteria and required functionality. Do not weaken tests,
acceptance criteria, or verification to make a failure disappear. Do not modify
unrelated or protected systems. Do not reset, clean, or discard unrelated
working-tree changes. Do not modify master-caio, push, merge, or deploy.

Before editing, inspect the working tree and avoid staging or committing
unrelated pre-existing changes. Make and verify only the repair warranted by
the evidence.

Verification must include relevant automated tests, the Client build, the
Server build, and git diff --check. Use the repository's standard commands
and report exact outcomes. Run browser verification only when repair.md says
it is appropriate or required; do not claim browser verification unless it
was actually completed. Confirm that task acceptance criteria remain intact.

If and only if a valid repair is implemented and verified, commit that repair
locally on the current feature branch. Do not include unrelated changes in the
commit. If unrelated changes prevent safe isolation, do not commit them; report
HUMAN_REQUIRED when the situation is unsafe or ambiguous.

Write the structured result to:
.agent/results/$TaskId/repair-$Attempt.md

Include every field below, each as a separate field label:

STATUS:
ATTEMPT:
BRANCH:
BEFORE_REPAIR_HEAD:
REPAIR_COMMIT:
ROOT_CAUSE:
FILES_CHANGED:
CLIENT_BUILD:
SERVER_BUILD:
TESTS:
ACCEPTANCE_PRESERVED:
DIFF_CHECK:
REPAIR_COMPLETE:
NEXT_ACTION:
NOTES:

STATUS must be exactly REPAIRED, REPAIR_FAILED, or HUMAN_REQUIRED.

For a successfully implemented, committed, and independently verifiable
repair, use STATUS: REPAIRED, REPAIR_COMPLETE: YES, and NEXT_ACTION: QA.

For an unsuccessful but potentially repairable attempt, use STATUS:
REPAIR_FAILED, REPAIR_COMPLETE: NO, and NEXT_ACTION: RETRY_REPAIR.

For an unsafe, ambiguous, or externally blocked situation, use STATUS:
HUMAN_REQUIRED, REPAIR_COMPLETE: NO, and NEXT_ACTION: HUMAN_REQUIRED.

Be precise about what was and was not verified. Do not wait for human
instructions; finish by writing the result file.
"@

# ==================================================
# RUN REPAIR AGENT
# ==================================================

Write-Host "Starting Repair Agent..."
Write-Host ""

Push-Location $ImplementationRoot
try {
    $Prompt | codex exec `
        -C $ImplementationRoot `
        --model "gpt-6-luna" `
        --approve-for-me `
        --output-last-message $CodexOutput `
        -
    $ExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Codex exit code: $ExitCode"

# ==================================================
# VALIDATE EXECUTION AND STRUCTURED OUTPUT
# ==================================================

if ($ExitCode -ne 0) {
    Write-Host ""
    Write-Host "REPAIR AGENT EXECUTION FAILED"
    exit $ExitCode
}

if (-not (Test-Path -LiteralPath $RepairResult -PathType Leaf)) {
    $WorktreeRepairResult = Join-Path $ImplementationRoot ".agent/results/$TaskId/repair-$Attempt.md"
    if (Test-Path -LiteralPath $WorktreeRepairResult -PathType Leaf) {
        Copy-PipelineArtifact -Source $WorktreeRepairResult -Destination $RepairResult | Out-Null
    }
}
if (-not (Test-Path -LiteralPath $RepairResult -PathType Leaf)) {
    Write-Host ""
    Write-Host "ERROR: Repair Agent completed but did not create:"
    Write-Host $RepairResult
    exit 2
}

$RepairText = Get-Content -LiteralPath $RepairResult -Raw
$RequiredResultFields = @(
    "STATUS", "ATTEMPT", "BRANCH", "BEFORE_REPAIR_HEAD", "REPAIR_COMMIT",
    "ROOT_CAUSE", "FILES_CHANGED", "CLIENT_BUILD", "SERVER_BUILD", "TESTS",
    "ACCEPTANCE_PRESERVED", "DIFF_CHECK", "REPAIR_COMPLETE", "NEXT_ACTION", "NOTES"
)

foreach ($Field in $RequiredResultFields) {
    if (-not [regex]::IsMatch($RepairText, "(?im)^\s*$([regex]::Escape($Field))\s*:")) {
        Write-Host "ERROR: Repair result is missing required field: $Field"
        exit 3
    }
}

$StatusMatches = [regex]::Matches($RepairText, '(?im)^\s*STATUS\s*:\s*(.*?)\s*$')
if ($StatusMatches.Count -ne 1) {
    Write-Host "ERROR: Repair result must contain exactly one STATUS field."
    exit 3
}
$Status = $StatusMatches[0].Groups[1].Value.Trim()

$CompleteMatches = [regex]::Matches($RepairText, '(?im)^\s*REPAIR_COMPLETE\s*:\s*(.*?)\s*$')
$NextMatches = [regex]::Matches($RepairText, '(?im)^\s*NEXT_ACTION\s*:\s*(.*?)\s*$')
if ($CompleteMatches.Count -ne 1 -or $NextMatches.Count -ne 1) {
    Write-Host "ERROR: Repair result must contain exactly one REPAIR_COMPLETE and NEXT_ACTION field."
    exit 3
}
$RepairComplete = $CompleteMatches[0].Groups[1].Value.Trim()
$NextAction = $NextMatches[0].Groups[1].Value.Trim()

switch ($Status) {
    "REPAIRED" {
        if ($RepairComplete -ne "YES" -or $NextAction -ne "QA") {
            Write-Host "ERROR: REPAIRED requires REPAIR_COMPLETE: YES and NEXT_ACTION: QA."
            exit 3
        }
        Write-Host ""
        Write-Host "================================"
        Write-Host "REPAIR COMPLETED"
        Write-Host "================================"
        Write-Host ""
        Write-Host "Next stage: QA"
    }
    "REPAIR_FAILED" {
        if ($RepairComplete -ne "NO" -or $NextAction -ne "RETRY_REPAIR") {
            Write-Host "ERROR: REPAIR_FAILED requires REPAIR_COMPLETE: NO and NEXT_ACTION: RETRY_REPAIR."
            exit 3
        }
        Write-Host ""
        Write-Host "================================"
        Write-Host "REPAIR ATTEMPT FAILED"
        Write-Host "================================"
        Write-Host ""
        if ($Attempt -ge 3) {
            Write-Host "Maximum repair attempts reached."
            Write-Host "Next stage: HUMAN_REQUIRED"
        }
        else {
            Write-Host "Next possible attempt: $($Attempt + 1)"
        }
    }
    "HUMAN_REQUIRED" {
        if ($RepairComplete -ne "NO" -or $NextAction -ne "HUMAN_REQUIRED") {
            Write-Host "ERROR: HUMAN_REQUIRED requires REPAIR_COMPLETE: NO and NEXT_ACTION: HUMAN_REQUIRED."
            exit 3
        }
        Write-Host ""
        Write-Host "================================"
        Write-Host "HUMAN INVESTIGATION REQUIRED"
        Write-Host "================================"
    }
    default {
        Write-Host "ERROR: Unknown Repair STATUS: $Status"
        exit 3
    }
}

Write-Host ""
Write-Host "Repair result:"
Write-Host $RepairResult
Write-Host ""
Write-Host "REPAIR STAGE COMPLETE"
