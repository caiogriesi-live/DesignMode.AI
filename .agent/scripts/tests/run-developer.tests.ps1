$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $PSScriptRoot '../pipeline-helpers.ps1')

$runner = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../run-developer.ps1') -Raw
$prompt = Get-Content -LiteralPath (Join-Path $RepoRoot '.agent/prompts/developer.md') -Raw
$nuget = Get-Content -LiteralPath (Join-Path $RepoRoot '.agent/nuget/NuGet.Config') -Raw

if ($runner -notmatch '--sandbox workspace-write' -or $runner -match 'danger-full-access') { throw 'Nested Codex sandbox is not explicitly restricted.' }
if ($prompt -match '(?im)^\s*(?:git\s+commit|commit completed work)' -or $prompt -notmatch 'Never commit, push, merge, or deploy') { throw 'Developer prompt does not reserve commit ownership for the outer wrapper.' }
if ($runner -notmatch 'git -C \$ImplementationRoot commit' -or $runner -notmatch 'PENDING_OUTER_COMMIT') { throw 'Outer wrapper does not own the feature commit and result SHA.' }
if ($runner -notmatch 'NUGET_PACKAGES = \$packageCache' -or $runner -notmatch 'NUGET_HTTP_CACHE_PATH = \$httpCache' -or $runner -notmatch 'DOTNET_CLI_HOME = \$dotnetHome') { throw 'NuGet and dotnet writable paths are not isolated to the task.' }
if (-not $runner.Contains("'--source',`$cacheSource") -or -not $runner.Contains("'--configfile',`$nugetConfig")) { throw 'Restore is not restricted to the configured local package cache.' }
if ($nuget -match '(?i)packageSourceCredentials|password|token|apikey') { throw 'Credential-free NuGet configuration contains credential fields.' }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('developer-wrapper-test-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $temp) { throw "Refusing to use pre-existing test directory: $temp" }
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $changed = @(Get-PipelineDeveloperChangedFiles -Text "TASK_ID: test-task`nSTATUS: IMPLEMENTED`nFILES_CHANGED:`n- DesignMode.AI.Client/Pages/Home.razor`nTESTS: Deferred" -ImplementationRoot $RepoRoot)
    if ($changed.Count -ne 1 -or $changed[0] -ne 'DesignMode.AI.Client/Pages/Home.razor') { throw 'Result file-list validation accepted unexpected content.' }
    foreach ($bad in @(
        "TASK_ID: test-task`nFILES_CHANGED:`n- ../outside.txt`nTESTS: Deferred",
        "TASK_ID: test-task`nFILES_CHANGED:`n- C:/outside.txt`nTESTS: Deferred",
        "TASK_ID: test-task`nFILES_CHANGED:`n- .agent/results/test-task/qa.md`nTESTS: Deferred",
        "TASK_ID: test-task`nFILES_CHANGED:`n- DesignMode.AI.Client/a.cs`n- DesignMode.AI.Client/a.cs`nTESTS: Deferred"
    )) {
        $rejected = $false
        try { Get-PipelineDeveloperChangedFiles -Text $bad -ImplementationRoot $RepoRoot | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Unsafe or malformed changed-file list was accepted.' }
    }

    $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $successLog = Join-Path $temp 'isolated/success.log'
    $successCode = Invoke-PipelineCheckedCommand -Executable $pwsh -ArgumentList @('-NoProfile','-Command','Write-Output verified; exit 0') -LogPath $successLog -Label 'test success' 6>$null
    if ($successCode -ne 0 -or -not (Test-Path -LiteralPath $successLog)) { throw 'Successful command result or isolated log was not recorded.' }

    $failureLog = Join-Path $temp 'isolated/failure.log'
    $failureRejected = $false
    try { Invoke-PipelineCheckedCommand -Executable $pwsh -ArgumentList @('-NoProfile','-Command','Write-Output expected-failure; exit 7') -LogPath $failureLog -Label 'test failure' 6>$null | Out-Null }
    catch { $failureRejected = $_.Exception.Message -match 'exit code 7' }
    if (-not $failureRejected) { throw 'Nonzero child exit code was not propagated as a failure.' }
    if (-not (Test-Path -LiteralPath $failureLog)) { throw 'Failed command diagnostics were not isolated and retained.' }
    if (-not [IO.Path]::GetFullPath($successLog).StartsWith([IO.Path]::GetFullPath($temp), [StringComparison]::OrdinalIgnoreCase)) { throw 'Test output escaped its unique temporary root.' }
    Write-Host 'Developer wrapper tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
