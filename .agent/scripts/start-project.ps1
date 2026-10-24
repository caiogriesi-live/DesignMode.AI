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
$RunningRoot = Join-Path $RepoRoot ".agent/projects/running"
$RunningDir = Join-Path $RunningRoot $ProjectId

$StateFile = Join-Path $RunningDir "state.json"
$EventsFile = Join-Path $RunningDir "events.jsonl"

# ==================================================
# HEADER
# ==================================================

Write-Host ""
Write-Host "DESIGNMODE PROJECT START"
Write-Host "========================"
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

if (Test-Path -LiteralPath $StateFile) {
    Write-Host "ERROR: Project already has running state:"
    Write-Host $StateFile
    exit 10
}

$ProjectText = Get-Content -LiteralPath $ApprovedProject -Raw

if ($ProjectText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    throw "Approved project does not contain the expected approval status."
}

# ==================================================
# PARSE ENGINEERING WORK ITEMS
# ==================================================

$WorkSectionMatch = [regex]::Match(
    $ProjectText,
    '(?ms)^## Engineering Work Items\s*(?<body>.*?)(?=^##\s|\z)'
)

if (!$WorkSectionMatch.Success) {
    throw "Engineering Work Items section not found."
}

$WorkSection = $WorkSectionMatch.Groups["body"].Value

$WorkMatches = [regex]::Matches(
    $WorkSection,
    '(?ms)^###\s+(?<id>WORK-\d+):\s*(?<title>[^\r\n]+)\s*(?<body>.*?)(?=^###\s+WORK-\d+:|\z)'
)

if ($WorkMatches.Count -eq 0) {
    throw "No engineering work items found."
}

$WorkItems = @()

foreach ($Match in $WorkMatches) {

    $WorkId = $Match.Groups["id"].Value.Trim()
    $Title = $Match.Groups["title"].Value.Trim()
    $Body = $Match.Groups["body"].Value

    $DependencyMatch = [regex]::Match(
        $Body,
        '(?ms)^Depends On:\s*(?<deps>.*?)(?=^\s*(?:Protected Systems:|Objective:)|\z)'
    )

    if (!$DependencyMatch.Success) {
        throw "Depends On section not found for $WorkId."
    }

    $DependencyText = $DependencyMatch.Groups["deps"].Value.Trim()

    if ($DependencyText -match '^(?i)NONE$') {
        $Dependencies = @()
    }
    else {
        $Dependencies = @(
            [regex]::Matches($DependencyText, 'WORK-\d+') |
                ForEach-Object { $_.Value } |
                Select-Object -Unique
        )
    }

    $Status = if ($Dependencies.Count -eq 0) {
        "ELIGIBLE"
    }
    else {
        "WAITING"
    }

    $WorkItems += [ordered]@{
        id          = $WorkId
        title       = $Title
        dependsOn   = @($Dependencies)
        status      = $Status
        taskId      = $null
        branch      = $null
        result      = $null
        humanAction = $null
    }
}

# ==================================================
# VALIDATE DEPENDENCIES
# ==================================================

$KnownIds = @($WorkItems | ForEach-Object { $_.id })

foreach ($Item in $WorkItems) {
    foreach ($Dependency in $Item.dependsOn) {
        if ($Dependency -notin $KnownIds) {
            throw "$($Item.id) references unknown dependency $Dependency."
        }

        if ($Dependency -eq $Item.id) {
            throw "$($Item.id) cannot depend on itself."
        }
    }
}

# ==================================================
# CREATE INITIAL STATE
# ==================================================

New-Item -ItemType Directory -Force -Path $RunningDir | Out-Null

$RelativeApprovedProject = ".agent/projects/approved/$ProjectId.md"

$State = [ordered]@{
    schemaVersion   = 1
    projectId       = $ProjectId
    projectStatus   = "RUNNING"
    approvedProject = $RelativeApprovedProject
    currentWorkItem = $null
    humanAction     = $null
    workItems       = @($WorkItems)
}

$StateJson = $State | ConvertTo-Json -Depth 10

[System.IO.File]::WriteAllText(
    $StateFile,
    $StateJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)

# ==================================================
# CREATE EVENT STREAM
# ==================================================

$StartEvent = [ordered]@{
    timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
    projectId  = $ProjectId
    workItemId = $null
    agent      = "PROJECT_ORCHESTRATOR"
    type       = "PROJECT_STARTED"
    message    = "Project entered RUNNING state."
}

$EventJson = $StartEvent | ConvertTo-Json -Compress

[System.IO.File]::WriteAllText(
    $EventsFile,
    $EventJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)

foreach ($Item in $WorkItems) {

    if ($Item.status -ne "ELIGIBLE") {
        continue
    }

    $EligibleEvent = [ordered]@{
        timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
        projectId  = $ProjectId
        workItemId = $Item.id
        agent      = "PROJECT_ORCHESTRATOR"
        type       = "WORK_ITEM_ELIGIBLE"
        message    = "$($Item.id) became eligible because all dependencies are satisfied."
    }

    $EligibleJson = $EligibleEvent | ConvertTo-Json -Compress

    [System.IO.File]::AppendAllText(
        $EventsFile,
        $EligibleJson + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

# ==================================================
# SUMMARY
# ==================================================

Write-Host "Project state created."
Write-Host ""

foreach ($Item in $WorkItems) {
    $Deps = if ($Item.dependsOn.Count -eq 0) {
        "NONE"
    }
    else {
        $Item.dependsOn -join ", "
    }

    Write-Host ("{0,-8} {1,-10} Depends on: {2}" -f `
        $Item.id,
        $Item.status,
        $Deps
    )
}

Write-Host ""
Write-Host "State:"
Write-Host $StateFile
Write-Host ""
Write-Host "Events:"
Write-Host $EventsFile
Write-Host ""
Write-Host "No agents started."
Write-Host "No application code modified."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."
Write-Host ""
Write-Host "PROJECT START STAGE COMPLETE"