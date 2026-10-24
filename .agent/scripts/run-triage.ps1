param(
    [string]$TaskId = "orchestrator-test"
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

$TriagePrompt = Join-Path $RepoRoot ".agent/prompts/triage.md"
$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"
$DevelopmentResult = Join-Path $ResultDir "development.md"
$QaResult = Join-Path $ResultDir "qa.md"
$TriageResult = Join-Path $ResultDir "triage.md"
$CodexOutput = Join-Path $ResultDir "triage-codex-output.txt"

# ==================================================
# RESOLVE IMPLEMENTATION WORKTREE
# ==================================================

$DevelopmentText = Get-Content -LiteralPath $DevelopmentResult -Raw

$DevelopmentBranch = Get-PipelineStageResultField -Text $DevelopmentText -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"
$ImplementationRoot = $null
$CurrentWorktree = $null

foreach ($Line in @(git -C $RepoRoot worktree list --porcelain)) {

    if ($Line -match '^worktree\s+(.+)$') {
        $CurrentWorktree = $Matches[1]
        continue
    }

    if ($Line -match '^branch\s+refs/heads/(.+)$') {
        if ($Matches[1] -eq $DevelopmentBranch) {
            $ImplementationRoot = $CurrentWorktree
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ImplementationRoot)) {
    throw "Could not locate implementation worktree for branch '$DevelopmentBranch'."
}
$ImplementationTask = Join-Path $ImplementationRoot ".agent/tasks/approved/$TaskId.md"
Copy-PipelineArtifact -Source $Task -Destination $ImplementationTask | Out-Null
Copy-PipelineArtifact -Source $DevelopmentResult -Destination (Join-Path (Join-Path $ImplementationRoot ".agent/results/$TaskId") (Split-Path $DevelopmentResult -Leaf)) | Out-Null
Copy-PipelineArtifact -Source $QaResult -Destination (Join-Path (Join-Path $ImplementationRoot ".agent/results/$TaskId") (Split-Path $QaResult -Leaf)) | Out-Null
$WorktreeTriageResult = Join-Path $ImplementationRoot ".agent/results/$TaskId/triage.md"
if (Test-Path -LiteralPath $WorktreeTriageResult -PathType Leaf) { Remove-Item -LiteralPath $WorktreeTriageResult -Force }
if (Test-Path -LiteralPath $TriageResult -PathType Leaf) { Remove-Item -LiteralPath $TriageResult -Force }

$BaselineEvidenceDir = Join-Path $ResultDir "baseline"
$BaselineRestoreLog = Join-Path $BaselineEvidenceDir "restore.log"
$BaselineTestLog = Join-Path $BaselineEvidenceDir "tests.log"
$BaselineTrx = Join-Path $BaselineEvidenceDir "baseline.trx"

$RepoParent = Split-Path $RepoRoot -Parent
$BaselineRoot = Join-Path $RepoParent "$TaskId-baseline"

Write-Host ""
Write-Host "DESIGNMODE AUTO TRIAGE"
Write-Host "======================"
Write-Host "Task: $TaskId"
Write-Host ""

# ==================================================
# PRECONDITIONS
# ==================================================

if (!(Test-Path $TriagePrompt)) {
    throw "Triage prompt not found: $TriagePrompt"
}

if (!(Test-Path $Task)) {
    throw "Approved task not found: $Task"
}

if (!(Test-Path $DevelopmentResult)) {
    throw "Developer result not found: $DevelopmentResult"
}

if (!(Test-Path $QaResult)) {
    throw "QA result not found: $QaResult"
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
New-Item -ItemType Directory -Force -Path $BaselineEvidenceDir | Out-Null
$BaselineRunStartedAtUtc = [DateTime]::UtcNow
foreach ($staleFile in @($BaselineRestoreLog, $BaselineTestLog, $BaselineTrx)) {
    if (Test-Path -LiteralPath $staleFile) { Remove-Item -LiteralPath $staleFile -Force }
}

# ==================================================
# DETERMINE FEATURE + EXACT BASE
# ==================================================

Push-Location $ImplementationRoot

try {

    $FeatureBranch = (git branch --show-current).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($FeatureBranch)) {
        throw "Unable to determine current feature branch."
    }

    $FeatureHead = (git rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine feature HEAD."
    }

    $BaseCommit = (git merge-base master-caio HEAD).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($BaseCommit)) {
        throw "Unable to determine exact master-caio merge base."
    }

}
finally {
    Pop-Location
}

Write-Host "Feature branch: $FeatureBranch"
Write-Host "Feature HEAD:   $FeatureHead"
Write-Host "Base commit:    $BaseCommit"
Write-Host ""

# ==================================================
# CREATE / VERIFY BASELINE WORKTREE
# ==================================================

$NeedCreateBaseline = $true

if (Test-Path $BaselineRoot) {

    Write-Host "Existing baseline directory found."
    Write-Host "Checking whether it is the correct Git worktree..."

    Push-Location $BaselineRoot

    try {

        $ExistingBaselineHead = (git rev-parse HEAD 2>$null).Trim()

        if ($LASTEXITCODE -eq 0 -and
            $ExistingBaselineHead -eq $BaseCommit) {

            Write-Host "Existing baseline is already at exact base."
            $NeedCreateBaseline = $false
        }

    }
    finally {
        Pop-Location
    }

    if ($NeedCreateBaseline) {

        Push-Location $RepoRoot

        try {

            $KnownWorktrees = git worktree list --porcelain
            $NormalizedBaseline = $BaselineRoot.Replace('\', '/')

            $KnownBaseline = $KnownWorktrees |
                Select-String -SimpleMatch $NormalizedBaseline

            if ($KnownBaseline) {

                Write-Host "Removing stale registered baseline worktree..."

                git worktree remove --force $BaselineRoot

                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to remove stale baseline worktree."
                }

            }
            else {

                throw @"
Baseline directory exists but is not the expected registered Git worktree:

$BaselineRoot

For safety it was not deleted automatically.
"@
            }

        }
        finally {
            Pop-Location
        }
    }
}

if ($NeedCreateBaseline) {

    Write-Host ""
    Write-Host "Creating exact-base baseline worktree..."

    Push-Location $RepoRoot

    try {

        git worktree add --detach $BaselineRoot $BaseCommit

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to create exact-base baseline worktree."
        }

    }
    finally {
        Pop-Location
    }
}

# ==================================================
# VERIFY BASELINE SHA
# ==================================================

Push-Location $BaselineRoot

try {

    $BaselineHead = (git rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to verify baseline HEAD."
    }

    if ($BaselineHead -ne $BaseCommit) {
        throw @"
Baseline SHA mismatch.

Expected:
$BaseCommit

Actual:
$BaselineHead
"@
    }

}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Baseline ready:"
Write-Host $BaselineRoot
Write-Host "Baseline HEAD: $BaselineHead"
Write-Host ""

# ==================================================
# POWERHELL BASELINE RESTORE
# ==================================================

Write-Host "Restoring baseline test project..."

Push-Location $BaselineRoot

try {

    dotnet restore `
        "DesignMode.AI.Test/DesignMode.AI.Test.csproj" `
        *> $BaselineRestoreLog

    $BaselineRestoreExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

if ($BaselineRestoreExit -ne 0) {

    Write-Host ""
    Write-Host "BASELINE RESTORE FAILED"
    Write-Host "See:"
    Write-Host $BaselineRestoreLog
    Write-Host ""

    # This is infrastructure failure, not an application repair.
    exit 20
}

Write-Host "Baseline restore: PASS"
Write-Host ""

# ==================================================
# POWERSHELL BASELINE TEST
# ==================================================

Write-Host "Running baseline test suite..."
Write-Host ""

Push-Location $BaselineRoot

try {

    dotnet test `
        "DesignMode.AI.Test/DesignMode.AI.Test.csproj" `
        --no-restore `
        --logger "trx;LogFileName=$BaselineTrx" `
        --logger "console;verbosity=minimal" `
        *> $BaselineTestLog

    # IMPORTANT:
    # A non-zero exit code is expected when baseline tests fail.
    # Do NOT abort merely because tests failed.
    $BaselineTestExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

Write-Host "Baseline test exit code: $BaselineTestExit"
Write-Host "Baseline evidence:"
Write-Host $BaselineTestLog
Write-Host ""

# Triage executes in the implementation worktree. Copy the current exact-base
# logs and TRX there, verify freshness and byte identity, and bind them to the
# feature/base SHAs in a manifest before asking the agent to classify failures.
$ImplementationBaselineEvidence = Join-Path $ImplementationRoot ".agent/results/$TaskId/baseline"
$BaselineEvidenceManifest = Copy-PipelineBaselineEvidence `
    -SourceDirectory $BaselineEvidenceDir `
    -DestinationDirectory $ImplementationBaselineEvidence `
    -RunStartedAtUtc $BaselineRunStartedAtUtc `
    -TaskId $TaskId `
    -FeatureBranch $FeatureBranch `
    -FeatureHead $FeatureHead `
    -BaseCommit $BaseCommit `
    -TestExitCode $BaselineTestExit

# ==================================================
# EXTRACT USEFUL BASELINE SUMMARY
# ==================================================

$BaselineSummary = ""

if (Test-Path $BaselineTestLog) {

    $SummaryLines = Get-Content $BaselineTestLog |
        Select-String -Pattern `
            "Failed!|Passed!|Test summary:|Failed .*Sequence_|Failed .*LargeGenModal_|Failed .*WorkspaceStyles_|Failed .*LandingScript_"

    if ($SummaryLines) {

        $BaselineSummary = (
            $SummaryLines |
            ForEach-Object { $_.Line }
        ) -join [Environment]::NewLine

    }
}

if ([string]::IsNullOrWhiteSpace($BaselineSummary)) {
    $BaselineSummary = "No concise summary could be extracted. Read tests.log."
}

Write-Host "Baseline summary:"
Write-Host ""
Write-Host $BaselineSummary
Write-Host ""

# ==================================================
# TRIAGE PROMPT
# ==================================================

$Prompt = @"
Act as the QA Failure Triage Agent defined in:

.agent/prompts/triage.md


AUTHORITATIVE TASK:

.agent/tasks/approved/$TaskId.md


DEVELOPER RESULT:

.agent/results/$TaskId/development.md


INDEPENDENT QA RESULT:

.agent/results/$TaskId/qa.md


==================================================
DETERMINISTIC BASELINE ENVIRONMENT
==================================================

The PowerShell orchestrator has already prepared and executed the exact
baseline for you.

DO NOT create a worktree.

DO NOT restore the baseline.

DO NOT rerun baseline tests unless absolutely necessary.

Use the evidence already produced by the orchestrator. The fresh baseline logs and
TRX were copied byte-for-byte into this implementation worktree and verified
against evidence-manifest.json before this invocation. Read that manifest and all
three copied artifacts. If any file is missing, stale, invalid, or does not match
its recorded SHA-256, classify the result as BLOCKED.


FEATURE CHECKOUT:

$ImplementationRoot


FEATURE BRANCH:

$FeatureBranch


FEATURE HEAD:

$FeatureHead


EXACT BASE COMMIT:

$BaseCommit


BASELINE CHECKOUT:

$BaselineRoot


BASELINE HEAD VERIFIED:

$BaselineHead


BASELINE RESTORE:

PASS


BASELINE TEST EXIT CODE:

$BaselineTestExit

BASELINE EVIDENCE MANIFEST:

$BaselineEvidenceManifest


BASELINE TEST LOG:

.agent/results/$TaskId/baseline/tests.log


BASELINE TRX:

.agent/results/$TaskId/baseline/baseline.trx


BASELINE RESTORE LOG:

.agent/results/$TaskId/baseline/restore.log

BASELINE EVIDENCE MANIFEST:

.agent/results/$TaskId/baseline/evidence-manifest.json


BASELINE SUMMARY:

$BaselineSummary


==================================================
TRIAGE RESPONSIBILITY
==================================================

Compare the independent feature QA evidence against the exact-base baseline
evidence.

Analyze every relevant failure.

Classify each as exactly one of:

BRANCH_REGRESSION
PRE_EXISTING
TEST_INFRASTRUCTURE
FLAKY_OR_INCONCLUSIVE


IMPORTANT:

A failing baseline test is NOT automatically a problem with the feature.

If the same test fails at the exact base and feature with materially the same
assertion/failure, classify it PRE_EXISTING.

If a failure exists only because of filesystem permissions, Node execution,
NuGet access, browser/runtime environment, or similar execution-environment
conditions, classify it TEST_INFRASTRUCTURE when supported by evidence.

If feature acceptance passes and there are zero branch regressions, application
repair should not be requested merely to make unrelated baseline tests green.

Do not infer causality only from file overlap or non-overlap when experimental
evidence is available.


==================================================
EXPECTED BASELINE FACT
==================================================

The baseline test command was allowed to return non-zero because failing tests
are evidence.

Do NOT interpret the non-zero baseline test exit code itself as a compilation
failure.

Use the test summary and individual failures in the evidence.


==================================================
DECISION
==================================================

If one or more failures are established as caused by this feature:

REPAIR_REQUIRED: YES
NEXT_ACTION: REPAIR


If:

- feature acceptance passes
- BRANCH_REGRESSIONS is 0
- relevant failures are PRE_EXISTING and/or TEST_INFRASTRUCTURE

then:

REPAIR_REQUIRED: NO
FEATURE_QA_STATUS: PASS_WITH_BASELINE_FAILURES
NEXT_ACTION: REGRESSION


If causality still genuinely cannot be established:

REPAIR_REQUIRED: NO
FEATURE_QA_STATUS: BLOCKED
NEXT_ACTION: HUMAN_REQUIRED


==================================================
OUTPUT
==================================================

Write the result to:

.agent/results/$TaskId/triage.md


Include:

STATUS:

FEATURE_BRANCH:

FEATURE_HEAD:

BASE_COMMIT:

BRANCH_REGRESSIONS:

PRE_EXISTING:

TEST_INFRASTRUCTURE:

FLAKY_OR_INCONCLUSIVE:

REPAIR_REQUIRED:

FEATURE_QA_STATUS:

NEXT_ACTION:

BASELINE_VERIFICATION:

NOTES:


NEXT_ACTION must be exactly one of:

REPAIR
REGRESSION
HUMAN_REQUIRED


Do NOT modify application code.

Do NOT repair anything.

Do NOT weaken tests.

Do NOT weaken acceptance criteria.

Do NOT push.

Do NOT merge.

Do NOT deploy.

You are being executed non-interactively by the DesignMode autonomous
engineering orchestrator.

Do not wait for human instructions.
"@

# ==================================================
# RUN TRIAGE AGENT
# ==================================================

Write-Host "Starting Triage Agent..."
Write-Host ""

$Prompt | codex exec `
    -C $ImplementationRoot `
    --add-dir $BaselineRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $CodexOutput `
    -

$ExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Codex exit code: $ExitCode"

# ==================================================
# VALIDATE TRIAGE
# ==================================================

if ($ExitCode -ne 0) {

    Write-Host ""
    Write-Host "TRIAGE AGENT EXECUTION FAILED"
    exit $ExitCode
}

if (Test-Path -LiteralPath $WorktreeTriageResult -PathType Leaf) {
    Copy-PipelineArtifact -Source $WorktreeTriageResult -Destination $TriageResult | Out-Null
    Write-Host "Copied current Triage result from implementation worktree:"
    Write-Host $WorktreeTriageResult
}
elseif (!(Test-Path -LiteralPath $TriageResult -PathType Leaf)) {
    Write-Host ""
    Write-Host "ERROR: Codex completed but Triage result was not found in either location:"
    Write-Host $TriageResult
    Write-Host $WorktreeTriageResult
    exit 2
}

# ==================================================
# COMPLETE
# ==================================================

Write-Host ""
Write-Host "Triage result created:"
Write-Host $TriageResult
Write-Host ""

Write-Host "Baseline evidence retained:"
Write-Host $BaselineEvidenceDir
Write-Host ""

Write-Host "TRIAGE STAGE COMPLETE"
