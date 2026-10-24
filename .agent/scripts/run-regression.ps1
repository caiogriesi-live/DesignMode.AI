param(
    [string]$TaskId = "orchestrator-test"
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

$RegressionPrompt = Join-Path $RepoRoot ".agent/prompts/regression.md"
$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"

$DevelopmentResult = Join-Path $ResultDir "development.md"
$QaResult = Join-Path $ResultDir "qa.md"
$TriageResult = Join-Path $ResultDir "triage.md"

$RegressionResult = Join-Path $ResultDir "regression.md"
$CodexOutput = Join-Path $ResultDir "regression-codex-output.txt"

$RegressionEvidenceDir = Join-Path $ResultDir "regression-evidence"

$FeatureClientLog = Join-Path $RegressionEvidenceDir "feature-client-build.log"
$FeatureServerLog = Join-Path $RegressionEvidenceDir "feature-server-build.log"

$BaselineClientLog = Join-Path $RegressionEvidenceDir "baseline-client-build.log"
$BaselineServerLog = Join-Path $RegressionEvidenceDir "baseline-server-build.log"

$RepoParent = Split-Path $RepoRoot -Parent
$BaselineRoot = Join-Path $RepoParent "$TaskId-baseline"

Write-Host ""
Write-Host "DESIGNMODE AUTO REGRESSION"
Write-Host "=========================="
Write-Host "Task: $TaskId"
Write-Host ""

# ==================================================
# PRECONDITIONS
# ==================================================

if (!(Test-Path -LiteralPath $RegressionPrompt -PathType Leaf)) {
    throw "Regression prompt not found: $RegressionPrompt"
}

if (!(Test-Path -LiteralPath $Task -PathType Leaf)) {
    throw "Approved task not found: $Task"
}

if (!(Test-Path -LiteralPath $DevelopmentResult -PathType Leaf)) {
    throw "Developer result not found: $DevelopmentResult"
}

if (!(Test-Path -LiteralPath $QaResult -PathType Leaf)) {
    throw "QA result not found: $QaResult"
}

if (!(Test-Path -LiteralPath $TriageResult -PathType Leaf)) {
    throw "Triage result not found: $TriageResult"
}

if (!(Test-Path -LiteralPath $BaselineRoot -PathType Container)) {
    throw "Baseline worktree not found: $BaselineRoot"
}

New-Item `
    -ItemType Directory `
    -Force `
    -Path $RegressionEvidenceDir |
    Out-Null

# ==================================================
# RESOLVE IMPLEMENTATION WORKTREE
# ==================================================

$DevelopmentText = Get-Content -LiteralPath $DevelopmentResult -Raw

$DevelopmentBranch = Get-PipelineStageResultField -Text $DevelopmentText -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"

$ImplementationRoot = $null
$CurrentWorktree = $null

$WorktreeLines = @(
    git -C $RepoRoot worktree list --porcelain
)

if ($LASTEXITCODE -ne 0) {
    throw "Unable to enumerate Git worktrees."
}

foreach ($Line in $WorktreeLines) {

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
Copy-PipelineArtifact -Source $TriageResult -Destination (Join-Path (Join-Path $ImplementationRoot ".agent/results/$TaskId") (Split-Path $TriageResult -Leaf)) | Out-Null

if (!(Test-Path -LiteralPath $ImplementationRoot -PathType Container)) {
    throw "Implementation worktree does not exist: $ImplementationRoot"
}

Write-Host "Implementation branch: $DevelopmentBranch"
Write-Host "Implementation root:   $ImplementationRoot"
Write-Host ""

# ==================================================
# VERIFY TRIAGE ROUTING
# ==================================================

$TriageText = Get-Content -LiteralPath $TriageResult -Raw

if ($TriageText -notmatch '(?im)^NEXT_ACTION:\s*REGRESSION\s*$') {

    Write-Host "Regression is not authorized by Triage."
    Write-Host ""
    Write-Host "Expected:"
    Write-Host "NEXT_ACTION: REGRESSION"
    Write-Host ""
    Write-Host "Stopping safely."

    exit 10
}

# ==================================================
# GIT STATE
# ==================================================

Push-Location $ImplementationRoot

try {

    $FeatureBranch = (git branch --show-current).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($FeatureBranch)) {
        throw "Unable to determine feature branch."
    }

    if ($FeatureBranch -ne $DevelopmentBranch) {
        throw "Implementation worktree branch '$FeatureBranch' does not match Developer branch '$DevelopmentBranch'."
    }

    $FeatureHead = (git rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($FeatureHead)) {
        throw "Unable to determine feature HEAD."
    }

    $BaseCommit = (git merge-base master-caio HEAD).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($BaseCommit)) {
        throw "Unable to determine exact base."
    }

}
finally {
    Pop-Location
}

# ==================================================
# VERIFY BASELINE
# ==================================================

Push-Location $BaselineRoot

try {

    $BaselineHead = (git rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($BaselineHead)) {
        throw "Unable to determine baseline HEAD."
    }

}
finally {
    Pop-Location
}

if ($BaselineHead -ne $BaseCommit) {

    throw @"
Baseline worktree is not at the exact feature base.

Expected:
$BaseCommit

Actual:
$BaselineHead
"@
}

Write-Host "Feature branch: $FeatureBranch"
Write-Host "Feature HEAD:   $FeatureHead"
Write-Host "Base commit:    $BaseCommit"
Write-Host "Baseline HEAD:  $BaselineHead"
Write-Host ""

# ==================================================
# FEATURE CLIENT BUILD
# ==================================================

Write-Host "Building FEATURE Client..."

Push-Location $ImplementationRoot

try {

    dotnet build `
        "DesignMode.AI.Client/DesignMode.AI.Client.csproj" `
        --no-restore `
        *> $FeatureClientLog

    $FeatureClientExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

Write-Host "Feature Client exit code: $FeatureClientExit"
Write-Host ""

# ==================================================
# FEATURE SERVER BUILD
# ==================================================

Write-Host "Building FEATURE Server..."

Push-Location $ImplementationRoot

try {

    dotnet build `
        "DesignMode.AI.Server/DesignMode.AI.Server.csproj" `
        --no-restore `
        *> $FeatureServerLog

    $FeatureServerExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

Write-Host "Feature Server exit code: $FeatureServerExit"
Write-Host ""

# ==================================================
# BASELINE CLIENT BUILD
# ==================================================

Write-Host "Building BASELINE Client..."

Push-Location $BaselineRoot

try {

    dotnet build `
        "DesignMode.AI.Client/DesignMode.AI.Client.csproj" `
        --no-restore `
        *> $BaselineClientLog

    $BaselineClientExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

Write-Host "Baseline Client exit code: $BaselineClientExit"
Write-Host ""

# ==================================================
# BASELINE SERVER BUILD
# ==================================================

Write-Host "Building BASELINE Server..."

Push-Location $BaselineRoot

try {

    dotnet build `
        "DesignMode.AI.Server/DesignMode.AI.Server.csproj" `
        --no-restore `
        *> $BaselineServerLog

    $BaselineServerExit = $LASTEXITCODE

}
finally {
    Pop-Location
}

Write-Host "Baseline Server exit code: $BaselineServerExit"
Write-Host ""

# ==================================================
# EXTRACT ERROR SIGNATURES
# ==================================================

function Get-BuildFailureSignature {

    param(
        [string]$LogPath
    )

    if (!(Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return "LOG_NOT_FOUND"
    }

    $Matches = Get-Content -LiteralPath $LogPath |
        Select-String -Pattern `
            "error [A-Z]+[0-9]+|MSB[0-9]+|ApplyCompressionNegotiation|InvalidOperationException|Endpoints not found|AWSSDK.Core.wasm|NuGet.Config|Access to the path|Build FAILED"

    if (!$Matches) {
        return "NO_MATCHING_FAILURE_SIGNATURE"
    }

    return (
        $Matches |
        ForEach-Object { $_.Line.Trim() } |
        Select-Object -Unique
    ) -join [Environment]::NewLine
}

$FeatureClientSignature =
    Get-BuildFailureSignature $FeatureClientLog

$FeatureServerSignature =
    Get-BuildFailureSignature $FeatureServerLog

$BaselineClientSignature =
    Get-BuildFailureSignature $BaselineClientLog

$BaselineServerSignature =
    Get-BuildFailureSignature $BaselineServerLog

# ==================================================
# DISPLAY BUILD MATRIX
# ==================================================

Write-Host ""
Write-Host "BUILD MATRIX"
Write-Host "============"
Write-Host ""

Write-Host "FEATURE CLIENT:  $FeatureClientExit"
Write-Host "BASELINE CLIENT: $BaselineClientExit"
Write-Host ""

Write-Host "FEATURE SERVER:  $FeatureServerExit"
Write-Host "BASELINE SERVER: $BaselineServerExit"
Write-Host ""

# ==================================================
# REGRESSION PROMPT
# ==================================================

$Prompt = @"
Act as the Regression / Release Candidate Agent defined in:

.agent/prompts/regression.md


AUTHORITATIVE TASK:

.agent/tasks/approved/$TaskId.md


DEVELOPER RESULT:

$DevelopmentResult


INDEPENDENT QA RESULT:

$QaResult


TRIAGE RESULT:

$TriageResult


==================================================
CURRENT GIT STATE
==================================================

FEATURE CHECKOUT:

$ImplementationRoot


FEATURE BRANCH:

$FeatureBranch


FEATURE HEAD:

$FeatureHead


EXACT BASE:

$BaseCommit


BASELINE CHECKOUT:

$BaselineRoot


BASELINE HEAD:

$BaselineHead


The PowerShell orchestrator has verified that the baseline checkout is exactly
at the feature merge base.


==================================================
AUTHORITATIVE BUILD EVIDENCE
==================================================

The deterministic PowerShell orchestrator executed the SAME standard build
commands against the feature checkout and exact baseline checkout.

Do not rely only on previous Developer or QA build claims.

Use this evidence for build-regression classification.


FEATURE CLIENT BUILD EXIT CODE:

$FeatureClientExit


BASELINE CLIENT BUILD EXIT CODE:

$BaselineClientExit


FEATURE SERVER BUILD EXIT CODE:

$FeatureServerExit


BASELINE SERVER BUILD EXIT CODE:

$BaselineServerExit


FEATURE CLIENT LOG:

$FeatureClientLog


BASELINE CLIENT LOG:

$BaselineClientLog


FEATURE SERVER LOG:

$FeatureServerLog


BASELINE SERVER LOG:

$BaselineServerLog


==================================================
EXTRACTED FAILURE SIGNATURES
==================================================

FEATURE CLIENT:

$FeatureClientSignature


BASELINE CLIENT:

$BaselineClientSignature


FEATURE SERVER:

$FeatureServerSignature


BASELINE SERVER:

$BaselineServerSignature


==================================================
BUILD CLASSIFICATION RULE
==================================================

Use the Build Baseline Comparison rules in regression.md.

In particular:

Feature fails + Base passes
=> BRANCH_BUILD_REGRESSION

Feature fails + Base fails with materially the same underlying failure
=> BASELINE_BUILD_FAILURE

Feature fails + Base fails differently
=> INCONCLUSIVE

Feature passes
=> PASS


Do not compare logs byte-for-byte.

Compare the meaningful underlying failure signatures.

Differences in paths, warning counts, timestamps, duration, and output ordering
are not regressions.


==================================================
REGRESSION RESPONSIBILITY
==================================================

Perform the final automated pre-merge assessment.

Review:

- authoritative task
- Developer result
- independent QA result
- Triage result
- feature acceptance
- desktop Playwright evidence
- mobile WebKit evidence
- baseline test evidence
- feature and baseline build evidence
- current branch diff
- unexpected scope changes
- protected systems

Do not modify application code.

Do not repair failures.

Do not weaken tests.

Do not weaken acceptance criteria.


==================================================
CONTROL-PLANE / WORKTREE RULE
==================================================

The feature implementation lives in:

$ImplementationRoot

The authoritative orchestration results live in:

$ResultDir

You are running from the feature implementation worktree so that repository
inspection and branch-diff analysis apply to the actual feature.

The Developer, QA, and Triage result files listed above are absolute paths in
the control plane and may be read directly.

The build logs listed above are also absolute control-plane paths.

Write your regression result to the normal relative task result path in the
feature worktree:

.agent/results/$TaskId/regression.md

The PowerShell orchestrator will recover that structured result into the
control plane after you finish.


==================================================
OUTPUT
==================================================

Write:

.agent/results/$TaskId/regression.md


Follow the required output structure from:

.agent/prompts/regression.md


Your output must include:

STATUS:
FEATURE:
BRANCH:
HEAD:
BASE:
ACCEPTANCE:
DESKTOP_PLAYWRIGHT:
MOBILE_WEBKIT:
BRANCH_REGRESSIONS:
BUILD_REGRESSIONS:
BASELINE_FAILURES:
BASELINE_BUILD_FAILURES:
TEST_INFRASTRUCTURE:
REGRESSION:
CLIENT_BUILD:
SERVER_BUILD:
UNEXPECTED_SCOPE_CHANGES:
REPAIR_REQUIRED:
HUMAN_ACTION:
NOTES:


STATUS must be exactly one of:

READY_FOR_HUMAN_REVIEW
REPAIR_REQUIRED
HUMAN_REQUIRED


Do NOT push.

Do NOT merge.

Do NOT deploy.

Do NOT wait for human instructions.
"@

# ==================================================
# RUN REGRESSION AGENT
# ==================================================

Write-Host "Starting Regression Agent..."
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
# VALIDATE
# ==================================================

if ($ExitCode -ne 0) {

    Write-Host ""
    Write-Host "REGRESSION AGENT EXECUTION FAILED"

    exit $ExitCode
}

if (!(Test-Path -LiteralPath $RegressionResult -PathType Leaf)) {

    $WorktreeRegressionResult = Join-Path `
        $ImplementationRoot `
        ".agent/results/$TaskId/regression.md"

    if (Test-Path -LiteralPath $WorktreeRegressionResult -PathType Leaf) {

        Copy-PipelineArtifact -Source $WorktreeRegressionResult -Destination $RegressionResult | Out-Null

        Write-Host ""
        Write-Host "Recovered Regression result from implementation worktree:"
        Write-Host $WorktreeRegressionResult
    }
    else {

        Write-Host ""
        Write-Host "ERROR: Regression result was not found in either location:"
        Write-Host $RegressionResult
        Write-Host $WorktreeRegressionResult

        exit 2
    }
}

# ==================================================
# FINAL STATUS
# ==================================================

$RegressionText = Get-Content -LiteralPath $RegressionResult -Raw

Write-Host ""

if ($RegressionText -match
    '(?im)^STATUS:\s*READY_FOR_HUMAN_REVIEW\s*$') {

    Write-Host "================================"
    Write-Host "READY FOR HUMAN REVIEW"
    Write-Host "================================"
    Write-Host ""
    Write-Host "Nothing pushed."
    Write-Host "Nothing merged."
    Write-Host "Nothing deployed."

}
elseif ($RegressionText -match
    '(?im)^STATUS:\s*REPAIR_REQUIRED\s*$') {

    Write-Host "================================"
    Write-Host "REPAIR REQUIRED"
    Write-Host "================================"
    Write-Host ""
    Write-Host "Future orchestrator will route to Repair Agent."

}
elseif ($RegressionText -match
    '(?im)^STATUS:\s*HUMAN_REQUIRED\s*$') {

    Write-Host "================================"
    Write-Host "HUMAN INVESTIGATION REQUIRED"
    Write-Host "================================"

}
else {

    Write-Host "ERROR: Unknown regression STATUS."

    exit 3
}

Write-Host ""
Write-Host "Regression result:"
Write-Host $RegressionResult
Write-Host ""

Write-Host "Regression evidence:"
Write-Host $RegressionEvidenceDir
Write-Host ""

Write-Host "REGRESSION STAGE COMPLETE"
