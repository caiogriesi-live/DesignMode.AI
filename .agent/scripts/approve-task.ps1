param(
    [Parameter(Mandatory = $true)]
    [string]$TaskId
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$DraftDir = Join-Path $RepoRoot ".agent/tasks/draft"
$ApprovedDir = Join-Path $RepoRoot ".agent/tasks/approved"

$DraftTask = Join-Path $DraftDir "$TaskId.md"
$ApprovedTask = Join-Path $ApprovedDir "$TaskId.md"
$RunningProjectsDir = Join-Path $RepoRoot ".agent/projects/running"

Write-Host ""
Write-Host "DESIGNMODE TASK APPROVAL"
Write-Host "========================"
Write-Host "Task: $TaskId"
Write-Host ""

if (!(Test-Path $DraftTask)) {
    throw "Draft task not found: $DraftTask"
}

$DraftText = Get-Content $DraftTask -Raw

if ($DraftText -notmatch '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {
    throw "Task is not READY_FOR_APPROVAL."
}

if ($DraftText -match '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {
    throw "Task still requires clarification."
}

New-Item -ItemType Directory -Force -Path $ApprovedDir | Out-Null

# Remove Planner-only status section from executable task.
$ApprovedText = $DraftText -replace `
    '(?ms)\r?\n## Planner Status\s*\r?\n\s*STATUS:\s*READY_FOR_APPROVAL\s*\r?\n?', `
    "`r`n"

if (Test-Path -LiteralPath $ApprovedTask -PathType Leaf) {

    # Approval is idempotent only when the existing approved task represents
    # the same executable task content.
    $ExistingApprovedText = Get-Content -LiteralPath $ApprovedTask -Raw

    $Normalize = {
        param([string]$Text)

        return (($Text -replace "`r`n", "`n").Trim())
    }

    if ((& $Normalize $ExistingApprovedText) -ne
        (& $Normalize $ApprovedText)) {

        throw "Approved task already exists with different content: $ApprovedTask"
    }

    Write-Host "Approved task already exists with matching content."
    Write-Host "Synchronizing runtime state."
}
else {

    Set-Content `
        -LiteralPath $ApprovedTask `
        -Value $ApprovedText `
        -Encoding UTF8
}

# Synchronize project runtime state when this task belongs to a running project.
$MatchingStates = @()

if (Test-Path -LiteralPath $RunningProjectsDir -PathType Container) {
    foreach ($StateFile in Get-ChildItem `
        -LiteralPath $RunningProjectsDir `
        -Filter "state.json" `
        -File `
        -Recurse) {

        try {
            $Candidate = Get-Content -LiteralPath $StateFile.FullName -Raw |
                ConvertFrom-Json

            $MatchingItem = @(
                $Candidate.workItems |
                    Where-Object { $_.taskId -eq $TaskId }
            )

            if ($MatchingItem.Count -gt 0) {
                $MatchingStates += [pscustomobject]@{
                    File  = $StateFile
                    State = $Candidate
                    Items = $MatchingItem
                }
            }
        }
        catch {
            throw "Unable to read project runtime state $($StateFile.FullName): $($_.Exception.Message)"
        }
    }
}

if ($MatchingStates.Count -gt 1) {
    throw "Task '$TaskId' is associated with more than one running project."
}

if ($MatchingStates.Count -eq 1) {
    $Runtime = $MatchingStates[0]
    $State = $Runtime.State
    $Items = @($Runtime.Items)

    if ($Items.Count -ne 1) {
        throw "Task '$TaskId' is associated with multiple work items in the same project."
    }

    $Item = $Items[0]

    if ($Item.status -ne "AWAITING_APPROVAL") {
        throw "Expected task '$TaskId' to be AWAITING_APPROVAL, but runtime state is '$($Item.status)'."
    }

    $Item.status = "APPROVED"
    $Item.result = ".agent/tasks/approved/$TaskId.md"
    $Item.humanAction = $null

    $State.currentWorkItem = $Item.id
    $State.humanAction = $null

    $Json = $State | ConvertTo-Json -Depth 20

    [System.IO.File]::WriteAllText(
        $Runtime.File.FullName,
        $Json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )

    $EventsFile = Join-Path $Runtime.File.Directory.FullName "events.jsonl"

    if (Test-Path -LiteralPath $EventsFile -PathType Leaf) {
        $Event = [ordered]@{
            timestamp  = [DateTimeOffset]::UtcNow.ToString("o")
            projectId  = $State.projectId
            workItemId = $Item.id
            agent      = "PROJECT_ORCHESTRATOR"
            type       = "WORK_ITEM_APPROVED"
            message    = "Engineering task $TaskId approved for execution."
        }

        [System.IO.File]::AppendAllText(
            $EventsFile,
            ($Event | ConvertTo-Json -Compress) + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false)
        )
    }
}

Write-Host "========================================"
Write-Host "TASK APPROVED"
Write-Host "========================================"
Write-Host ""
Write-Host "Approved task:"
Write-Host $ApprovedTask
Write-Host ""
Write-Host "The task has NOT been executed."
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."
Write-Host ""
Write-Host "Next command:"
Write-Host ".\.agent\scripts\run-task.ps1 $TaskId"
Write-Host ""