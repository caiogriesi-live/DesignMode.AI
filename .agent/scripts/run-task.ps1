param(
    [Parameter(Mandatory = $true)]
    [string]$TaskId,

    [int]$MaxRepairAttempts = 3
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

$DeveloperScript  = Join-Path $PSScriptRoot "run-developer.ps1"
$QaScript         = Join-Path $PSScriptRoot "run-qa.ps1"
$TriageScript     = Join-Path $PSScriptRoot "run-triage.ps1"
$RepairScript     = Join-Path $PSScriptRoot "run-repair.ps1"
$RegressionScript = Join-Path $PSScriptRoot "run-regression.ps1"

$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"
$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"

$DevelopmentResult = Join-Path $ResultDir "development.md"
$QaResult = Join-Path $ResultDir "qa.md"
$TriageResult = Join-Path $ResultDir "triage.md"
$RegressionResult = Join-Path $ResultDir "regression.md"

# ==================================================
# HELPERS
# ==================================================

function Stop-Pipeline {
    param(
        [string]$Message,
        [int]$Code = 1
    )

    Write-Host ""
    Write-Host "========================================"
    Write-Host "PIPELINE STOPPED"
    Write-Host "========================================"
    Write-Host $Message
    Write-Host ""

    exit $Code
}

function Invoke-Stage {
    param(
        [string]$Name,
        [string]$Script,
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "========================================"
    Write-Host "STAGE: $Name"
    Write-Host "========================================"
    Write-Host ""

    & $Script @Arguments

    $StageExitCode = $LASTEXITCODE

    if ($StageExitCode -ne 0) {
        Stop-Pipeline "$Name failed with exit code $StageExitCode." $StageExitCode
    }
}

function Read-RequiredResult {
    param(
        [string]$Path,
        [string]$Name
    )

    if (!(Test-Path $Path)) {
        Stop-Pipeline "$Name result not found: $Path" 2
    }

    return Get-Content $Path -Raw
}

# ==================================================
# START
# ==================================================

Write-Host ""
Write-Host "========================================"
Write-Host "DESIGNMODE AUTONOMOUS ENGINEERING"
Write-Host "========================================"
Write-Host ""
Write-Host "Task: $TaskId"
Write-Host "Maximum repair attempts: $MaxRepairAttempts"
Write-Host ""

if (!(Test-Path $Task)) {
    throw "Approved task not found: $Task"
}

foreach ($Script in @(
    $DeveloperScript,
    $QaScript,
    $TriageScript,
    $RepairScript,
    $RegressionScript
)) {
    if (!(Test-Path $Script)) {
        throw "Required orchestrator script not found: $Script"
    }
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

# ==================================================
# DEVELOPER
# ==================================================

$DeveloperAction = Get-PipelineDeveloperStageAction `
    -RepoRoot $RepoRoot `
    -TaskId $TaskId `
    -ResultDir $ResultDir
if ($DeveloperAction -eq 'REUSE') {
    Write-Host "Recovered verified Developer result for $TaskId; implementation work was not rerun."
}

if ($DeveloperAction -eq 'RUN' -and !(Test-Path -LiteralPath $DevelopmentResult -PathType Leaf)) {
    Invoke-Stage -Name "DEVELOPER" -Script $DeveloperScript -Arguments @($TaskId)
}

$DevelopmentText = Read-RequiredResult `
    -Path $DevelopmentResult `
    -Name "Developer"

Write-Host "Developer stage completed."

# ==================================================
# QA
# ==================================================

$DevelopmentBranch = Get-PipelineStageResultField -Text $DevelopmentText -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"
$ImplementationRoot = Get-PipelineImplementationRoot -RepoRoot $RepoRoot -Branch $DevelopmentBranch
if (!$ImplementationRoot) { Stop-Pipeline "Could not resolve the Developer implementation worktree." 2 }
$ImplementationHead = (git -C $ImplementationRoot rev-parse HEAD).Trim()
$QaHeadFile = Join-Path $ResultDir "qa-head.txt"
$QaReusable = Test-PipelineStageReusable -ResultPath $QaResult -HeadPath $QaHeadFile -ExpectedHead $ImplementationHead -StatusPattern '(?im)^STATUS:\s*PASS(?:_WITH_TEST_FAILURES_PENDING_TRIAGE)?\s*$'
if (!$QaReusable) { Invoke-Stage -Name "QA" -Script $QaScript -Arguments @($TaskId); Set-Content -LiteralPath $QaHeadFile -Value $ImplementationHead -Encoding UTF8 }

$QaText = Read-RequiredResult `
    -Path $QaResult `
    -Name "QA"

Write-Host "QA stage completed."

# ==================================================
# TRIAGE
# ==================================================

$TriageHeadFile = Join-Path $ResultDir "triage-head.txt"
$TriageReusable = Test-PipelineStageReusable -ResultPath $TriageResult -HeadPath $TriageHeadFile -ExpectedHead $ImplementationHead -StatusPattern '(?im)^NEXT_ACTION:\s*(?:REPAIR|REGRESSION)\s*$'
if (!$TriageReusable) { Invoke-Stage -Name "TRIAGE" -Script $TriageScript -Arguments @($TaskId); Set-Content -LiteralPath $TriageHeadFile -Value $ImplementationHead -Encoding UTF8 }

$TriageText = Read-RequiredResult `
    -Path $TriageResult `
    -Name "Triage"

# ==================================================
# TRIAGE ROUTING
# ==================================================

$TriageAction = Get-PipelineTriageAction -Text $TriageText -ResultName "Triage result for '$TaskId'"
if ($TriageAction -eq 'REGRESSION') {
    $EvidenceSummaryPath = Join-Path $ResultDir 'qa-evidence-summary.txt'
    if (-not (Test-Path -LiteralPath $EvidenceSummaryPath -PathType Leaf)) { Stop-Pipeline 'QA evidence summary is missing; Regression routing is not safe.' 21 }
    Assert-PipelineRegressionEligible -TriageText $TriageText -QaText $QaText `
        -EvidenceSummaryText (Get-Content -LiteralPath $EvidenceSummaryPath -Raw) `
        -TaskText (Get-Content -LiteralPath $Task -Raw) -ResultName "Triage result for '$TaskId'"
}

switch ($TriageAction) {
    'HUMAN_REQUIRED' {

    Stop-Pipeline `
        "Triage requires human investigation. Review $TriageResult" `
        20
    }
    'REPAIR' { $NeedRepair = $true }
    'REGRESSION' { $NeedRepair = $false }
}

# ==================================================
# REPAIR LOOP
# ==================================================

$RepairAttempt = 0

while ($true) {

    # ----------------------------------------------
    # Repair if currently required
    # ----------------------------------------------

    if ($NeedRepair) {

        $RepairAttempt++

        if ($RepairAttempt -gt $MaxRepairAttempts) {

            Stop-Pipeline `
                "Maximum autonomous repair attempts reached. Human investigation required." `
                30
        }

        Invoke-Stage `
            -Name "REPAIR #$RepairAttempt" `
            -Script $RepairScript `
            -Arguments @($TaskId, "$RepairAttempt")

        $RepairResult =
            Join-Path $ResultDir "repair-$RepairAttempt.md"

        $RepairText = Read-RequiredResult `
            -Path $RepairResult `
            -Name "Repair #$RepairAttempt"

        if ($RepairText -match '(?im)^STATUS:\s*HUMAN_REQUIRED\s*$') {

            Stop-Pipeline `
                "Repair Agent requires human investigation. Review $RepairResult" `
                31
        }

        if ($RepairText -match '(?im)^STATUS:\s*REPAIR_FAILED\s*$') {

            if ($RepairAttempt -ge $MaxRepairAttempts) {

                Stop-Pipeline `
                    "Repair failed and maximum repair attempts were reached." `
                    32
            }

            Write-Host ""
            Write-Host "Repair attempt $RepairAttempt failed."
            Write-Host "Trying another autonomous repair attempt..."

            $NeedRepair = $true
            continue
        }

        if ($RepairText -notmatch '(?im)^STATUS:\s*REPAIRED\s*$') {

            Stop-Pipeline `
                "Repair returned an unknown STATUS. Review $RepairResult" `
                33
        }

        # ------------------------------------------
        # Independent QA after repair
        # ------------------------------------------

        $ImplementationHead = (git -C $ImplementationRoot rev-parse HEAD).Trim()
        Invoke-Stage -Name "POST-REPAIR QA" -Script $QaScript -Arguments @($TaskId)
        Set-Content -LiteralPath $QaHeadFile -Value $ImplementationHead -Encoding UTF8

        $QaText = Read-RequiredResult `
            -Path $QaResult `
            -Name "Post-repair QA"

        # ------------------------------------------
        # Triage after repair
        # ------------------------------------------

        $ImplementationHead = (git -C $ImplementationRoot rev-parse HEAD).Trim()
        Invoke-Stage -Name "POST-REPAIR TRIAGE" -Script $TriageScript -Arguments @($TaskId)
        Set-Content -LiteralPath $TriageHeadFile -Value $ImplementationHead -Encoding UTF8

        $TriageText = Read-RequiredResult `
            -Path $TriageResult `
            -Name "Post-repair Triage"

        $PostRepairAction = Get-PipelineTriageAction -Text $TriageText -ResultName "Post-repair Triage result for '$TaskId'"
        if ($PostRepairAction -eq 'REGRESSION') {
            $EvidenceSummaryPath = Join-Path $ResultDir 'qa-evidence-summary.txt'
            if (-not (Test-Path -LiteralPath $EvidenceSummaryPath -PathType Leaf)) { Stop-Pipeline 'Post-repair QA evidence summary is missing; Regression routing is not safe.' 35 }
            Assert-PipelineRegressionEligible -TriageText $TriageText -QaText $QaText `
                -EvidenceSummaryText (Get-Content -LiteralPath $EvidenceSummaryPath -Raw) `
                -TaskText (Get-Content -LiteralPath $Task -Raw) -ResultName "Post-repair Triage result for '$TaskId'"
        }
        if ($PostRepairAction -eq 'HUMAN_REQUIRED') {

            Stop-Pipeline `
                "Post-repair Triage requires human investigation." `
                34
        }

        if ($PostRepairAction -eq 'REPAIR') {

            $NeedRepair = $true
            continue
        }

        # The next iteration skips the repair block and proceeds to Regression.
        $NeedRepair = $false
        continue
    }

    # ==================================================
    # REGRESSION
    # ==================================================

    $RegressionHeadFile = Join-Path $ResultDir "regression-head.txt"
    $RegressionReusable = Test-PipelineStageReusable -ResultPath $RegressionResult -HeadPath $RegressionHeadFile -ExpectedHead $ImplementationHead -StatusPattern '(?im)^STATUS:\s*READY_FOR_HUMAN_REVIEW\s*$'
    if (!$RegressionReusable) { Invoke-Stage -Name "REGRESSION" -Script $RegressionScript -Arguments @($TaskId); Set-Content -LiteralPath $RegressionHeadFile -Value $ImplementationHead -Encoding UTF8 }

    $RegressionText = Read-RequiredResult `
        -Path $RegressionResult `
        -Name "Regression"

    # ==================================================
    # FINAL ROUTING
    # ==================================================

    if ($RegressionText -match
        '(?im)^STATUS:\s*READY_FOR_HUMAN_REVIEW\s*$') {

        Write-Host ""
        Write-Host "========================================"
        Write-Host "READY FOR HUMAN REVIEW"
        Write-Host "========================================"
        Write-Host ""
        Write-Host "Task:   $TaskId"
        Write-Host ""
        Write-Host "Development: PASS"
        Write-Host "QA:          PASS"
        Write-Host "Triage:      PASS"
        Write-Host "Regression:  PASS"
        Write-Host "Repairs:     $RepairAttempt"
        Write-Host ""
        Write-Host "Nothing pushed."
        Write-Host "Nothing merged."
        Write-Host "Nothing deployed."
        Write-Host ""
        Write-Host "Human action: MERGE REVIEW"
        Write-Host ""

        exit 0
    }

    if ($RegressionText -match
        '(?im)^STATUS:\s*HUMAN_REQUIRED\s*$') {

        Stop-Pipeline `
            "Regression requires human investigation. Review $RegressionResult" `
            40
    }

    if ($RegressionText -match
        '(?im)^STATUS:\s*REPAIR_REQUIRED\s*$') {

        Write-Host ""
        Write-Host "Regression discovered a repair requirement."

        if ($RepairAttempt -ge $MaxRepairAttempts) {

            Stop-Pipeline `
                "Regression requires another repair but maximum attempts were reached." `
                41
        }

        $NeedRepair = $true
        continue
    }

    Stop-Pipeline `
        "Regression returned an unknown STATUS. Review $RegressionResult" `
        42
}
