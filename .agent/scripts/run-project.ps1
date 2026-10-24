param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$ApprovedProject = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"
$RunningDir = Join-Path $RepoRoot ".agent/projects/running/$ProjectId"
$StateFile = Join-Path $RunningDir "state.json"
$EventsFile = Join-Path $RunningDir "events.jsonl"

$PlanTaskScript = Join-Path $RepoRoot ".agent/scripts/plan-task.ps1"

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

    $Json = $Event | ConvertTo-Json -Compress

    [System.IO.File]::AppendAllText(
        $EventsFile,
        $Json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

# ==================================================
# HEADER
# ==================================================

Write-Host ""
Write-Host "DESIGNMODE PROJECT ORCHESTRATOR"
Write-Host "==============================="
Write-Host "Project: $ProjectId"
Write-Host ""

# ==================================================
# VALIDATION
# ==================================================

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    throw "ProjectId cannot be empty."
}

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Approved project not found: $ApprovedProject"
}

if (!(Test-Path -LiteralPath $StateFile -PathType Leaf)) {
    throw "Running project state not found. Run start-project.ps1 first."
}

if (!(Test-Path -LiteralPath $EventsFile -PathType Leaf)) {
    throw "Project event stream not found."
}

if (!(Test-Path -LiteralPath $PlanTaskScript -PathType Leaf)) {
    throw "Planner script not found: $PlanTaskScript"
}

# ==================================================
# LOAD STATE
# ==================================================

$State = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json

if ($State.projectStatus -ne "RUNNING") {
    throw "Project is not RUNNING. Current status: $($State.projectStatus)"
}

$ActiveItems = @(
    $State.workItems |
        Where-Object {
            $_.status -in @(
                "PLANNING",
                "AWAITING_APPROVAL",
                "RUNNING",
                "HUMAN_REQUIRED"
            )
        }
)

if ($ActiveItems.Count -gt 0) {
    Write-Host "Project already has an active work item:"
    foreach ($Item in $ActiveItems) {
        Write-Host "$($Item.id): $($Item.status)"
    }

    Write-Host ""
    Write-Host "No additional work will be delegated."
    exit 30
}

$EligibleItems = @(
    $State.workItems |
        Where-Object { $_.status -eq "ELIGIBLE" }
)

if ($EligibleItems.Count -eq 0) {
    Write-Host "No eligible work items are currently available."
    Write-Host ""
    Write-Host "The project may be waiting on dependencies or human action."
    exit 31
}

# v1 intentionally delegates only one work item at a time.
$WorkItem = $EligibleItems | Select-Object -First 1

$WorkId = $WorkItem.id
$TaskId = "$ProjectId-$($WorkId.ToLowerInvariant())"

# ==================================================
# READ WORK ITEM FROM APPROVED PROJECT
# ==================================================

$ProjectText = Get-Content -LiteralPath $ApprovedProject -Raw

$EscapedWorkId = [regex]::Escape($WorkId)

$WorkMatch = [regex]::Match(
    $ProjectText,
    "(?ms)^###\s+${EscapedWorkId}:\s*(?<title>[^\r\n]+)\s*(?<body>.*?)(?=^###\s+WORK-\d+:|^##\s|\z)"
)

if (!$WorkMatch.Success) {
    throw "Unable to locate $WorkId in approved project."
}

$WorkTitle = $WorkMatch.Groups["title"].Value.Trim()
$WorkBody = $WorkMatch.Groups["body"].Value.Trim()

$ObjectiveMatch = [regex]::Match(
    $WorkBody,
    '(?ms)^Objective:\s*(?<value>.*?)(?=^Depends On:)'
)

$ProtectedMatch = [regex]::Match(
    $WorkBody,
    '(?ms)^Protected Systems:\s*(?<value>.*?)(?=^###\s+WORK-\d+:|^##\s|\z)'
)

if (!$ObjectiveMatch.Success) {
    throw "Objective not found for $WorkId."
}

if (!$ProtectedMatch.Success) {
    throw "Protected Systems not found for $WorkId."
}

$Objective = $ObjectiveMatch.Groups["value"].Value.Trim()
$ProtectedSystems = $ProtectedMatch.Groups["value"].Value.Trim()

# ==================================================
# MARK PLANNING
# ==================================================

$WorkItem.status = "PLANNING"
$WorkItem.taskId = $TaskId

$State.currentWorkItem = $WorkId

Save-State $State

Add-ProjectEvent `
    -Type "WORK_ITEM_PLANNING_STARTED" `
    -WorkItemId $WorkId `
    -Agent "PROJECT_ORCHESTRATOR" `
    -Message "$WorkId delegated to Planner as task $TaskId."

# ==================================================
# BUILD PLANNER REQUEST
# ==================================================

$PlannerRequest = @"
This engineering task belongs to the approved project:

$ProjectId

Work item:

$WorkId - $WorkTitle

Objective:

$Objective

Protected systems and constraints:

$ProtectedSystems

Read the approved parent project at:

.agent/projects/approved/$ProjectId.md

Use that project as authoritative business and product context.

Inspect the repository before planning.

Create a precise engineering task with acceptance criteria for this work item
only.

Do not expand scope into later project work items.

Preserve the parent project's approved definitions, dependencies, out-of-scope
items, and protected systems.

Do not implement anything.

Do not push.

Do not merge.

Do not deploy.
"@

# ==================================================
# RUN PLANNER
# ==================================================

Write-Host "Delegating:"
Write-Host "$WorkId - $WorkTitle"
Write-Host ""
Write-Host "Planner task:"
Write-Host $TaskId
Write-Host ""

& $PlanTaskScript `
    -TaskId $TaskId `
    -Request $PlannerRequest

$PlannerExit = $LASTEXITCODE

# ==================================================
# HANDLE PLANNER RESULT
# ==================================================

if ($PlannerExit -ne 0) {

    $WorkItem.status = "HUMAN_REQUIRED"
    $WorkItem.result = "Planner exited with code $PlannerExit."
    $WorkItem.humanAction = "Review Planner failure."

    $State.projectStatus = "HUMAN_REQUIRED"
    $State.humanAction = "Review Planner failure for $WorkId."

    Save-State $State

    Add-ProjectEvent `
        -Type "WORK_ITEM_PLANNING_FAILED" `
        -WorkItemId $WorkId `
        -Agent "PLANNER" `
        -Message "Planner failed for $WorkId with exit code $PlannerExit."

    exit $PlannerExit
}

$DraftTask = Join-Path $RepoRoot ".agent/tasks/draft/$TaskId.md"

if (!(Test-Path -LiteralPath $DraftTask -PathType Leaf)) {

    $WorkItem.status = "HUMAN_REQUIRED"
    $WorkItem.result = "Planner did not create expected draft task."
    $WorkItem.humanAction = "Review Planner output."

    $State.projectStatus = "HUMAN_REQUIRED"
    $State.humanAction = "Planner output missing for $WorkId."

    Save-State $State

    Add-ProjectEvent `
        -Type "WORK_ITEM_PLANNING_FAILED" `
        -WorkItemId $WorkId `
        -Agent "PLANNER" `
        -Message "Expected draft task was not created for $WorkId."

    exit 40
}

$DraftText = Get-Content -LiteralPath $DraftTask -Raw

if ($DraftText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {

    $WorkItem.status = "HUMAN_REQUIRED"
    $WorkItem.result = "Planner task requires review or clarification."
    $WorkItem.humanAction = "Review generated Planner task."

    $State.projectStatus = "HUMAN_REQUIRED"
    $State.humanAction = "Review Planner task for $WorkId."

    Save-State $State

    Add-ProjectEvent `
        -Type "WORK_ITEM_HUMAN_REQUIRED" `
        -WorkItemId $WorkId `
        -Agent "PLANNER" `
        -Message "Planner output for $WorkId is not READY_FOR_APPROVAL."

    exit 41
}

# ==================================================
# AWAIT HUMAN APPROVAL
# ==================================================

$WorkItem.status = "AWAITING_APPROVAL"
$WorkItem.result = ".agent/tasks/draft/$TaskId.md"
$WorkItem.humanAction = "Approve generated engineering task."

$State.currentWorkItem = $WorkId
$State.humanAction = "Approve task $TaskId."

Save-State $State

Add-ProjectEvent `
    -Type "WORK_ITEM_PLANNED" `
    -WorkItemId $WorkId `
    -Agent "PLANNER" `
    -Message "$WorkId planning completed and is awaiting human approval."

Write-Host ""
Write-Host "========================================"
Write-Host "WORK ITEM AWAITING APPROVAL"
Write-Host "========================================"
Write-Host ""
Write-Host "Project:   $ProjectId"
Write-Host "Work item: $WorkId"
Write-Host "Task:      $TaskId"
Write-Host ""
Write-Host "Draft:"
Write-Host $DraftTask
Write-Host ""
Write-Host "No engineering started."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."