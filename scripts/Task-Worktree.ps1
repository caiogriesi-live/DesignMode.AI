#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Create', 'Cleanup')][string]$Operation,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$TaskId,
    [string]$TaskName,
    # Required for cleanup only when a task ID has multiple launches.
    [string]$Branch
)

$ErrorActionPreference = 'Stop'
function Invoke-Git {
    param([string[]]$GitArgs)
    $result = & git -C $script:repository @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "Git failed: git $($GitArgs -join ' ')" }
    return $result
}

$script:repository = Split-Path $PSScriptRoot -Parent
$common = [string](Invoke-Git @('rev-parse', '--git-common-dir'))
if (-not [IO.Path]::IsPathRooted($common)) { $common = Join-Path $script:repository $common }
$common = [IO.Path]::GetFullPath($common)
# This repository uses a normal main checkout with its common .git directory.
if ((Split-Path $common -Leaf) -ne '.git') { throw 'A normal main checkout with a .git directory is required.' }
$root = Join-Path (Split-Path $common -Parent) 'worktrees'
$state = Join-Path $common 'codex-task-worktrees'
[IO.Directory]::CreateDirectory($state) | Out-Null

# Keep the lock file: deleting it can allow two processes to lock different files.
# FileShare.None releases automatically on process exit, including crashes.
$lock = $null
$deadline = [DateTime]::UtcNow.AddMinutes(5)
try {
    while ($null -eq $lock) {
        try {
            $lock = [IO.File]::Open((Join-Path $state 'launch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the task worktree lock.' }
            Start-Sleep -Milliseconds 200
        }
    }

    if ($Operation -eq 'Create') {
        if ([string]::IsNullOrWhiteSpace($TaskName)) { throw 'Create requires -TaskName.' }
        if ($Branch) { throw '-Branch is only supported for cleanup.' }
        $slug = ($TaskName.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
        if (-not $slug) { $slug = 'task' }
        $slug = $slug.Substring(0, [Math]::Min(48, $slug.Length)).TrimEnd('-')
        Invoke-Git @('fetch', '--no-tags', 'origin', '+refs/heads/*:refs/remotes/origin/*') | Out-Null
        $base = [string](Invoke-Git @('rev-parse', '--verify', 'refs/remotes/origin/master-caio^{commit}'))
        [IO.Directory]::CreateDirectory($root) | Out-Null
        $stamp = [DateTime]::UtcNow
        $refs = @(Invoke-Git @('for-each-ref', '--format=%(refname)', 'refs/heads', 'refs/remotes/origin'))
        do {
            $name = "$TaskId-$slug-$($stamp.ToString('yyyyMMdd-HHmmss'))"
            $taskBranch = "codex/$name"
            $path = Join-Path $root $name
            $recordPath = Join-Path $state "$name.json"
            $occupied = $refs -contains "refs/heads/$taskBranch" -or
                $refs -contains "refs/remotes/origin/$taskBranch" -or
                (Test-Path -LiteralPath $path) -or (Test-Path -LiteralPath $recordPath)
            $stamp = $stamp.AddSeconds(1)
        } while ($occupied)
        Invoke-Git @('check-ref-format', '--branch', $taskBranch) | Out-Null
        # Save the reservation first. An interrupted launch keeps its name reserved.
        $record = [pscustomobject]@{ TaskId = $TaskId; TaskName = $TaskName; Branch = $taskBranch; WorktreePath = $path; BaseCommit = $base }
        $record | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding UTF8
        Invoke-Git @('worktree', 'add', '--no-track', '-b', $taskBranch, $path, $base) | Out-Null
        $record
    }
    else {
        $records = @(Get-ChildItem -LiteralPath $state -Filter '*.json' | ForEach-Object {
            $entry = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            if ($entry.TaskId -eq $TaskId -and (-not $Branch -or $entry.Branch -eq $Branch)) {
                [pscustomobject]@{ File = $_.FullName; Entry = $entry }
            }
        })
        if ($records.Count -ne 1) { throw 'Expected one task record. Check the task ID; for repeated launches specify -Branch.' }
        $record = $records[0].Entry
        $path = [IO.Path]::GetFullPath($record.WorktreePath)
        $expectedName = $record.Branch -replace '^codex/', ''
        if ($record.Branch -notmatch "^codex/$TaskId-[a-z0-9-]+-\d{8}-\d{6}$" -or
            $path -ne [IO.Path]::GetFullPath((Join-Path $root $expectedName))) {
            throw 'Task record does not point to its expected worktree directory.'
        }
        $registered = @(Invoke-Git @('-c', 'core.quotePath=false', 'worktree', 'list', '--porcelain'))
        if ($registered -notcontains "worktree $($path.Replace('\', '/'))") { throw 'Worktree is not registered; inspect the interrupted launch manually.' }
        $currentBranch = [string](Invoke-Git @('-C', $path, 'symbolic-ref', '--short', 'HEAD'))
        if ($currentBranch -ne $record.Branch) { throw 'Worktree branch has changed; restore the assigned branch before cleanup.' }
        # Include ignored files: local settings and build output should not be silently lost.
        $dirty = @(Invoke-Git @('-C', $path, 'status', '--porcelain', '--untracked-files=all', '--ignored'))
        if ($dirty.Count) { throw 'Worktree contains changed, untracked, or ignored files. Preserve or remove them explicitly before cleanup.' }
        Invoke-Git @('worktree', 'remove', $path) | Out-Null
        Remove-Item -LiteralPath $records[0].File
        $record
    }
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
}
