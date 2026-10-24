param(
    [string]$TaskId = 'orchestrator-test'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

$DeveloperPrompt = Join-Path $RepoRoot '.agent/prompts/developer.md'
$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"
$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"
$Result = Join-Path $ResultDir 'development.md'
$CodexOutput = Join-Path $ResultDir 'developer-codex-output.txt'

function Set-DeveloperResultField {
    param([string]$Text, [string]$Name, [string]$Value)
    $pattern = "(?im)^[ \t]*$([regex]::Escape($Name)):[^\r\n]*$"
    $replacement = "$Name`: $Value"
    $updated = [regex]::Replace($Text, $pattern, $replacement)
    if ($updated -eq $Text -and $Text -notmatch $pattern) { throw "Developer result is missing $Name metadata." }
    return $updated
}

if (-not (Test-Path -LiteralPath $DeveloperPrompt -PathType Leaf)) { throw "Developer prompt not found: $DeveloperPrompt" }
if (-not (Test-Path -LiteralPath $Task -PathType Leaf)) { throw "Approved task not found: $Task" }
if ($TaskId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'TaskId contains unsupported path characters.' }

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
$ImplementationRoot = Get-OrCreate-PipelineWorktree -RepoRoot $RepoRoot -TaskId $TaskId -ResultDir $ResultDir
$ImplementationTask = Join-Path $ImplementationRoot ".agent/tasks/approved/$TaskId.md"
Copy-PipelineArtifact -Source $Task -Destination $ImplementationTask | Out-Null
$ImplementationResult = Join-Path $ImplementationRoot ".agent/results/$TaskId/development.md"
$ImplementationResultDir = Split-Path $ImplementationResult -Parent
New-Item -ItemType Directory -Force -Path $ImplementationResultDir | Out-Null

$branch = (git -C $ImplementationRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -notmatch "^automation/$([regex]::Escape($TaskId))-[0-9]{8}-[0-9]{6}$") {
    throw "Implementation worktree branch is not assigned to task '$TaskId': $branch"
}

$Prompt = @"
Act as the Developer Agent defined in .agent/prompts/developer.md.

Implement only the approved task in .agent/tasks/approved/$TaskId.md. Do not modify that task file, pipeline infrastructure, or unrelated files. Do not run builds or tests; the outer Developer wrapper performs verification. Report only implementation changes and any blockers.

Write a structured result to .agent/results/$TaskId/development.md with exactly one each of these fields:
TASK_ID: $TaskId
STATUS: IMPLEMENTED
BRANCH: $branch
COMMIT: PENDING_OUTER_COMMIT
FILES_CHANGED:
- repository/relative/path
TESTS: Deferred to outer wrapper
CLIENT_BUILD: Deferred to outer wrapper
SERVER_BUILD: Deferred to outer wrapper
NOTES: <concise implementation summary or NONE>

List each changed implementation file once under FILES_CHANGED, using repository-relative paths. Do not claim tests/builds passed. Never commit, push, merge, or deploy. Do not access or reveal credentials.
"@

Write-Host 'Starting restricted Developer Agent...'
$Prompt | codex exec `
    -C $ImplementationRoot `
    --model 'gpt-6-luna' `
    --sandbox workspace-write `
    --approve-for-me `
    --output-last-message $CodexOutput `
    -
$codexExit = $LASTEXITCODE
if ($codexExit -ne 0) { Write-Host "DEVELOPER AGENT FAILED: $codexExit"; exit $codexExit }

try {
    if (-not (Test-Path -LiteralPath $ImplementationResult -PathType Leaf)) { throw 'Nested Developer did not create its structured result.' }
    $text = Get-Content -LiteralPath $ImplementationResult -Raw
    foreach ($field in @('TASK_ID','STATUS','BRANCH','COMMIT','TESTS','CLIENT_BUILD','SERVER_BUILD','NOTES')) {
        Get-PipelineStageResultField -Text $text -FieldName $field -ResultName "Developer result for '$TaskId'" | Out-Null
    }
    if ((Get-PipelineStageResultField -Text $text -FieldName 'TASK_ID') -ne $TaskId) { throw 'Developer result task id does not match the approved task.' }
    if ((Get-PipelineStageResultField -Text $text -FieldName 'STATUS') -ne 'IMPLEMENTED') { throw 'Developer result must report STATUS: IMPLEMENTED before wrapper verification.' }
    if ((Get-PipelineStageResultField -Text $text -FieldName 'BRANCH') -ne $branch) { throw 'Developer result branch does not match the assigned implementation branch.' }
    if ((Get-PipelineStageResultField -Text $text -FieldName 'COMMIT') -ne 'PENDING_OUTER_COMMIT') { throw 'Nested Developer must leave commit ownership to the outer wrapper.' }

    $changedFiles = @(Get-PipelineDeveloperChangedFiles -Text $text -ImplementationRoot $ImplementationRoot)
    $initialStatus = @(git -C $ImplementationRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect implementation worktree status.' }
    foreach ($entry in $initialStatus) {
        if ($entry.Length -lt 4) { throw 'Git returned an unrecognized worktree status entry.' }
        if ($entry.Substring(0,1) -notin @(' ', '?')) { throw 'Nested Developer staged changes; outer wrapper will not alter that index.' }
    }
    $expectedAuxiliary = @(".agent/tasks/approved/$TaskId.md", ".agent/results/$TaskId/development.md")
    $actualFiles = @($initialStatus | ForEach-Object { $_.Substring(3).Replace('\','/') } | Where-Object { $_ -notin $expectedAuxiliary } | Sort-Object -Unique)
    $declaredFiles = @($changedFiles | Sort-Object -Unique)
    if (Compare-Object -ReferenceObject $declaredFiles -DifferenceObject $actualFiles) {
        throw 'Developer FILES_CHANGED does not exactly match the worktree changes; refusing to stage or commit.'
    }
    foreach ($relative in $changedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $ImplementationRoot $relative) -PathType Leaf)) { throw "Declared implementation file is missing: $relative" }
    }

    $verificationRoot = Join-Path $ImplementationRoot ".agent/results/$TaskId/developer-verification"
    $packageCache = Join-Path $verificationRoot 'nuget-packages'
    $httpCache = Join-Path $verificationRoot 'nuget-http-cache'
    $dotnetHome = Join-Path $verificationRoot 'dotnet-home'
    $testResults = Join-Path $verificationRoot 'test-results'
    foreach ($directory in @($verificationRoot,$packageCache,$httpCache,$dotnetHome,$testResults)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $cacheSource = Join-Path $env:USERPROFILE '.nuget/packages'
    if (-not (Test-Path -LiteralPath $cacheSource -PathType Container)) {
        throw 'TEST_INFRASTRUCTURE: No existing NuGet package cache is available; network restore is disabled for this Developer verification.'
    }
    $nugetConfig = Join-Path $RepoRoot '.agent/nuget/NuGet.Config'
    if (-not (Test-Path -LiteralPath $nugetConfig -PathType Leaf)) { throw 'Credential-free repository NuGet configuration is missing.' }

    $oldPackages = $env:NUGET_PACKAGES; $oldHttp = $env:NUGET_HTTP_CACHE_PATH; $oldCliHome = $env:DOTNET_CLI_HOME
    try {
        $env:NUGET_PACKAGES = $packageCache
        $env:NUGET_HTTP_CACHE_PATH = $httpCache
        $env:DOTNET_CLI_HOME = $dotnetHome
        $restoreLog = Join-Path $verificationRoot 'restore.log'
        Invoke-PipelineCheckedCommand -Executable 'dotnet' -ArgumentList @('restore',(Join-Path $ImplementationRoot 'DesignMode.AI.sln'),'--configfile',$nugetConfig,'--source',$cacheSource,'--packages',$packageCache,'--verbosity','minimal') -LogPath $restoreLog -Label 'offline solution restore' | Out-Null
        $clientLog = Join-Path $verificationRoot 'client-build.log'
        Invoke-PipelineCheckedCommand -Executable 'dotnet' -ArgumentList @('build',(Join-Path $ImplementationRoot 'DesignMode.AI.Client/DesignMode.AI.Client.csproj'),'--no-restore','--verbosity','minimal') -LogPath $clientLog -Label 'Client build' | Out-Null
        $serverLog = Join-Path $verificationRoot 'server-build.log'
        Invoke-PipelineCheckedCommand -Executable 'dotnet' -ArgumentList @('build',(Join-Path $ImplementationRoot 'DesignMode.AI.Server/DesignMode.AI.Server.csproj'),'--no-restore','--verbosity','minimal') -LogPath $serverLog -Label 'Server build' | Out-Null
        $testLog = Join-Path $verificationRoot 'tests.log'
        Invoke-PipelineCheckedCommand -Executable 'dotnet' -ArgumentList @('test',(Join-Path $ImplementationRoot 'DesignMode.AI.Test/DesignMode.AI.Test.csproj'),'--no-restore','--results-directory',$testResults,'--logger','trx;LogFileName=developer-tests.trx','--verbosity','minimal') -LogPath $testLog -Label 'relevant .NET tests' | Out-Null
    }
    finally {
        $env:NUGET_PACKAGES = $oldPackages
        $env:NUGET_HTTP_CACHE_PATH = $oldHttp
        $env:DOTNET_CLI_HOME = $oldCliHome
    }

    git -C $ImplementationRoot add -- $changedFiles
    if ($LASTEXITCODE -ne 0) { throw 'Unable to stage the declared implementation files.' }
    $postStageStatus = @(git -C $ImplementationRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify staged implementation files.' }
    $stagedFiles = @($postStageStatus | Where-Object { $_.Substring(0,1) -notin @(' ', '?') } | ForEach-Object { $_.Substring(3).Replace('\','/') } | Sort-Object -Unique)
    if (Compare-Object -ReferenceObject $declaredFiles -DifferenceObject $stagedFiles) { throw 'Staged files differ from the Developer declared implementation files.' }
    git -C $ImplementationRoot commit -m "Implement $TaskId"
    if ($LASTEXITCODE -ne 0) { throw 'Feature commit failed.' }
    $commit = (git -C $ImplementationRoot rev-parse --verify 'HEAD^{commit}').Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve actual feature commit SHA.' }

    $text = Set-DeveloperResultField -Text $text -Name 'COMMIT' -Value $commit
    $text = Set-DeveloperResultField -Text $text -Name 'TESTS' -Value "PASS (dotnet test exit 0; see developer-verification/tests.log)"
    $text = Set-DeveloperResultField -Text $text -Name 'CLIENT_BUILD' -Value 'PASS (dotnet build exit 0)'
    $text = Set-DeveloperResultField -Text $text -Name 'SERVER_BUILD' -Value 'PASS (dotnet build exit 0)'
    Set-Content -LiteralPath $ImplementationResult -Value $text -Encoding UTF8
    Sync-PipelineDevelopmentResult -RepoRoot $RepoRoot -TaskId $TaskId -ResultDir $ResultDir -ImplementationRoot $ImplementationRoot | Out-Null
}
catch {
    Write-Host "DEVELOPER INFRASTRUCTURE/VALIDATION FAILURE: $($_.Exception.Message)"
    exit 2
}

Write-Host "Development result created: $Result"
Write-Host "Feature commit: $commit"
Write-Host 'DEVELOPER STAGE COMPLETE'
