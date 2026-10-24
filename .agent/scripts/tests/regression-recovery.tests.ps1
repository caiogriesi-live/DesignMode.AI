$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot '..\run-regression-recovery.ps1'
$runner = (Resolve-Path $runner).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$testRoot = Join-Path $repo ('.agent/results/infra08-regression-tests-' + [guid]::NewGuid().ToString('N'))
$feature = Join-Path $testRoot 'feature'
$baseline = Join-Path $testRoot 'baseline'
$controller = Join-Path $testRoot 'controller'
$inputs = Join-Path $testRoot 'inputs'
New-Item -ItemType Directory -Force -Path $testRoot, $controller, $inputs | Out-Null

function Invoke-GitFixture([string]$Root, [string[]]$GitArgs) {
    $output = @(& git -C $Root @GitArgs)
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git command failed: git -C $Root $($GitArgs -join ' ')" }
    return ($output -join "`n").Trim()
}

function Assert-Throws([scriptblock]$Action, [string]$Case) {
    try { & $Action; throw "Expected recovery validation to reject $Case." }
    catch { if ($_.Exception.Message -eq "Expected recovery validation to reject $Case.") { throw } }
}

function New-Fixture {
    New-Item -ItemType Directory -Force -Path $feature, $baseline | Out-Null
    $env:GIT_AUTHOR_DATE = '2020-01-01T00:00:00Z'
    $env:GIT_COMMITTER_DATE = '2020-01-01T00:00:00Z'
    foreach ($root in @($feature, $baseline)) {
        & git init -b feature/pipeline-validation $root | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to initialize isolated Git fixture: $root" }
        Invoke-GitFixture $root @('config', 'user.name', 'INFRA-08 Test') | Out-Null
        Invoke-GitFixture $root @('config', 'user.email', 'infra08@example.invalid') | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'tracked.txt') -Value 'baseline' -NoNewline
        Invoke-GitFixture $root @('add', 'tracked.txt') | Out-Null
        Invoke-GitFixture $root @('commit', '-m', 'test baseline') | Out-Null
    }
    Remove-Item Env:GIT_AUTHOR_DATE
    Remove-Item Env:GIT_COMMITTER_DATE
    $base = Invoke-GitFixture $feature @('rev-parse', 'HEAD')
    if ((Invoke-GitFixture $baseline @('rev-parse', 'HEAD')) -ne $base) { throw 'Baseline fixture commit identity mismatch.' }
    Set-Content -LiteralPath (Join-Path $feature 'tracked.txt') -Value 'feature' -NoNewline
    Invoke-GitFixture $feature @('add', 'tracked.txt') | Out-Null
    Invoke-GitFixture $feature @('commit', '-m', 'test feature') | Out-Null
    $head = Invoke-GitFixture $feature @('rev-parse', 'HEAD')
    $branch = Invoke-GitFixture $feature @('branch', '--show-current')

    Set-Content -LiteralPath (Join-Path $inputs 'development.md') -Value "STATUS: IMPLEMENTED`nBRANCH: $branch`nCOMMIT: $($head.Substring(0, 7)) (test)"
    Set-Content -LiteralPath (Join-Path $inputs 'qa.md') -Value @"
STATUS: PASS (recovered)
FEATURE_BRANCH: $branch
FEATURE_HEAD: $head
BASE_COMMIT: $base
DESKTOP_PLAYWRIGHT: PASS
MOBILE_WEBKIT: PASS
"@
    Set-Content -LiteralPath (Join-Path $inputs 'triage.md') -Value @"
STATUS: BLOCKED_PENDING_FORMAL_REGRESSION
FEATURE_HEAD: $head
BASE_COMMIT: $base
FEATURE_QA_STATUS: PASS
NEXT_ACTION: HUMAN_REQUIRED
"@
    $browser = Join-Path $inputs 'browser'
    New-Item -ItemType Directory -Force -Path (Join-Path $browser 'browser-artifacts-infra06f-attempt5/chromium'), (Join-Path $browser 'browser-artifacts-infra06f-attempt5/webkit') | Out-Null
    Set-Content -LiteralPath (Join-Path $browser 'browser-run-summary-attempt5.txt') -Value 'chromium_exit=0; webkit_exit=0'
    foreach ($name in @('chromium', 'webkit')) {
        Set-Content -LiteralPath (Join-Path $browser "$name.result-attempt5.json") -Value '{"status":0,"timedOut":false,"stdout":"1 passed"}'
        Set-Content -LiteralPath (Join-Path $browser "$name.exit-code-attempt5.txt") -Value '0'
        Set-Content -LiteralPath (Join-Path $browser "$name.stdout-attempt5.log") -Value '1 passed (test)'
        Set-Content -LiteralPath (Join-Path $browser "browser-artifacts-infra06f-attempt5/$name/trace.zip") -Value 'trace'
        Set-Content -LiteralPath (Join-Path $browser "browser-artifacts-infra06f-attempt5/$name/test-finished-1.png") -Value 'image'
    }

    $failure = 'DesignMode.AI.Test.SampleTests.ExistingFailure'
    $testSummary = "Failed! - Failed: 1, Passed: 2, Skipped: 0, Total: 3`nFailed $failure [2 ms]"
    $featureLog = Join-Path $inputs 'feature-tests.log'
    $baselineLog = Join-Path $inputs 'baseline-tests.log'
    Set-Content -LiteralPath $featureLog -Value $testSummary
    Set-Content -LiteralPath $baselineLog -Value $testSummary
    $trxPath = Join-Path $inputs 'baseline.trx'
    Set-Content -LiteralPath $trxPath -Value "<?xml version=`"1.0`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results><UnitTestResult testName=`"$failure`" outcome=`"Failed`" /></Results><ResultSummary><Counters total=`"3`" passed=`"2`" failed=`"1`" /></ResultSummary></TestRun>"
    $baselineEvidence = Join-Path $feature '.agent/results/pipeline-validation-work-01/baseline'
    New-Item -ItemType Directory -Force -Path $baselineEvidence | Out-Null
    Copy-Item -LiteralPath $trxPath -Destination (Join-Path $baselineEvidence 'baseline.trx')
    Copy-Item -LiteralPath $baselineLog -Destination (Join-Path $baselineEvidence 'tests.log')
    Set-Content -LiteralPath (Join-Path $baselineEvidence 'restore.log') -Value 'restore completed'
    $artifacts = @()
    foreach ($name in @('restore.log', 'tests.log', 'baseline.trx')) {
        $artifactPath = Join-Path $baselineEvidence $name
        $artifacts += [pscustomobject]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash }
    }
    $manifestPath = Join-Path $inputs 'baseline-manifest.json'
    [pscustomobject]@{ taskId = 'pipeline-validation-work-01'; featureBranch = $branch; baseCommit = $base; featureHead = $head; baselineTestExitCode = 1; artifacts = $artifacts } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath

    $protectedFiles = @()
    for ($index = 1; $index -le 9; $index++) {
        $relative = "evidence-$index.txt"
        $path = Join-Path $controller $relative
        Set-Content -LiteralPath $path -Value "protected-$index" -NoNewline
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $protectedFiles += [pscustomobject]@{ relativePath = $relative; sha256 = $hash }
    }
    $protectedPath = Join-Path $inputs 'protected-evidence-manifest.json'
    [pscustomobject]@{ taskId = 'pipeline-validation-work-01'; files = $protectedFiles } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $protectedPath

    return [pscustomobject]@{
        FeatureHead = $head; BaselineHead = $base; Branch = $branch; Browser = $browser
        FeatureLog = $featureLog; BaselineLog = $baselineLog; Trx = $trxPath; Manifest = $manifestPath
        ProtectedManifest = $protectedPath; Development = (Join-Path $inputs 'development.md')
        Qa = (Join-Path $inputs 'qa.md'); Triage = (Join-Path $inputs 'triage.md')
    }
}

function Invoke-Validation([pscustomobject]$Fixture, [string]$Destination, [switch]$Prepare) {
    $arguments = @{
        TaskId = 'pipeline-validation-work-01'
        DevelopmentResult = $Fixture.Development; QaResult = $Fixture.Qa; TriageResult = $Fixture.Triage
        FeatureRoot = $feature; BaselineRoot = $baseline; ControllerRoot = $controller
        BrowserEvidenceRoot = $Fixture.Browser; FeatureTestLog = $Fixture.FeatureLog
        BaselineTestLog = $Fixture.BaselineLog; BaselineTrx = $Fixture.Trx
        BaselineManifest = $Fixture.Manifest; ProtectedEvidenceManifest = $Fixture.ProtectedManifest
        OutputDirectory = $Destination
        ExpectedFeatureCommit = $Fixture.FeatureHead; ExpectedBaselineCommit = $Fixture.BaselineHead
    }
    if ($Prepare) { & $runner @arguments -PrepareOnly } else { & $runner @arguments }
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Runner failed with exit code $LASTEXITCODE." }
}

try {
    $fixture = New-Fixture
    $output = Join-Path $testRoot 'isolated-output'
    $beforeProtectedHashes = @(Get-ChildItem -LiteralPath $controller -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })

    Invoke-Validation $fixture $output
    if (Test-Path -LiteralPath $output) { throw 'Validation-only mode wrote to its output directory.' }

    Invoke-Validation $fixture $output -Prepare
    if (!(Test-Path -LiteralPath (Join-Path $output 'input-validation.json'))) { throw 'Prepare-only did not write the isolated validation manifest.' }
    if (!(Test-Path -LiteralPath (Join-Path $output 'inputs/feature.diff'))) { throw 'Prepare-only did not snapshot the feature diff.' }
    $afterProtectedHashes = @(Get-ChildItem -LiteralPath $controller -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    if (($beforeProtectedHashes -join ',') -cne ($afterProtectedHashes -join ',')) { throw 'Prepare-only modified protected input evidence.' }

    $developmentOriginal = Get-Content -LiteralPath $fixture.Development -Raw
    Add-Content -LiteralPath $fixture.Development -Value "`nBRANCH: duplicate/unsafe"
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-schema') } 'duplicate Development schema field'
    Set-Content -LiteralPath $fixture.Development -Value $developmentOriginal

    $qaOriginal = Get-Content -LiteralPath $fixture.Qa -Raw
    Set-Content -LiteralPath $fixture.Qa -Value $qaOriginal.Replace($fixture.FeatureHead, ('0' * 40))
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-feature') } 'mismatched feature commit'
    Set-Content -LiteralPath $fixture.Qa -Value $qaOriginal

    $triageOriginal = Get-Content -LiteralPath $fixture.Triage -Raw
    Set-Content -LiteralPath $fixture.Triage -Value $triageOriginal.Replace($fixture.BaselineHead, ('1' * 40))
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-baseline') } 'mismatched baseline commit'
    Set-Content -LiteralPath $fixture.Triage -Value $triageOriginal

    $browserExit = Join-Path $fixture.Browser 'chromium.exit-code-attempt5.txt'
    Set-Content -LiteralPath $browserExit -Value '1'
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-browser') } 'failed browser evidence'
    Set-Content -LiteralPath $browserExit -Value '0'

    $featureLogOriginal = Get-Content -LiteralPath $fixture.FeatureLog -Raw
    Set-Content -LiteralPath $fixture.FeatureLog -Value $featureLogOriginal.Replace('Failed: 1', 'Failed: 2')
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-test-counts') } 'baseline-mismatched test totals'
    Set-Content -LiteralPath $fixture.FeatureLog -Value $featureLogOriginal

    $baselineArtifact = Join-Path $feature '.agent/results/pipeline-validation-work-01/baseline/tests.log'
    $baselineArtifactBytes = [IO.File]::ReadAllBytes($baselineArtifact)
    Set-Content -LiteralPath $baselineArtifact -Value 'tampered baseline evidence'
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-baseline-artifact') } 'baseline artifact hash mismatch'
    [IO.File]::WriteAllBytes($baselineArtifact, $baselineArtifactBytes)

    $protectedOriginal = Get-Content -LiteralPath $fixture.ProtectedManifest -Raw
    $protectedObject = $protectedOriginal | ConvertFrom-Json
    $protectedObject.files[0].sha256 = 'F' * 64
    $protectedObject | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixture.ProtectedManifest
    Assert-Throws { Invoke-Validation $fixture (Join-Path $testRoot 'bad-hash') } 'protected evidence hash mismatch'
    Set-Content -LiteralPath $fixture.ProtectedManifest -Value $protectedOriginal

    Assert-Throws { Invoke-Validation $fixture (Join-Path $feature '.agent/results/overlap') } 'output overlapping a protected worktree'
    if (Test-Path -LiteralPath (Join-Path $testRoot 'bad-feature')) { throw 'Failed validation created output files.' }
    Write-Host 'Regression recovery tests passed (10 scenarios).'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
