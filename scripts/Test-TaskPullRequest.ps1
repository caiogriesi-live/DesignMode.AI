#requires -Version 5.1
# Read-only safety tests against a real managed, committed task worktree.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorktreePath,
    [Parameter(Mandatory)][string]$TaskId,
    [Parameter(Mandatory)][string]$TaskName,
    [Parameter(Mandatory)][string]$Branch,
    [Parameter(Mandatory)][string]$TaskCommit,
    [Parameter(Mandatory)][string]$ValidationPath
)
$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot 'Publish-TaskPullRequest.ps1'
$arguments = @{WorktreePath=$WorktreePath;TaskId=$TaskId;TaskName=$TaskName;Branch=$Branch;TaskCommit=$TaskCommit;ValidationPath=$ValidationPath;CheckOnly=$true}
function Assert-Rejected {
    param([string]$Name, [hashtable]$Overrides, [string]$Expected)
    $case = $arguments.Clone()
    foreach ($key in $Overrides.Keys) { $case[$key] = $Overrides[$key] }
    $failure = $null
    try { & $publisher @case | Out-Null } catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch $Expected) { throw "FAIL: $Name (unexpected result: $failure)" }
    "PASS: $Name"
}
Assert-Rejected 'main is protected' @{Branch='main'} 'Only the assigned'
Assert-Rejected 'master-caio is protected' @{Branch='master-caio'} 'Only the assigned'
Assert-Rejected 'additional protected branch' @{ProtectedBranches=@($Branch)} 'Only the assigned'
Assert-Rejected 'wrong task ID' @{TaskId='999999'} 'Only the assigned'
Assert-Rejected 'wrong task name' @{TaskName='different-task'} 'Task name'
Assert-Rejected 'wrong worktree' @{WorktreePath=$PSScriptRoot} 'does not belong'
Assert-Rejected 'missing task commit' @{TaskCommit=('0' * 40)} 'Task commit must exist'
Assert-Rejected 'wrong target' @{TargetBranch='main'} 'ValidateSet|does not belong to the set'
$tempValidation = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
try {
    @{Commit=$TaskCommit;Results=@(@{Command='failed test';ExitCode=1})} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $tempValidation
    Assert-Rejected 'failed validation' @{ValidationPath=$tempValidation} 'Every reported validation'
    @{Commit=('0' * 40);Results=@(@{Command='stale test';ExitCode=0})} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $tempValidation
    Assert-Rejected 'stale validation' @{ValidationPath=$tempValidation} 'Validation must describe'
} finally { Remove-Item -LiteralPath $tempValidation }
$preflight = & $publisher @arguments
if (-not $preflight.Checked -or $preflight.Description -match 'System.Object\[\]' -or
    $preflight.Description -notmatch 'Files changed:\r?\n- ' -or $preflight.Description -notmatch 'requires review before merge') { throw 'FAIL: preflight metadata' }
'PASS: authenticated preflight and PR metadata (no push or PR creation)'
