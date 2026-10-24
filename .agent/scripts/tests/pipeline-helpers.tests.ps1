$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\pipeline-helpers.ps1')

function Assert-StageResultFieldThrows {
    param([string]$Text, [string]$FieldName, [string]$ExpectedMessage)
    try {
        Get-PipelineStageResultField -Text $Text -FieldName $FieldName -ResultName 'Test result' | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw "Unexpected parser error: $($_.Exception.Message)" }
        return
    }
    throw "Expected parser to reject $FieldName metadata."
}

$expectedBranch = 'feature/marketing-dashboard-work-03-20261007-101757'
$repairAction = Get-PipelineTriageAction -Text "STATUS: TRIAGED`nNEXT_ACTION: REPAIR"
$regressionAction = Get-PipelineTriageAction -Text "STATUS: TRIAGED`nNEXT_ACTION: REGRESSION"
$humanAction = Get-PipelineTriageAction -Text "STATUS: TRIAGED`nNEXT_ACTION: HUMAN_REQUIRED"
if ($repairAction -ne 'REPAIR') { throw 'REPAIR transition was not parsed.' }
if ($regressionAction -ne 'REGRESSION') { throw 'REGRESSION transition was not parsed.' }
if ($humanAction -ne 'HUMAN_REQUIRED') { throw 'HUMAN_REQUIRED transition was not parsed.' }
$passingQa = @'
STATUS: PASS
ACCEPTANCE: PASS
DESKTOP_PLAYWRIGHT: PASS
MOBILE_WEBKIT: PASS
AUTHORIZATION_TESTS: PASS
'@
$passingEvidence = "ACCEPTANCE_APPLICABLE: True`nACCEPTANCE_EXIT_CODE: 0"
foreach ($taskId in @('WORK-01', 'WORK-04', 'WORK-09')) {
    $passingTriage = "FEATURE_QA_STATUS: PASS_WITH_BASELINE_FAILURES`nBRANCH_REGRESSIONS: 0`nREPAIR_REQUIRED: NO`nNEXT_ACTION: REGRESSION"
    Assert-PipelineRegressionEligible -TriageText $passingTriage -QaText $passingQa `
        -EvidenceSummaryText $passingEvidence -TaskText "${taskId}: server authorization" | Out-Null

    $blockedCases = @(
        @{ Triage = $passingTriage.Replace('BRANCH_REGRESSIONS: 0', 'BRANCH_REGRESSIONS: 1'); Qa = $passingQa; Evidence = $passingEvidence },
        @{ Triage = $passingTriage; Qa = $passingQa.Replace('MOBILE_WEBKIT: PASS', 'MOBILE_WEBKIT: FAIL'); Evidence = $passingEvidence },
        @{ Triage = $passingTriage; Qa = $passingQa.Replace('AUTHORIZATION_TESTS: PASS', 'AUTHORIZATION_TESTS: FAIL'); Evidence = $passingEvidence },
        @{ Triage = $passingTriage; Qa = $passingQa; Evidence = $passingEvidence.Replace('ACCEPTANCE_EXIT_CODE: 0', 'ACCEPTANCE_EXIT_CODE: 1') }
    )
    foreach ($case in $blockedCases) {
        try {
            Assert-PipelineRegressionEligible -TriageText $case.Triage -QaText $case.Qa `
                -EvidenceSummaryText $case.Evidence -TaskText "${taskId}: server authorization" | Out-Null
            throw "Unsafe Regression routing was accepted for $taskId."
        }
        catch {
            if ($_.Exception.Message -eq "Unsafe Regression routing was accepted for $taskId.") { throw }
        }
    }
}
$notApplicableTriage = "FEATURE_QA_STATUS: PASS`nBRANCH_REGRESSIONS: 0`nREPAIR_REQUIRED: NO`nNEXT_ACTION: REGRESSION"
$notApplicableQa = "STATUS: PASS`nACCEPTANCE: NOT APPLICABLE`nAUTHORIZATION_TESTS: NOT APPLICABLE"
Assert-PipelineRegressionEligible -TriageText $notApplicableTriage -QaText $notApplicableQa `
    -EvidenceSummaryText "ACCEPTANCE_APPLICABLE: False`nACCEPTANCE_EXIT_CODE: 0" -TaskText 'WORK-02: reporting service' | Out-Null
$crlfResult = "STATUS: IMPLEMENTED`r`n  BRANCH : `t$expectedBranch `t`r`nCOMMIT: 9226736"
if ((Get-PipelineStageResultField -Text $crlfResult -FieldName 'BRANCH') -ne $expectedBranch) { throw 'CRLF or optional whitespace parsing failed.' }
$lfResult = "STATUS: IMPLEMENTED`nBRANCH: $expectedBranch`nCOMMIT: 9226736"
if ((Get-PipelineStageResultField -Text $lfResult -FieldName 'BRANCH') -ne $expectedBranch) { throw 'LF parsing failed.' }
$bomResult = ([char]0xFEFF).ToString() + $lfResult
if ((Get-PipelineStageResultField -Text $bomResult -FieldName 'BRANCH') -ne $expectedBranch) { throw 'UTF-8 BOM parsing failed.' }
Assert-StageResultFieldThrows -Text "STATUS: PASS`nBRANCH feature/foo" -FieldName 'BRANCH' -ExpectedMessage 'malformed BRANCH'
Assert-StageResultFieldThrows -Text "STATUS: PASS`nCOMMIT: abc123" -FieldName 'BRANCH' -ExpectedMessage 'missing BRANCH'
Assert-StageResultFieldThrows -Text "STATUS: PASS`nBRANCH:   `nCOMMIT: abc123" -FieldName 'BRANCH' -ExpectedMessage 'empty BRANCH'
Assert-StageResultFieldThrows -Text "BRANCH: feature/one`nBRANCH: feature/two" -FieldName 'BRANCH' -ExpectedMessage 'duplicate BRANCH'

$testRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$temp = Join-Path $testRoot (".pipeline-helper-test-" + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $temp 'repo'
$results = Join-Path $repo '.agent/results/task-a'
New-Item -ItemType Directory -Force -Path $results | Out-Null
try {
    foreach ($taskId in @('task-a', 'task-b', 'task-c')) {
        $sourceEvidence = Join-Path $temp "$taskId-source"
        $destinationEvidence = Join-Path $temp "$taskId-implementation\.agent\results\$taskId\baseline"
        New-Item -ItemType Directory -Force -Path $sourceEvidence | Out-Null
        $startedAt = [DateTime]::UtcNow.AddSeconds(-3)
        Set-Content -LiteralPath (Join-Path $sourceEvidence 'restore.log') -Value 'restore completed'
        Set-Content -LiteralPath (Join-Path $sourceEvidence 'tests.log') -Value 'Passed: 12, Failed: 0'
        Set-Content -LiteralPath (Join-Path $sourceEvidence 'baseline.trx') -Value '<TestRun><Results /></TestRun>'
        $manifestPath = Copy-PipelineBaselineEvidence -SourceDirectory $sourceEvidence `
            -DestinationDirectory $destinationEvidence -RunStartedAtUtc $startedAt `
            -TaskId $taskId -FeatureBranch "feature/$taskId" -FeatureHead ('a' * 40) `
            -BaseCommit ('b' * 40) -TestExitCode 0
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.taskId -ne $taskId -or $manifest.artifacts.Count -ne 3) { throw "Baseline evidence manifest is incomplete for $taskId." }
        foreach ($name in @('restore.log', 'tests.log', 'baseline.trx')) {
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $sourceEvidence $name) -Algorithm SHA256).Hash
            $copiedHash = (Get-FileHash -LiteralPath (Join-Path $destinationEvidence $name) -Algorithm SHA256).Hash
            if ($sourceHash -ne $copiedHash) { throw "Fresh baseline evidence was not copied intact for $taskId/$name." }
        }

        $staleLog = Join-Path $sourceEvidence 'tests.log'
        (Get-Item -LiteralPath $staleLog).LastWriteTimeUtc = $startedAt.AddMinutes(-1)
        try {
            Copy-PipelineBaselineEvidence -SourceDirectory $sourceEvidence -DestinationDirectory $destinationEvidence `
                -RunStartedAtUtc $startedAt -TaskId $taskId -FeatureBranch "feature/$taskId" `
                -FeatureHead ('a' * 40) -BaseCommit ('b' * 40) -TestExitCode 0 | Out-Null
            throw "Stale baseline evidence was accepted for $taskId."
        }
        catch {
            if ($_.Exception.Message -notmatch 'Baseline evidence is stale') { throw }
        }
    }

    $artifact = Join-Path $results 'same.txt'
    Set-Content -LiteralPath $artifact -Value 'preserved'
    if (Copy-PipelineArtifact -Source $artifact -Destination $artifact) { throw 'Same-path copy did not no-op.' }
    if ((Get-Content -LiteralPath $artifact -Raw).Trim() -ne 'preserved') { throw 'Same-path copy changed the artifact.' }

    if (Test-PipelineAcceptanceApplicable -TaskText 'Implement internal server-only reporting service.' -DiffText 'DesignMode.AI.Server/Reporting/Source.cs') { throw 'Server-only implementation incorrectly requires browser acceptance.' }
    $commentOnlyHtmlDiff = @'
diff --git a/DesignMode.AI.Client/wwwroot/index.html b/DesignMode.AI.Client/wwwroot/index.html
index 1111111..2222222 100644
--- a/DesignMode.AI.Client/wwwroot/index.html
+++ b/DesignMode.AI.Client/wwwroot/index.html
@@ -1,1 +1,1 @@
-<!-- DEPLOY TEST 2026-09-25-921 -->
+<!-- DEPLOY TEST 2026-10-05-729 -->
'@
    if (Test-PipelineAcceptanceApplicable -TaskText 'Internal reporting service.' -DiffText $commentOnlyHtmlDiff) { throw 'HTML comment-only change incorrectly required browser acceptance.' }

    $actualHtmlDiff = @'
diff --git a/DesignMode.AI.Client/wwwroot/index.html b/DesignMode.AI.Client/wwwroot/index.html
index 1111111..2222222 100644
--- a/DesignMode.AI.Client/wwwroot/index.html
+++ b/DesignMode.AI.Client/wwwroot/index.html
@@ -1,1 +1,1 @@
-<main>Old dashboard</main>
+<main>New dashboard</main>
'@
    if (-not (Test-PipelineAcceptanceApplicable -TaskText 'Internal reporting service.' -DiffText $actualHtmlDiff)) { throw 'Meaningful HTML change did not require browser acceptance.' }

    $mixedHtmlDiff = @'
diff --git a/DesignMode.AI.Client/wwwroot/index.html b/DesignMode.AI.Client/wwwroot/index.html
index 1111111..2222222 100644
--- a/DesignMode.AI.Client/wwwroot/index.html
+++ b/DesignMode.AI.Client/wwwroot/index.html
@@ -1,2 +1,2 @@
-<!-- DEPLOY TEST 2026-09-25-921 -->
+<!-- DEPLOY TEST 2026-10-05-729 -->
-<main>Old dashboard</main>
+<main>New dashboard</main>
'@
    if (-not (Test-PipelineAcceptanceApplicable -TaskText 'Internal reporting service.' -DiffText $mixedHtmlDiff)) { throw 'Functional HTML change was masked by a comment-only change.' }

    $multilineCommentDiff = @'
diff --git a/DesignMode.AI.Client/wwwroot/index.html b/DesignMode.AI.Client/wwwroot/index.html
index 1111111..2222222 100644
--- a/DesignMode.AI.Client/wwwroot/index.html
+++ b/DesignMode.AI.Client/wwwroot/index.html
@@ -1,4 +1,4 @@
 <html>
 <!-- Deployment marker
-old marker value
+new marker value
 -->
 </html>
'@
    if (Test-PipelineAcceptanceApplicable -TaskText 'Internal reporting service.' -DiffText $multilineCommentDiff) { throw 'Change inside a multiline HTML comment incorrectly required browser acceptance.' }

    $razorUiDiff = @'
diff --git a/DesignMode.AI.Client/Pages/Dashboard.razor b/DesignMode.AI.Client/Pages/Dashboard.razor
@@ -1 +1 @@
-<button>Old</button>
+<button>New</button>
'@
    if (-not (Test-PipelineAcceptanceApplicable -TaskText 'Add a button to the dashboard.' -DiffText $razorUiDiff)) { throw 'UI implementation did not require browser acceptance.' }

    $httpRouteDiff = @'
diff --git a/DesignMode.AI.Server/Reporting/Function.cs b/DesignMode.AI.Server/Reporting/Function.cs
@@ -1 +1 @@
-[HttpTrigger(AuthorizationLevel.Function, "get", Route = "old")]
+[HttpTrigger(AuthorizationLevel.Function, "get", Route = "new")]
'@
    if (-not (Test-PipelineAcceptanceApplicable -TaskText 'Add an endpoint.' -DiffText $httpRouteDiff)) { throw 'HTTP route change did not require browser acceptance.' }

    $httpResponseDiff = @'
diff --git a/DesignMode.AI.Server/Reporting/Function.cs b/DesignMode.AI.Server/Reporting/Function.cs
@@ -1 +1 @@
-return Results.Ok(oldResponse);
+return Results.Ok(newResponse);
'@
    if (-not (Test-PipelineAcceptanceApplicable -TaskText 'Change the reporting response.' -DiffText $httpResponseDiff)) { throw 'HTTP response change did not require browser acceptance.' }

    New-Item -ItemType Directory -Force -Path $repo | Out-Null
    git -C $repo init -b master-caio | Out-Null
    git -C $repo config user.name 'Pipeline Test'
    git -C $repo config user.email 'pipeline-test@example.invalid'
    Set-Content -LiteralPath (Join-Path $repo 'tracked.txt') -Value 'base'
    git -C $repo add tracked.txt
    git -C $repo commit -m 'test base' | Out-Null
    $cwd = (Get-Location).Path
    $worktree = Get-OrCreate-PipelineWorktree -RepoRoot $repo -TaskId 'task-a' -ResultDir $results
    if (-not (Test-Path -LiteralPath $worktree -PathType Container)) { throw 'Task worktree was not created.' }
    $expectedWorktreeRoot = [IO.Path]::GetFullPath((Join-Path $repo 'worktrees'))
    if (-not [IO.Path]::GetFullPath($worktree).StartsWith($expectedWorktreeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Task worktree was not created under the repository worktrees directory.' }
    if ((Get-Content -LiteralPath (Join-Path $worktree 'tracked.txt') -Raw).Trim() -ne 'base') { throw 'New no-checkout worktree was not populated from its branch.' }
    if ((git -C $worktree status --porcelain).Count -ne 0) { throw 'New implementation worktree is not clean after creation.' }
    Set-Content -LiteralPath (Join-Path $worktree 'tracked.txt') -Value 'feature'
    git -C $worktree add -- tracked.txt
    if ($LASTEXITCODE -ne 0) { throw 'Git could not stage a file in the isolated task worktree.' }
    git -C $worktree commit -m 'test implementation'
    if ($LASTEXITCODE -ne 0) { throw 'Git could not commit a file in the isolated task worktree.' }
    if ((git -C $worktree rev-parse --verify 'HEAD^{commit}').Trim() -eq (git -C $repo rev-parse HEAD).Trim()) { throw 'Feature worktree commit did not advance its branch.' }
    if ((Get-Location).Path -ne $cwd) { throw 'Task worktree creation switched the control checkout.' }
    $resolved = Get-PipelineImplementationRoot -RepoRoot $repo -Branch ((git -C $worktree branch --show-current).Trim())
    if ([IO.Path]::GetFullPath($resolved) -ne [IO.Path]::GetFullPath($worktree)) { throw "Implementation worktree resolution failed: '$resolved' versus '$worktree'." }
    if ((Get-OrCreate-PipelineWorktree -RepoRoot $repo -TaskId 'task-a' -ResultDir $results) -ne $worktree) { throw 'Task worktree resume did not reuse the recorded worktree.' }
    $implementationRoot = Join-Path $temp 'implementation-checkout'
    New-Item -ItemType Directory -Force -Path $implementationRoot | Out-Null
    $implementationBranch = 'pipeline/task-a-20261008-123456'
    $implementationHead = (git -C $repo rev-parse HEAD).Trim()
    function git {
        if ($args[0] -eq '-C' -and [IO.Path]::GetFullPath($args[1]) -eq [IO.Path]::GetFullPath($implementationRoot)) {
            if ($args -contains '--show-current') { $global:LASTEXITCODE = 0; return $implementationBranch }
            if ($args -contains 'HEAD^{commit}') { $global:LASTEXITCODE = 0; return $implementationHead }
            if ($args -contains '--is-ancestor') {
                $global:LASTEXITCODE = if ($implementationHead.StartsWith($args[4], [StringComparison]::OrdinalIgnoreCase)) { 0 } else { 1 }
                return
            }
        }
        throw "Unexpected Git command in implementation checkout fixture: $($args -join ' ')"
    }
    Set-Content -LiteralPath (Join-Path $results 'implementation-worktree.txt') -Value $implementationRoot

    $stageResult = Join-Path $results 'qa.md'
    $stageHead = Join-Path $results 'qa-head.txt'
    Set-Content -LiteralPath $stageResult -Value 'STATUS: PASS'
    Set-Content -LiteralPath $stageHead -Value 'abc123'
    if (-not (Test-PipelineStageReusable -ResultPath $stageResult -HeadPath $stageHead -ExpectedHead 'abc123' -StatusPattern '(?im)^STATUS:\s*PASS\s*$')) { throw 'Successful matching stage evidence was not reusable.' }
    if (Test-PipelineStageReusable -ResultPath $stageResult -HeadPath $stageHead -ExpectedHead 'def456' -StatusPattern '(?im)^STATUS:\s*PASS\s*$') { throw 'Stage evidence was reused for a different HEAD.' }

    # Reproduce post-repair: a stale control-checkout result says REPAIR while
    # the just-written implementation-worktree result requests REGRESSION.
    $worktreeResult = Join-Path $results 'worktree-triage.md'
    Set-Content -LiteralPath $stageResult -Value 'NEXT_ACTION: REPAIR'
    Set-Content -LiteralPath $worktreeResult -Value 'NEXT_ACTION: REGRESSION'
    Copy-PipelineArtifact -Source $worktreeResult -Destination $stageResult | Out-Null
    if ((Get-PipelineTriageAction -Text (Get-Content -LiteralPath $stageResult -Raw)) -ne 'REGRESSION') { throw 'Post-repair REGRESSION result did not override stale control-checkout evidence.' }
    # Developer evidence is produced inside a distinct implementation worktree,
    # then promoted to the control checkout and reused on resume.
    $actualImplementationBranch = (git -C $implementationRoot branch --show-current).Trim()
    if ($actualImplementationBranch -ne $implementationBranch) { throw 'Test implementation branch setup failed.' }
    $implementationResults = Join-Path $implementationRoot '.agent/results/task-a'
    New-Item -ItemType Directory -Force -Path $implementationResults | Out-Null
    $developmentEvidence = Join-Path $implementationResults 'development.md'
    Set-Content -LiteralPath $developmentEvidence -Value "STATUS: IMPLEMENTED`nBRANCH: $implementationBranch`nCOMMIT: $($implementationHead.Substring(0, 12)) (implementation)"
    $promoted = Sync-PipelineDevelopmentResult -RepoRoot $repo -TaskId 'task-a' -ResultDir $results -ImplementationRoot $implementationRoot
    if ($promoted -notmatch [regex]::Escape($implementationBranch) -or -not (Test-Path -LiteralPath (Join-Path $results 'development.md'))) { throw 'Development evidence was not promoted from the implementation worktree.' }
    $controlCopy = Get-Content -LiteralPath (Join-Path $results 'development.md') -Raw
    $context = Get-Content -LiteralPath (Join-Path $results 'development-context.json') -Raw | ConvertFrom-Json
    if ($context.taskId -ne 'task-a' -or $context.branch -ne $implementationBranch -or $context.implementationHead -ne $implementationHead) { throw 'Promoted Developer result context does not identify the task branch and implementation HEAD.' }
    Sync-PipelineDevelopmentResult -RepoRoot $repo -TaskId 'task-a' -ResultDir $results -ImplementationRoot $implementationRoot | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $results 'development.md') -Raw) -ne $controlCopy) { throw 'Resuming changed verified Developer evidence.' }
    if ((Get-PipelineDeveloperStageAction -RepoRoot $repo -TaskId 'task-a' -ResultDir $results) -ne 'REUSE') { throw 'A resumed task would repeat the Developer implementation instead of advancing to QA.' }

    Set-Content -LiteralPath (Join-Path $results 'development.md') -Value ($controlCopy + "CONTROL EVIDENCE")
    try {
        Sync-PipelineDevelopmentResult -RepoRoot $repo -TaskId 'task-a' -ResultDir $results -ImplementationRoot $implementationRoot | Out-Null
        throw 'Existing control evidence was overwritten.'
    }
    catch {
        if ($_.Exception.Message -eq 'Existing control evidence was overwritten.') { throw }
        if ($_.Exception.Message -notmatch 'refusing to overwrite') { throw }
    }
    Set-Content -LiteralPath (Join-Path $results 'development.md') -Value $controlCopy

    Set-Content -LiteralPath $developmentEvidence -Value "STATUS: IMPLEMENTED`nBRANCH: unrelated/task-a-20261008-124246`nCOMMIT: $($implementationHead.Substring(0, 12))"
    try {
        Sync-PipelineDevelopmentResult -RepoRoot $repo -TaskId 'task-a' -ResultDir $results -ImplementationRoot $implementationRoot | Out-Null
        throw 'Developer evidence for a different branch was accepted.'
    }
    catch {
        if ($_.Exception.Message -eq 'Developer evidence for a different branch was accepted.') { throw }
        if ($_.Exception.Message -notmatch 'does not match implementation branch') { throw }
    }

    Set-Content -LiteralPath $developmentEvidence -Value "STATUS: IMPLEMENTED`nBRANCH: $implementationBranch`nCOMMIT: deadbee (implementation)"
    try {
        Sync-PipelineDevelopmentResult -RepoRoot $repo -TaskId 'task-a' -ResultDir $results -ImplementationRoot $implementationRoot | Out-Null
        throw 'Developer evidence for a different commit was accepted.'
    }
    catch {
        if ($_.Exception.Message -eq 'Developer evidence for a different commit was accepted.') { throw }
        if ($_.Exception.Message -notmatch 'not an ancestor of implementation HEAD') { throw }
    }
    $declared = @(Get-PipelineDeveloperChangedFiles -Text "TASK_ID: task-a`nFILES_CHANGED:`n- DesignMode.AI.Client/Pages/Home.razor`nTESTS: Deferred" -ImplementationRoot $worktree)
    if ($declared.Count -ne 1 -or $declared[0] -ne 'DesignMode.AI.Client/Pages/Home.razor') { throw 'Safe Developer changed-file list was not parsed.' }
    foreach ($unsafe in @("TASK_ID: task-a`nFILES_CHANGED:`n- ../outside.txt`nTESTS: Deferred", "TASK_ID: task-a`nFILES_CHANGED:`n- .agent/results/task-a/qa.md`nTESTS: Deferred")) {
        try { Get-PipelineDeveloperChangedFiles -Text $unsafe -ImplementationRoot $worktree | Out-Null; throw 'Unsafe Developer changed-file entry was accepted.' }
        catch { if ($_.Exception.Message -eq 'Unsafe Developer changed-file entry was accepted.') { throw } }
    }
    Write-Host 'Pipeline helper tests passed.'
}
finally {
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
