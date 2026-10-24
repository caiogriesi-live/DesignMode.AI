param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$ApprovedProject = Join-Path $RepoRoot ".agent/projects/approved/$ProjectId.md"
$RunningDir = Join-Path $RepoRoot ".agent/projects/running/$ProjectId"
$StateFile = Join-Path $RunningDir "state.json"
$EventsFile = Join-Path $RunningDir "events.jsonl"

$TaskDraftDir = Join-Path $RepoRoot ".agent/tasks/draft"
$TaskHistoryDir = Join-Path $RepoRoot ".agent/tasks/history/$ProjectId"

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
        [string]$WorkItemId = $null
    )

    $Event = [ordered]@{
        timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
        projectId  = $ProjectId
        workItemId = $WorkItemId
        agent      = "PROJECT_ORCHESTRATOR"
        type       = $Type
        message    = $Message
    }

    [System.IO.File]::AppendAllText(
        $EventsFile,
        ($Event | ConvertTo-Json -Compress) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

Write-Host ""
Write-Host "DESIGNMODE PROJECT STATE RESET"
Write-Host "=============================="
Write-Host "Project: $ProjectId"
Write-Host ""

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "Invalid ProjectId."
}

if (!(Test-Path -LiteralPath $ApprovedProject -PathType Leaf)) {
    throw "Approved project not found: $ApprovedProject"
}

if (!(Test-Path -LiteralPath $StateFile -PathType Leaf)) {
    throw "Running project state not found: $StateFile"
}

if (!(Test-Path -LiteralPath $EventsFile -PathType Leaf)) {
    throw "Project event stream not found: $EventsFile"
}

$State = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json

$Executing = @(
    $State.workItems |
        Where-Object { $_.status -eq "RUNNING" }
)

if ($Executing.Count -gt 0) {
    Write-Host "ERROR: Cannot reset while engineering work is RUNNING."
    foreach ($Item in $Executing) {
        Write-Host "$($Item.id): $($Item.status)"
    }
    exit 20
}

# Parse the work-item dependency graph from the newly approved project.
$ProjectText = Get-Content -LiteralPath $ApprovedProject -Raw

$Matches = [regex]::Matches(
    $ProjectText,
    '(?ms)^###\s+(?<id>WORK-\d+):\s*(?<title>[^\r\n]+).*?^Depends On:\s*(?<deps>[^\r\n]+)'
)

if ($Matches.Count -eq 0) {
    throw "No engineering work items found in approved project."
}

$NewItems = @()

foreach ($Match in $Matches) {

    $Id = $Match.Groups["id"].Value.Trim()
    $Title = $Match.Groups["title"].Value.Trim()
    $DependencyText = $Match.Groups["deps"].Value.Trim()

    if ($DependencyText -match '^(?i)NONE\b') {
        $Dependencies = @()
    }
    else {
        $Dependencies = @(
            [regex]::Matches($DependencyText, '\bWORK-\d+\b') |
                ForEach-Object { $_.Value } |
                Select-Object -Unique
        )
    }

    $NewItems += [ordered]@{
        id          = $Id
        title       = $Title
        dependsOn   = $Dependencies
        status      = "WAITING"
        taskId      = $null
        branch      = $null
        result      = $null
        humanAction = $null
    }
}

# Validate dependency references.
$Ids = @($NewItems | ForEach-Object { $_.id })

foreach ($Item in $NewItems) {
    foreach ($Dependency in $Item.dependsOn) {
        if ($Dependency -notin $Ids) {
            throw "$($Item.id) references unknown dependency $Dependency."
        }
    }
}

# Archive stale draft tasks belonging to this project.
$StaleDrafts = @(
    Get-ChildItem `
        -LiteralPath $TaskDraftDir `
        -Filter "$ProjectId-work-*.md" `
        -File `
        -ErrorAction SilentlyContinue
)

if ($StaleDrafts.Count -gt 0) {

    $Timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $ArchiveDir = Join-Path $TaskHistoryDir $Timestamp

    New-Item -ItemType Directory -Force -Path $ArchiveDir | Out-Null

    foreach ($Draft in $StaleDrafts) {
        Move-Item `
            -LiteralPath $Draft.FullName `
            -Destination (Join-Path $ArchiveDir $Draft.Name)

        Add-ProjectEvent `
            -Type "WORK_ITEM_TASK_INVALIDATED" `
            -WorkItemId (($Draft.BaseName -replace "^$([regex]::Escape($ProjectId))-", "").ToUpperInvariant()) `
            -Message "Draft task invalidated because the approved project was amended."
    }
}

# Replace runtime work graph with the amended project graph.
$State.workItems = $NewItems
$State.projectStatus = "RUNNING"
$State.currentWorkItem = $null
$State.humanAction = $null

# Determine initial eligibility from dependencies.
foreach ($Item in $State.workItems) {
    if (@($Item.dependsOn).Count -eq 0) {
        $Item.status = "ELIGIBLE"
    }
}

Save-State $State

Add-ProjectEvent `
    -Type "PROJECT_STATE_RECONCILED" `
    -Message "Runtime state reconciled against amended approved project."

foreach ($Item in $State.workItems) {
    if ($Item.status -eq "ELIGIBLE") {
        Add-ProjectEvent `
            -Type "WORK_ITEM_ELIGIBLE" `
            -WorkItemId $Item.id `
            -Message "$($Item.id) became eligible under the amended project."
    }
}

Write-Host ""
Write-Host "========================================"
Write-Host "PROJECT STATE RECONCILED"
Write-Host "========================================"
Write-Host ""

foreach ($Item in $State.workItems) {
    Write-Host "$($Item.id): $($Item.status)"
}

Write-Host ""
Write-Host "Stale Planner drafts archived: $($StaleDrafts.Count)"
Write-Host ""
Write-Host "No engineering started."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."