param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$WorkItemId,

    [Parameter(Mandatory = $true)]
    [string]$TaskId
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

$RunningDir = Join-Path $RepoRoot ".agent/projects/running/$ProjectId"
$StateFile = Join-Path $RunningDir "state.json"
$EventsFile = Join-Path $RunningDir "events.jsonl"

$RegressionResult = Join-Path `
    $RepoRoot `
    ".agent/results/$TaskId/regression.md"

# ==================================================
# HELPERS
# ==================================================

function Save-State {
    param($State)

    $Json = $State | ConvertTo-Json -Depth 20

    [System.IO.File]::WriteAllText(
        $StateFile,
        $Json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Add-ProjectEvent {
    param(
        [string]$Type,
        [string]$Message,
        [string]$WorkItemId = $null,
        [string]$Agent = "PROJECT_ORCHESTRATOR"
    )

    $Event = [ordered]@{
        timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
        projectId  = $ProjectId
        workItemId = $WorkItemId
        agent      = $Agent
        type       = $Type
        message    = $Message
    }

    [System.IO.File]::AppendAllText(
        $EventsFile,
        ($Event | ConvertTo-Json -Compress) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

# ==================================================
# VALIDATION
# ==================================================

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

if ($WorkItemId -notmatch '^WORK-\d+$') {
    throw "Invalid WorkItemId."
}

if ($TaskId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid TaskId."
}

if (!(Test-Path -LiteralPath $StateFile -PathType Leaf)) {
    throw "Project state not found: $StateFile"
}

if (!(Test-Path -LiteralPath $EventsFile -PathType Leaf)) {
    throw "Project event stream not found: $EventsFile"
}

if (!(Test-Path -LiteralPath $RegressionResult -PathType Leaf)) {
    throw "Regression result not found: $RegressionResult"
}

# ==================================================
# VERIFY REGRESSION
# ==================================================

$RegressionText = Get-Content -LiteralPath $RegressionResult -Raw

if ($RegressionText -notmatch
    '(?im)^STATUS:\s*READY_FOR_HUMAN_REVIEW\s*$') {

    throw "Regression has not reached READY_FOR_HUMAN_REVIEW."
}

if ($RegressionText -notmatch
    '(?im)^REPAIR_REQUIRED:\s*NO\s*$') {

    throw "Regression still requires repair."
}

$FeatureBranch = Get-PipelineStageResultField -Text $RegressionText -FieldName 'BRANCH' -ResultName "Regression result for '$TaskId'"

$HeadMatch = [regex]::Match(
    $RegressionText,
    '(?im)^HEAD:\s*(?<head>[0-9a-f]{7,40})\s*$'
)

if (!$HeadMatch.Success) {
    throw "Regression result does not identify HEAD."
}

$FeatureHead = $HeadMatch.Groups["head"].Value.Trim()

# ==================================================
# VERIFY MERGE
# ==================================================

$CurrentBranch = (
    git -C $RepoRoot branch --show-current
).Trim()

if ($LASTEXITCODE -ne 0 -or $CurrentBranch -ne "master-caio") {
    throw "Completion must be recorded from master-caio."
}

$MasterHead = (
    git -C $RepoRoot rev-parse HEAD
).Trim()

if ($LASTEXITCODE -ne 0) {
    throw "Unable to determine master-caio HEAD."
}

git -C $RepoRoot merge-base --is-ancestor $FeatureHead $MasterHead
$AncestorExit = $LASTEXITCODE

if ($AncestorExit -ne 0) {
    throw "Feature HEAD $FeatureHead is not merged into current master-caio."
}

# ==================================================
# LOAD PROJECT STATE
# ==================================================

$State = Get-Content -LiteralPath $StateFile -Raw |
    ConvertFrom-Json

if ($State.projectStatus -notin @("RUNNING", "HUMAN_REQUIRED")) {
    throw "Project cannot be completed from status '$($State.projectStatus)'."
}

$MatchingItems = @(
    $State.workItems |
        Where-Object { $_.id -eq $WorkItemId }
)

if ($MatchingItems.Count -ne 1) {
    throw "Expected exactly one work item named $WorkItemId."
}

$WorkItem = $MatchingItems[0]

if ($WorkItem.taskId -ne $TaskId) {
    throw "Runtime task '$($WorkItem.taskId)' does not match '$TaskId'."
}

# Idempotent resume.
if ($WorkItem.status -eq "COMPLETE") {

    Write-Host ""
    Write-Host "$WorkItemId is already COMPLETE."
    Write-Host "Nothing changed."
    exit 0
}

if ($WorkItem.status -ne "APPROVED") {
    throw "Expected $WorkItemId to be APPROVED, but it is '$($WorkItem.status)'."
}

# ==================================================
# COMPLETE WORK ITEM
# ==================================================

$WorkItem.status = "COMPLETE"
$WorkItem.branch = $FeatureBranch
$WorkItem.result = ".agent/results/$TaskId/regression.md"
$WorkItem.humanAction = $null

$State.currentWorkItem = $null
$State.humanAction = $null
$State.projectStatus = "RUNNING"

Add-ProjectEvent `
    -Type "WORK_ITEM_COMPLETED" `
    -WorkItemId $WorkItemId `
    -Message "$WorkItemId completed and merged into master-caio."

# ==================================================
# RECOMPUTE DEPENDENCIES
# ==================================================

foreach ($Candidate in $State.workItems) {

    if ($Candidate.status -ne "WAITING") {
        continue
    }

    $Dependencies = @($Candidate.dependsOn)

    $AllComplete = $true

    foreach ($DependencyId in $Dependencies) {

        $DependencyItems = @(
            $State.workItems |
                Where-Object { $_.id -eq $DependencyId }
        )

        if ($DependencyItems.Count -ne 1 -or
            $DependencyItems[0].status -ne "COMPLETE") {

            $AllComplete = $false
            break
        }
    }

    if (!$AllComplete) {
        continue
    }

    $Candidate.status = "ELIGIBLE"
    $Candidate.humanAction = $null

    Add-ProjectEvent `
        -Type "WORK_ITEM_ELIGIBLE" `
        -WorkItemId $Candidate.id `
        -Message "$($Candidate.id) became eligible because all dependencies are satisfied."
}

# ==================================================
# PROJECT COMPLETION
# ==================================================

$IncompleteItems = @(
    $State.workItems |
        Where-Object { $_.status -ne "COMPLETE" }
)

if ($IncompleteItems.Count -eq 0) {

    $State.projectStatus = "COMPLETE"
    $State.currentWorkItem = $null
    $State.humanAction = $null

    Add-ProjectEvent `
        -Type "PROJECT_COMPLETED" `
        -Message "All project work items are complete."
}

# Persist after all transitions/events have been determined.
Save-State $State

# ==================================================
# SUMMARY
# ==================================================

Write-Host ""
Write-Host "WORK ITEM COMPLETION RECORDED"
Write-Host "============================="
Write-Host ""
Write-Host "Project: $ProjectId"
Write-Host "Work:    $WorkItemId"
Write-Host "Task:    $TaskId"
Write-Host "Branch:  $FeatureBranch"
Write-Host "Head:    $FeatureHead"
Write-Host ""

foreach ($Item in $State.workItems) {
    Write-Host ("{0,-8} {1}" -f $Item.id, $Item.status)
}

Write-Host ""
Write-Host "Project status: $($State.projectStatus)"
