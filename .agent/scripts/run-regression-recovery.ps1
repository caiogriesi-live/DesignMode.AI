param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string]$TaskId,
    [Parameter(Mandatory)][string]$DevelopmentResult,
    [Parameter(Mandatory)][string]$QaResult,
    [Parameter(Mandatory)][string]$TriageResult,
    [Parameter(Mandatory)][string]$FeatureRoot,
    [Parameter(Mandatory)][string]$BaselineRoot,
    [Parameter(Mandatory)][string]$ControllerRoot,
    [Parameter(Mandatory)][string]$BrowserEvidenceRoot,
    [Parameter(Mandatory)][string]$FeatureTestLog,
    [Parameter(Mandatory)][string]$BaselineTestLog,
    [Parameter(Mandatory)][string]$BaselineTrx,
    [Parameter(Mandatory)][string]$BaselineManifest,
    [Parameter(Mandatory)][string]$ProtectedEvidenceManifest,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedFeatureCommit,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedBaselineCommit,
    [switch]$PrepareOnly,
    [switch]$ExecuteRegression
)

$ErrorActionPreference = 'Stop'
if ($PrepareOnly -and $ExecuteRegression) { throw 'Choose either -PrepareOnly or -ExecuteRegression, not both.' }

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Resolve-ExistingFile([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-ExistingDirectory([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw "$Label not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
}

function Test-PathWithin([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-RequiredField([string]$Text, [string]$Name, [string]$Label) {
    $matches = [regex]::Matches($Text, "(?im)^\s*$([regex]::Escape($Name))\s*:\s*(.*?)\s*$")
    if ($matches.Count -ne 1) { throw "$Label must contain exactly one '${Name}:' field." }
    $value = $matches[0].Groups[1].Value.Trim()
    if (!$value) { throw "$Label has an empty '${Name}:' field." }
    return $value
}

function Invoke-GitReadOnly([string]$Root, [string[]]$Arguments, [string]$Label) {
    $configPath = Join-Path (Join-Path $RepoRoot '.agent/results') ("git-safe-directory-" + [guid]::NewGuid().ToString('N') + '.config')
    $previousConfig = $env:GIT_CONFIG_GLOBAL
    try {
        Set-Content -LiteralPath $configPath -Value "[safe]`ndirectory = $($Root.Replace('\', '/'))" -Encoding ascii
        $env:GIT_CONFIG_GLOBAL = $configPath
        $output = @(& git -C $Root @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "Git could not $Label (exit $LASTEXITCODE): $($Arguments -join ' ')" }
        return ($output -join "`n").Trim()
    }
    finally {
        if ($null -eq $previousConfig) { Remove-Item Env:GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue }
        else { $env:GIT_CONFIG_GLOBAL = $previousConfig }
        if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
    }
}

function Get-TestSummary([string]$Path, [string]$Label) {
    $text = Get-Content -LiteralPath $Path -Raw
    $summary = [regex]::Match($text, 'Failed!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if (!$summary.Success) { throw "$Label does not contain a recognized .NET test summary." }
    return [pscustomobject]@{
        Failed = [int]$summary.Groups[1].Value
        Passed = [int]$summary.Groups[2].Value
        Skipped = [int]$summary.Groups[3].Value
        Total = [int]$summary.Groups[4].Value
    }
}

function Get-TestFailures([string]$Path, [string]$Kind) {
    if ($Kind -eq 'trx') {
        try { [xml]$xml = Get-Content -LiteralPath $Path -Raw }
        catch { throw "Baseline TRX is not valid XML: $Path" }
        $namespace = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $namespace.AddNamespace('t', $xml.DocumentElement.NamespaceURI)
        return @($xml.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $namespace) |
            ForEach-Object { $_.GetAttribute('testName') } | Sort-Object -Unique)
    }
    $text = Get-Content -LiteralPath $Path -Raw
    return @([regex]::Matches($text, '(?m)^\s*Failed\s+(.+?)\s+\[[^\]]+\]') |
        ForEach-Object { $_.Groups[1].Value.Trim() } | Sort-Object -Unique)
}

$devPath = Resolve-ExistingFile $DevelopmentResult 'Development result'
$qaPath = Resolve-ExistingFile $QaResult 'QA result'
$triagePath = Resolve-ExistingFile $TriageResult 'Triage result'
$featurePath = Resolve-ExistingDirectory $FeatureRoot 'Feature worktree'
$baselinePath = Resolve-ExistingDirectory $BaselineRoot 'Baseline worktree'
$controllerPath = Resolve-ExistingDirectory $ControllerRoot 'Controller worktree'
$browserPath = Resolve-ExistingDirectory $BrowserEvidenceRoot 'Browser evidence directory'
$featureLogPath = Resolve-ExistingFile $FeatureTestLog 'Feature test log'
$baselineLogPath = Resolve-ExistingFile $BaselineTestLog 'Baseline test log'
$trxPath = Resolve-ExistingFile $BaselineTrx 'Baseline TRX'
$baselineManifestPath = Resolve-ExistingFile $BaselineManifest 'Baseline manifest'
$protectedManifestPath = Resolve-ExistingFile $ProtectedEvidenceManifest 'Protected evidence manifest'

$outputPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$allowedOutputRoot = Join-Path $RepoRoot '.agent/results'
if (!(Test-PathWithin $outputPath $allowedOutputRoot)) { throw "OutputDirectory must be a new child of '$allowedOutputRoot'." }
if (Test-Path -LiteralPath $outputPath) { throw "OutputDirectory already exists; refusing to overwrite it: $outputPath" }
foreach ($protectedRoot in @($featurePath, $baselinePath, $controllerPath, $browserPath)) {
    if ((Test-PathWithin $outputPath $protectedRoot) -or (Test-PathWithin $protectedRoot $outputPath)) {
        throw "OutputDirectory overlaps protected input/worktree '$protectedRoot'."
    }
}

$developmentText = Get-Content -LiteralPath $devPath -Raw
$qaText = Get-Content -LiteralPath $qaPath -Raw
$triageText = Get-Content -LiteralPath $triagePath -Raw
$developmentBranch = Get-RequiredField $developmentText 'BRANCH' 'Development result'
$developmentCommit = Get-RequiredField $developmentText 'COMMIT' 'Development result'
$qaFeatureHead = Get-RequiredField $qaText 'FEATURE_HEAD' 'QA result'
$qaFeatureBranch = Get-RequiredField $qaText 'FEATURE_BRANCH' 'QA result'
$triageFeatureHead = Get-RequiredField $triageText 'FEATURE_HEAD' 'Triage result'
$triageBaseCommit = Get-RequiredField $triageText 'BASE_COMMIT' 'Triage result'
$qaStatus = Get-RequiredField $qaText 'STATUS' 'QA result'
$qaDesktop = Get-RequiredField $qaText 'DESKTOP_PLAYWRIGHT' 'QA result'
$qaMobile = Get-RequiredField $qaText 'MOBILE_WEBKIT' 'QA result'
$triageQaStatus = Get-RequiredField $triageText 'FEATURE_QA_STATUS' 'Triage result'
$triageAction = Get-RequiredField $triageText 'NEXT_ACTION' 'Triage result'

$featureHead = Invoke-GitReadOnly $featurePath @('rev-parse', 'HEAD^{commit}') 'resolve feature HEAD'
$featureBranch = Invoke-GitReadOnly $featurePath @('branch', '--show-current') 'resolve feature branch'
$baselineHead = Invoke-GitReadOnly $baselinePath @('rev-parse', 'HEAD^{commit}') 'resolve baseline HEAD'
if ($featureHead -ne $ExpectedFeatureCommit -or $qaFeatureHead -ne $featureHead -or $triageFeatureHead -ne $featureHead) {
    throw 'Feature commit does not match the approved feature commit across Git and QA/Triage evidence.'
}
if ($baselineHead -ne $ExpectedBaselineCommit -or $triageBaseCommit -ne $baselineHead) {
    throw 'Baseline commit does not match the exact feature base across Git and Triage evidence.'
}
if ($developmentBranch -ne $featureBranch -or $qaFeatureBranch -ne $featureBranch) { throw 'Development/QA branch does not match the feature worktree branch.' }
if ($developmentCommit -notmatch "(?i)^$([regex]::Escape($featureHead.Substring(0, 7)))(?:\b|\s)") { throw 'Development commit does not identify the verified feature commit.' }
$mergeBase = Invoke-GitReadOnly $featurePath @('merge-base', $baselineHead, $featureHead) 'resolve feature merge base'
if ($mergeBase -ne $baselineHead) { throw 'Baseline worktree HEAD is not the feature commit merge base.' }
$changedFiles = @((Invoke-GitReadOnly $featurePath @('diff', '--name-only', $baselineHead, $featureHead) 'inspect feature diff') -split "`n" | Where-Object { $_ })
if (!$changedFiles.Count) { throw 'Feature commit has no changed files to review.' }

if ($qaStatus -notmatch '(?i)^PASS\b' -or $qaDesktop -notmatch '(?i)^PASS\b' -or $qaMobile -notmatch '(?i)^PASS\b') {
    throw 'Recovered QA evidence must show PASS for overall acceptance, Chromium, and WebKit.'
}
if ($triageQaStatus -notmatch '(?i)^PASS\b' -or $triageAction -notmatch '^(REGRESSION|HUMAN_REQUIRED)$') {
    throw 'Recovered Triage must show passing QA and route to REGRESSION or HUMAN_REQUIRED.'
}

$browserSummaryPath = Join-Path $browserPath 'browser-run-summary-attempt5.txt'
if (!(Test-Path -LiteralPath $browserSummaryPath -PathType Leaf)) { throw 'Attempt 5 browser summary is missing.' }
$browserResults = @{}
foreach ($browser in @('chromium', 'webkit')) {
    $resultPath = Resolve-ExistingFile (Join-Path $browserPath "$browser.result-attempt5.json") "$browser attempt 5 result"
    $exitPath = Resolve-ExistingFile (Join-Path $browserPath "$browser.exit-code-attempt5.txt") "$browser attempt 5 exit code"
    $stdoutPath = Resolve-ExistingFile (Join-Path $browserPath "$browser.stdout-attempt5.log") "$browser attempt 5 stdout"
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $exitCode = (Get-Content -LiteralPath $exitPath -Raw).Trim()
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $artifactRoot = Join-Path $browserPath "browser-artifacts-infra06f-attempt5\$browser"
    $traces = if (Test-Path -LiteralPath $artifactRoot) { @(Get-ChildItem -LiteralPath $artifactRoot -Filter 'trace.zip' -File -Recurse) } else { @() }
    $screenshots = if (Test-Path -LiteralPath $artifactRoot) { @(Get-ChildItem -LiteralPath $artifactRoot -Filter 'test-finished-1.png' -File -Recurse) } else { @() }
    if ($exitCode -ne '0' -or $result.status -ne 0 -or $result.timedOut -or $stdout -notmatch '1 passed' -or !$traces.Count -or !$screenshots.Count) {
        throw "$browser attempt 5 evidence is incomplete or unsuccessful."
    }
    $browserResults[$browser] = [pscustomobject]@{ ExitCode = 0; ResultStatus = $result.status; PassedTests = 1; Traces = $traces.Count; Screenshots = $screenshots.Count }
}

$baselineBuildEvidence = Get-Content -LiteralPath $baselineManifestPath -Raw | ConvertFrom-Json
if ($baselineBuildEvidence.taskId -ne $TaskId -or
    $baselineBuildEvidence.featureBranch -ne $featureBranch -or
    $baselineBuildEvidence.baseCommit -ne $baselineHead -or
    $baselineBuildEvidence.featureHead -ne $featureHead) {
    throw 'Baseline evidence manifest does not identify the verified feature and base commits.'
}
if (@($baselineBuildEvidence.artifacts).Count -ne 3 -or
    @($baselineBuildEvidence.artifacts | Select-Object -ExpandProperty name -Unique).Count -ne 3) {
    throw 'Baseline evidence manifest must contain three unique artifacts.'
}
$baselineCounts = Get-TestSummary $baselineLogPath 'Baseline test log'
$featureCounts = Get-TestSummary $featureLogPath 'Feature test log'
if (($baselineCounts | ConvertTo-Json -Compress) -cne ($featureCounts | ConvertTo-Json -Compress)) {
    throw 'Feature and baseline .NET test totals do not match.'
}
$trxXml = [xml](Get-Content -LiteralPath $trxPath -Raw)
$trxNamespace = New-Object System.Xml.XmlNamespaceManager($trxXml.NameTable)
$trxNamespace.AddNamespace('t', $trxXml.DocumentElement.NamespaceURI)
$trxCounters = $trxXml.SelectSingleNode('//t:ResultSummary/t:Counters', $trxNamespace)
if (!$trxCounters) { throw 'Baseline TRX has no test result counters.' }
$trxCounts = [pscustomobject]@{
    Failed = [int]$trxCounters.GetAttribute('failed')
    Passed = [int]$trxCounters.GetAttribute('passed')
    Skipped = [int]$trxCounters.GetAttribute('total') - [int]$trxCounters.GetAttribute('failed') - [int]$trxCounters.GetAttribute('passed')
    Total = [int]$trxCounters.GetAttribute('total')
}
if (($baselineCounts | ConvertTo-Json -Compress) -cne ($trxCounts | ConvertTo-Json -Compress)) {
    throw 'Baseline test log totals do not match the baseline TRX counters.'
}
$expectedTestExit = if ($baselineCounts.Failed -gt 0) { 1 } else { 0 }
if ([int]$baselineBuildEvidence.baselineTestExitCode -ne $expectedTestExit) { throw 'Baseline evidence manifest exit code does not match the recorded test failures.' }
foreach ($artifact in $baselineBuildEvidence.artifacts) {
    if ($artifact.name -notin @('restore.log', 'tests.log', 'baseline.trx') -or $artifact.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Baseline evidence manifest has an unsupported artifact or invalid SHA-256.'
    }
    $artifactPath = Join-Path (Join-Path $featurePath ".agent/results/$TaskId/baseline") $artifact.name
    if (!(Test-Path -LiteralPath $artifactPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash -ne $artifact.sha256) {
        throw "Baseline evidence artifact is missing or has a hash mismatch: $artifactPath"
    }
}
$baselineFailures = @(Get-TestFailures $trxPath 'trx')
$featureFailures = @(Get-TestFailures $featureLogPath 'log')
if (($baselineFailures -join "`n") -cne ($featureFailures -join "`n") -or $baselineFailures.Count -ne $baselineCounts.Failed) {
    throw 'Feature .NET failing test identities do not match the baseline TRX.'
}

$protected = Get-Content -LiteralPath $protectedManifestPath -Raw | ConvertFrom-Json
if ($protected.taskId -ne $TaskId -or @($protected.files).Count -ne 9) { throw 'Protected evidence manifest must identify this task and exactly nine files.' }
$protectedChecks = @()
foreach ($entry in $protected.files) {
    if ($entry.relativePath -match '(^|[\\/])\.\.([\\/]|$)' -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Protected evidence manifest contains an unsafe path or invalid SHA-256.'
    }
    $copies = @()
    foreach ($root in @($controllerPath, $featurePath)) {
        $candidate = Join-Path $root $entry.relativePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
            if ($actual -ne $entry.sha256) { throw "Protected evidence hash mismatch: $candidate" }
            $copies += $candidate
        }
    }
    if (!$copies.Count) { throw "Protected evidence file is missing from controller and feature worktrees: $($entry.relativePath)" }
    $protectedChecks += [pscustomobject]@{ RelativePath = $entry.relativePath; SHA256 = $entry.sha256; VerifiedCopies = $copies.Count }
}

$validation = [pscustomobject]@{
    TaskId = $TaskId
    FeatureCommit = $featureHead
    FeatureBranch = $featureBranch
    BaselineCommit = $baselineHead
    ChangedFiles = $changedFiles
    BrowserAttempt5 = $browserResults
    FeatureDotNet = $featureCounts
    BaselineDotNet = $baselineCounts
    BaselineFailureIdentities = $baselineFailures
    ProtectedEvidence = $protectedChecks
    InputsValidatedUtc = [DateTime]::UtcNow.ToString('o')
}

if (!$PrepareOnly -and !$ExecuteRegression) {
    Write-Output ($validation | ConvertTo-Json -Depth 6)
    Write-Output 'Validation only; no output files were written and Regression was not executed.'
    exit 0
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
$snapshotRoot = Join-Path $outputPath 'inputs'
New-Item -ItemType Directory -Path $snapshotRoot | Out-Null
Copy-Item -LiteralPath $devPath -Destination (Join-Path $snapshotRoot 'development.md')
Copy-Item -LiteralPath $qaPath -Destination (Join-Path $snapshotRoot 'qa.md')
Copy-Item -LiteralPath $triagePath -Destination (Join-Path $snapshotRoot 'triage.md')
Copy-Item -LiteralPath $featureLogPath -Destination (Join-Path $snapshotRoot 'feature-tests.log')
Copy-Item -LiteralPath $baselineLogPath -Destination (Join-Path $snapshotRoot 'baseline-tests.log')
Copy-Item -LiteralPath $trxPath -Destination (Join-Path $snapshotRoot 'baseline.trx')
Copy-Item -LiteralPath $baselineManifestPath -Destination (Join-Path $snapshotRoot 'baseline-manifest.json')
Copy-Item -LiteralPath $protectedManifestPath -Destination (Join-Path $snapshotRoot 'protected-evidence-manifest.json')
Copy-Item -LiteralPath $browserSummaryPath -Destination (Join-Path $snapshotRoot 'browser-run-summary-attempt5.txt')
foreach ($browser in @('chromium', 'webkit')) {
    Copy-Item -LiteralPath (Join-Path $browserPath "$browser.result-attempt5.json") -Destination $snapshotRoot
    Copy-Item -LiteralPath (Join-Path $browserPath "$browser.exit-code-attempt5.txt") -Destination $snapshotRoot
    Copy-Item -LiteralPath (Join-Path $browserPath "$browser.stdout-attempt5.log") -Destination $snapshotRoot
    $sourceArtifacts = Join-Path $browserPath "browser-artifacts-infra06f-attempt5\$browser"
    Copy-Item -LiteralPath $sourceArtifacts -Destination (Join-Path $snapshotRoot "browser-artifacts\$browser") -Recurse
}
$taskFile = Join-Path $featurePath ".agent/tasks/approved/$TaskId.md"
if (Test-Path -LiteralPath $taskFile -PathType Leaf) { Copy-Item -LiteralPath $taskFile -Destination (Join-Path $snapshotRoot 'task.md') }
$diffText = Invoke-GitReadOnly $featurePath @('diff', '--no-ext-diff', '--unified=80', $baselineHead, $featureHead) 'read feature diff'
Set-Content -LiteralPath (Join-Path $snapshotRoot 'feature.diff') -Value $diffText -Encoding utf8
$validation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'input-validation.json') -Encoding utf8

if ($PrepareOnly) {
    Write-Output "Prepared isolated regression evidence at: $outputPath"
    exit 0
}

$promptPath = Join-Path $outputPath 'regression-prompt.md'
$resultPath = Join-Path $outputPath 'regression.md'
$stdoutPath = Join-Path $outputPath 'codex.stdout.log'
$stderrPath = Join-Path $outputPath 'codex.stderr.log'
$prompt = @"
Act as the Regression / Release Candidate Agent. Read the repository instructions at .agent/prompts/regression.md and the immutable evidence snapshot in '$snapshotRoot'. Review the actual implementation diff in inputs/feature.diff and task/evidence. Do not build, test, or modify application or repository files. Write your complete structured regression assessment as your final response only; the isolated wrapper captures it at '$resultPath'.

All inputs were independently validated before this invocation. Feature commit: $featureHead. Baseline commit: $baselineHead. Triage NEXT_ACTION: $triageAction. Treat the explicitly invoked recovery wrapper as authorization to perform this isolated Regression review; do not claim the task complete unless all required checks pass. Do not write files or run commands that mutate any worktree.
"@
Set-Content -LiteralPath $promptPath -Value $prompt -Encoding utf8
$prompt | & codex exec -C $RepoRoot --sandbox read-only --model 'gpt-6-luna' - 1> $stdoutPath 2> $stderrPath
$codexExit = $LASTEXITCODE
$codexOutput = Get-Content -LiteralPath $stdoutPath -Raw
Set-Content -LiteralPath $resultPath -Value $codexOutput -Encoding utf8
Set-Content -LiteralPath (Join-Path $outputPath 'codex.exit-code.txt') -Value $codexExit -Encoding ascii
if ($codexExit -ne 0) { throw "Regression agent failed with exit code $codexExit; evidence preserved at '$outputPath'." }
if ($codexOutput -notmatch '(?im)^STATUS:\s*(READY_FOR_HUMAN_REVIEW|REPAIR_REQUIRED|HUMAN_REQUIRED)\s*$') {
    throw "Regression agent output lacks a recognized STATUS field; evidence preserved at '$outputPath'."
}
if ((Invoke-GitReadOnly $featurePath @('rev-parse', 'HEAD^{commit}') 'recheck feature HEAD') -ne $featureHead -or
    (Invoke-GitReadOnly $baselinePath @('rev-parse', 'HEAD^{commit}') 'recheck baseline HEAD') -ne $baselineHead) {
    throw "Feature or baseline HEAD changed during regression; evidence preserved at '$outputPath'."
}
foreach ($entry in $protected.files) {
    foreach ($root in @($controllerPath, $featurePath)) {
        $candidate = Join-Path $root $entry.relativePath
        if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
            (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Protected evidence changed during regression: $candidate. Evidence preserved at '$outputPath'."
        }
    }
}
Write-Output "Isolated regression result: $resultPath"
