if (-not $env:DESIGNMODE_GIT_SAFEDIR_CONFIGURED) {
    $count = 0
    if ($env:GIT_CONFIG_COUNT -match '^\d+$') { $count = [int]$env:GIT_CONFIG_COUNT }
    [Environment]::SetEnvironmentVariable("GIT_CONFIG_KEY_$count", 'safe.directory', 'Process')
    [Environment]::SetEnvironmentVariable("GIT_CONFIG_VALUE_$count", '*', 'Process')
    $env:GIT_CONFIG_COUNT = [string]($count + 1)
    $env:DESIGNMODE_GIT_SAFEDIR_CONFIGURED = '1'
}

function Resolve-PipelinePath {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path) }
    return [IO.Path]::GetFullPath($Path)
}

function Get-PipelineStageResultField {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z][A-Za-z0-9_-]*$')][string]$FieldName,
        [string]$ResultName = 'Stage result'
    )

    $normalizedText = $Text.TrimStart([char]0xFEFF)
    $values = [Collections.Generic.List[string]]::new()
    foreach ($line in ($normalizedText -split "`r`n|`n|`r")) {
        $match = [regex]::Match($line, '^[ \t]*(?<name>[A-Za-z][A-Za-z0-9_-]*)[ \t]*:[ \t]*(?<value>.*?)[ \t]*$')
        if ($match.Success -and $match.Groups['name'].Value -ieq $FieldName) {
            $values.Add($match.Groups['value'].Value.Trim())
            continue
        }

        if ($line -match "^[ `t]*$([regex]::Escape($FieldName))(?:[ `t]|$)") {
            throw "$ResultName has malformed $FieldName metadata. Expected '$($FieldName): <value>'."
        }
    }

    if ($values.Count -eq 0) {
        throw "$ResultName is missing $FieldName metadata. Add a '$($FieldName): <value>' line."
    }
    if ($values.Count -gt 1) {
        throw "$ResultName contains duplicate $FieldName metadata; keep exactly one '$($FieldName): <value>' line."
    }
    if ([string]::IsNullOrWhiteSpace($values[0])) {
        throw "$ResultName contains empty $FieldName metadata. Set '$($FieldName): <value>' to a non-empty value."
    }

    return $values[0]
}

function Test-PipelineStageReusable {
    param([Parameter(Mandatory)][string]$ResultPath, [Parameter(Mandatory)][string]$HeadPath, [Parameter(Mandatory)][string]$ExpectedHead, [Parameter(Mandatory)][string]$StatusPattern)
    return (Test-Path -LiteralPath $ResultPath -PathType Leaf) -and
        (Test-Path -LiteralPath $HeadPath -PathType Leaf) -and
        ((Get-Content -LiteralPath $HeadPath -Raw).Trim() -eq $ExpectedHead) -and
        ((Get-Content -LiteralPath $ResultPath -Raw) -match $StatusPattern)
}

function Get-PipelineTriageAction {
    param([Parameter(Mandatory)][string]$Text, [string]$ResultName = 'Triage result')

    $match = [regex]::Match($Text.TrimStart([char]0xFEFF), '(?im)^[ \t]*NEXT_ACTION:[ \t]*(REPAIR|REGRESSION|HUMAN_REQUIRED)[ \t]*\r?$')
    if (-not $match.Success) { throw "$ResultName has an unknown or missing NEXT_ACTION." }
    return $match.Groups[1].Value.ToUpperInvariant()
}

function Assert-PipelineRegressionEligible {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TriageText,
        [Parameter(Mandatory)][string]$QaText,
        [Parameter(Mandatory)][string]$EvidenceSummaryText,
        [Parameter(Mandatory)][string]$TaskText,
        [string]$ResultName = 'Triage result'
    )

    $triageStatus = Get-PipelineStageResultField -Text $TriageText -FieldName 'FEATURE_QA_STATUS' -ResultName $ResultName
    $branchRegressions = Get-PipelineStageResultField -Text $TriageText -FieldName 'BRANCH_REGRESSIONS' -ResultName $ResultName
    $repairRequired = Get-PipelineStageResultField -Text $TriageText -FieldName 'REPAIR_REQUIRED' -ResultName $ResultName
    if ($triageStatus -notin @('PASS', 'PASS_WITH_BASELINE_FAILURES') -or $branchRegressions -ne '0' -or $repairRequired -ne 'NO') {
        throw "$ResultName cannot route to Regression without passing feature QA and zero branch regressions."
    }

    $qaStatus = Get-PipelineStageResultField -Text $QaText -FieldName 'STATUS' -ResultName 'QA result'
    if ($qaStatus -notin @('PASS', 'PASS_WITH_TEST_FAILURES_PENDING_TRIAGE')) {
        throw 'QA status is not eligible for Regression.'
    }
    $acceptanceApplicable = Get-PipelineStageResultField -Text $EvidenceSummaryText -FieldName 'ACCEPTANCE_APPLICABLE' -ResultName 'QA evidence summary'
    if ($acceptanceApplicable -notin @('True', 'False')) { throw 'Acceptance applicability is missing or invalid.' }
    $acceptance = Get-PipelineStageResultField -Text $QaText -FieldName 'ACCEPTANCE' -ResultName 'QA result'
    if ($acceptanceApplicable -eq 'True') {
        $acceptanceExit = Get-PipelineStageResultField -Text $EvidenceSummaryText -FieldName 'ACCEPTANCE_EXIT_CODE' -ResultName 'QA evidence summary'
        if ($acceptanceExit -ne '0' -or $acceptance -ne 'PASS') { throw 'Required browser acceptance evidence did not pass.' }
        foreach ($field in @('DESKTOP_PLAYWRIGHT', 'MOBILE_WEBKIT')) {
            if ((Get-PipelineStageResultField -Text $QaText -FieldName $field -ResultName 'QA result') -ne 'PASS') {
                throw "Required browser acceptance result is not PASS: $field."
            }
        }
    }
    elseif ($acceptance -ne 'NOT APPLICABLE') {
        throw 'QA browser acceptance applicability does not match its evidence summary.'
    }

    if ($TaskText -match '(?i)authorization|unauthenticated|ordinary customer|server-side') {
        if ((Get-PipelineStageResultField -Text $QaText -FieldName 'AUTHORIZATION_TESTS' -ResultName 'QA result') -ne 'PASS') {
            throw 'Required server authorization boundary tests did not pass.'
        }
    }
}

function Get-OrCreate-PipelineWorktree {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$TaskId, [Parameter(Mandatory)][string]$ResultDir)
    if ($TaskId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'TaskId contains unsupported path or branch characters.' }
    $record = Join-Path $ResultDir 'implementation-worktree.txt'
    if (Test-Path -LiteralPath $record) {
        $path = (Get-Content -LiteralPath $record -Raw).Trim()
        if (Test-Path -LiteralPath $path -PathType Container) {
            $branch = (git -C $path branch --show-current).Trim()
            if ($LASTEXITCODE -eq 0 -and $branch -match "^automation/$([regex]::Escape($TaskId))-\d{8}-\d{6}$") { return $path }
            throw "Recorded implementation worktree has an unexpected branch: $branch"
        }
    }
    $worktreeRoot = Join-Path $RepoRoot 'worktrees'
    New-Item -ItemType Directory -Force -Path $worktreeRoot | Out-Null
    $head = (git -C $RepoRoot rev-parse --verify 'refs/heads/master-caio^{commit}').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve local master-caio for the isolated task worktree.' }
    $stamp = [DateTime]::UtcNow
    do {
        $launch = $stamp.ToString('yyyyMMdd-HHmmss')
        $path = Join-Path $worktreeRoot "automation-$TaskId-$launch"
        $branch = "automation/$TaskId-$launch"
        git -C $RepoRoot show-ref --verify --quiet "refs/heads/$branch"
        $branchExists = ($LASTEXITCODE -eq 0)
        if ($branchExists -or (Test-Path -LiteralPath $path)) { $stamp = $stamp.AddSeconds(1) }
    } while ($branchExists -or (Test-Path -LiteralPath $path))
    # Git for Windows 2.41 can run the internal checkout/reset from the
    # source worktree when creating a linked worktree. Create the registration
    # without checkout, then populate the newly created empty worktree in place.
    $null = git -C $RepoRoot worktree add --no-checkout -b $branch $path $head
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create isolated implementation worktree.' }
    git -C $path checkout --force $branch | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to populate newly created implementation worktree '$path'; preserving it for diagnosis." }
    Set-Content -LiteralPath $record -Value $path -Encoding UTF8
    return $path
}

function Copy-PipelineArtifact {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Artifact source not found: $Source" }
    $sourcePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Source).Path)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    if ([string]::Equals($sourcePath, $destinationPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $parent = Split-Path -Parent $destinationPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    return $true
}

function Copy-PipelineBaselineEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [Parameter(Mandatory)][DateTime]$RunStartedAtUtc,
        [Parameter(Mandatory)][string]$TaskId,
        [Parameter(Mandatory)][string]$FeatureBranch,
        [Parameter(Mandatory)][string]$FeatureHead,
        [Parameter(Mandatory)][string]$BaseCommit,
        [Parameter(Mandatory)][int]$TestExitCode
    )

    $SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
    $DestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
    $artifacts = @('restore.log', 'tests.log', 'baseline.trx')
    $records = @()

    foreach ($name in $artifacts) {
        $source = Join-Path $SourceDirectory $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Fresh baseline evidence is missing: $source" }
        $item = Get-Item -LiteralPath $source
        if ($item.Length -eq 0) { throw "Baseline evidence is empty: $source" }
        if ($item.LastWriteTimeUtc -lt $RunStartedAtUtc.AddSeconds(-1)) { throw "Baseline evidence is stale: $source" }

        if ($name -eq 'baseline.trx') {
            try {
                [xml]$trx = Get-Content -LiteralPath $source -Raw
                if ($trx.DocumentElement.LocalName -ne 'TestRun') { throw 'TRX root element is not TestRun.' }
            }
            catch { throw "Baseline TRX is invalid: $($_.Exception.Message)" }
        }

        $destination = Join-Path $DestinationDirectory $name
        Copy-PipelineArtifact -Source $source -Destination $destination | Out-Null
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) { throw "Copied baseline evidence hash mismatch: $name" }
        $records += [ordered]@{ name = $name; sha256 = $sourceHash; bytes = $item.Length; lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o') }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        taskId = $TaskId
        featureBranch = $FeatureBranch
        featureHead = $FeatureHead
        baseCommit = $BaseCommit
        baselineTestExitCode = $TestExitCode
        runStartedAtUtc = $RunStartedAtUtc.ToString('o')
        artifacts = $records
    }
    $manifestPath = Join-Path $DestinationDirectory 'evidence-manifest.json'
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    return $manifestPath
}

function Get-PipelineDeveloperChangedFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$ImplementationRoot
    )

    $lines = $Text.TrimStart([char]0xFEFF) -split "`r`n|`n|`r"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^[ \t]*FILES_CHANGED:[ \t]*$') {
            if ($start -ge 0) { throw 'Developer result contains duplicate FILES_CHANGED sections.' }
            $start = $i + 1
        }
    }
    if ($start -lt 0) { throw 'Developer result is missing FILES_CHANGED.' }

    $paths = [Collections.Generic.List[string]]::new()
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^[ \t]*[A-Z][A-Z0-9_]*:[ \t]*') { break }
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        if ($lines[$i] -notmatch '^[ \t]*-[ \t]+(?<path>[^\r\n]+?)[ \t]*$') {
            throw 'Developer FILES_CHANGED entries must use one "- relative/path" per line.'
        }

        $relative = $Matches.path.Replace('\', '/')
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.?(/|$)' -or $relative -match '^[A-Za-z]:' -or $relative.Contains('*') -or $relative.Contains('?')) {
            throw "Developer result contains an unsafe changed-file path: $relative"
        }
        if ($relative -match '(^|/)(\.git|\.nuget|\.dotnet|bin|obj)(/|$)' -or $relative -match '^\.agent/') {
            throw "Developer result may not stage generated or pipeline-control files: $relative"
        }

        $fullPath = [IO.Path]::GetFullPath((Join-Path $ImplementationRoot $relative))
        $rootPath = [IO.Path]::GetFullPath($ImplementationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Developer result changed-file path escapes its implementation worktree: $relative"
        }
        if ($paths.Contains($relative)) { throw "Developer result lists a changed file more than once: $relative" }
        $paths.Add($relative)
    }
    if ($paths.Count -eq 0) { throw 'Developer result has no changed implementation files.' }
    return $paths.ToArray()
}

function Invoke-PipelineCheckedCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$Label
    )

    $parent = Split-Path -Parent $LogPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    & $Executable @ArgumentList 2>&1 | Tee-Object -FilePath $LogPath | Out-Null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "$Label failed with exit code $exitCode. See $LogPath" }
    return $exitCode
}

function Get-PipelineImplementationRoot {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Branch)
    $current = $null
    foreach ($line in @(git -C $RepoRoot worktree list --porcelain)) {
        if ($line -match '^worktree\s+(.+)$') { $current = $Matches[1]; continue }
        if ($line -match '^branch\s+refs/heads/(.+)$' -and $Matches[1] -eq $Branch) {
            if ([IO.Path]::GetFullPath($current) -eq [IO.Path]::GetFullPath($RepoRoot)) { return $null }
            return $current
        }
    }
    return $null
}

function Sync-PipelineDevelopmentResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$TaskId,
        [Parameter(Mandatory)][string]$ResultDir,
        [Parameter(Mandatory)][string]$ImplementationRoot
    )

    $record = Join-Path $ResultDir 'implementation-worktree.txt'
    if (-not (Test-Path -LiteralPath $record -PathType Leaf)) {
        throw "Implementation worktree record not found for task '$TaskId'."
    }

    $recordedRoot = [IO.Path]::GetFullPath((Get-Content -LiteralPath $record -Raw).Trim())
    $implementationPath = [IO.Path]::GetFullPath($ImplementationRoot)
    if (-not [string]::Equals($recordedRoot, $implementationPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Implementation worktree does not match task '$TaskId' worktree record."
    }

    $branch = (git -C $implementationPath branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -notmatch "^[A-Za-z0-9._/-]+/$([regex]::Escape($TaskId))-\d{8}-\d{6}$") {
        throw "Implementation branch '$branch' is not assigned to task '$TaskId'."
    }

    $source = Join-Path $implementationPath ".agent/results/$TaskId/development.md"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Developer result not found in implementation worktree: $source"
    }

    $text = Get-Content -LiteralPath $source -Raw
    $reportedBranch = Get-PipelineStageResultField -Text $text -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"
    $reportedCommit = Get-PipelineStageResultField -Text $text -FieldName 'COMMIT' -ResultName "Developer result for '$TaskId'"
    if ($reportedBranch -ne $branch) { throw "Developer result branch '$reportedBranch' does not match implementation branch '$branch'." }
    if ($reportedCommit -notmatch '^(?<sha>[0-9a-fA-F]{7,40})(?:\s|$)') { throw "Developer result for '$TaskId' has an invalid COMMIT value." }
    $commitSha = $Matches.sha

    $head = (git -C $implementationPath rev-parse --verify 'HEAD^{commit}').Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to resolve implementation HEAD for task '$TaskId'." }
    git -C $implementationPath merge-base --is-ancestor $commitSha HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Developer result commit '$commitSha' is not an ancestor of implementation HEAD '$head'."
    }

    $taskIdField = [regex]::Match($text, '(?im)^[ \t]*TASK_ID:[ \t]*(?<value>[^\r\n]+)$')
    if ($taskIdField.Success -and $taskIdField.Groups['value'].Value.Trim() -ne $TaskId) {
        throw "Developer result task id '$($taskIdField.Groups['value'].Value.Trim())' does not match '$TaskId'."
    }

    $destination = Join-Path $ResultDir 'development.md'
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existing = Get-Content -LiteralPath $destination -Raw
        if ($existing -ne $text) {
            throw "Existing Developer result for '$TaskId' differs from its implementation evidence; refusing to overwrite it."
        }
    }

    Copy-PipelineArtifact -Source $source -Destination $destination | Out-Null
    $contextPath = Join-Path $ResultDir 'development-context.json'
    [ordered]@{
        taskId = $TaskId
        branch = $branch
        resultCommit = $commitSha
        implementationHead = $head
    } | ConvertTo-Json | Set-Content -LiteralPath $contextPath -Encoding UTF8
    return $text
}

function Get-PipelineDeveloperStageAction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$TaskId,
        [Parameter(Mandatory)][string]$ResultDir
    )

    $record = Join-Path $ResultDir 'implementation-worktree.txt'
    if (-not (Test-Path -LiteralPath $record -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $ResultDir 'development.md') -PathType Leaf) {
            throw "Developer result for '$TaskId' has no implementation worktree record; refusing to reuse unverified evidence."
        }
        return 'RUN'
    }

    $implementationRoot = (Get-Content -LiteralPath $record -Raw).Trim()
    $implementationResult = Join-Path $implementationRoot ".agent/results/$TaskId/development.md"
    if (Test-Path -LiteralPath $implementationResult -PathType Leaf) {
        Sync-PipelineDevelopmentResult -RepoRoot $RepoRoot -TaskId $TaskId -ResultDir $ResultDir -ImplementationRoot $implementationRoot | Out-Null
        return 'REUSE'
    }

    if (Test-Path -LiteralPath (Join-Path $ResultDir 'development.md') -PathType Leaf) {
        throw "Recorded implementation result is missing for '$TaskId'; refusing to reuse unverified control-checkout evidence."
    }

    return 'RUN'
}

function Test-PipelineHtmlDiffLineHasVisibleContent {
    param([Parameter(Mandatory)][string]$Line, [Parameter(Mandatory)][ref]$InsideComment)

    $position = 0
    while ($position -lt $Line.Length) {
        if ($InsideComment.Value) {
            $commentEnd = $Line.IndexOf('-->', $position, [StringComparison]::Ordinal)
            if ($commentEnd -lt 0) { return $false }
            $InsideComment.Value = $false
            $position = $commentEnd + 3
            continue
        }

        $commentStart = $Line.IndexOf('<!--', $position, [StringComparison]::Ordinal)
        if ($commentStart -lt 0) { return -not [string]::IsNullOrWhiteSpace($Line.Substring($position)) }
        if (-not [string]::IsNullOrWhiteSpace($Line.Substring($position, $commentStart - $position))) { return $true }
        $InsideComment.Value = $true
        $position = $commentStart + 4
    }

    return $false
}

function Test-PipelineHtmlDiffHasFunctionalChange {
    param([Parameter(Mandatory)][string[]]$DiffLines)

    $oldInsideComment = $false
    $newInsideComment = $false
    foreach ($line in $DiffLines) {
        if ($line.StartsWith('+++') -or $line.StartsWith('---') -or $line.StartsWith('\\') -or $line.StartsWith('@@')) { continue }
        if ($line.Length -eq 0) { continue }

        $prefix = $line.Substring(0, 1)
        $content = if ($line.Length -gt 1) { $line.Substring(1) } else { '' }
        if ($prefix -eq ' ') {
            $oldIgnored = Test-PipelineHtmlDiffLineHasVisibleContent -Line $content -InsideComment ([ref]$oldInsideComment)
            $newIgnored = Test-PipelineHtmlDiffLineHasVisibleContent -Line $content -InsideComment ([ref]$newInsideComment)
            continue
        }
        if ($prefix -eq '-') {
            if (Test-PipelineHtmlDiffLineHasVisibleContent -Line $content -InsideComment ([ref]$oldInsideComment)) { return $true }
            continue
        }
        if ($prefix -eq '+') {
            if (Test-PipelineHtmlDiffLineHasVisibleContent -Line $content -InsideComment ([ref]$newInsideComment)) { return $true }
        }
    }

    return $false
}

function Test-PipelineAcceptanceApplicable {
    param([Parameter(Mandatory)][string]$TaskText, [Parameter(Mandatory)][string]$DiffText)

    $httpBehaviorChanged = $false
    $lines = @($DiffText -split "`r?`n")
    $currentFile = $null
    $fileLines = [Collections.Generic.List[string]]::new()

    foreach ($line in $lines) {
        if ($line -match '^diff --git a/(.+?) b/(.+)$') {
            if ($currentFile -and $currentFile -match '(?i)\.html$' -and (Test-PipelineHtmlDiffHasFunctionalChange -DiffLines $fileLines.ToArray())) { return $true }
            $currentFile = $Matches[2]
            $fileLines.Clear()
            continue
        }

        if (-not $currentFile) { continue }
        $fileLines.Add($line)

        if ($line -match '^[+-](?![+-]).*(\[HttpTrigger|Map(Get|Post|Put|Delete)|Route\s*=|Results\.(Ok|Created|Accepted|NoContent|NotFound|BadRequest|Unauthorized|Forbid|StatusCode)|StatusCode\s*=|WriteAsJsonAsync|WriteStringAsync|ContentType\s*=)') { $httpBehaviorChanged = $true }
        if ($currentFile -notmatch '(?i)(\.razor|\.html|\.css|\.js|wwwroot/)') { continue }
        if ($currentFile -match '(?i)\.html$') { continue }
        if ($line -match '^[+-](?![+-]).*\S') { return $true }
    }

    if ($currentFile -and $currentFile -match '(?i)\.html$' -and (Test-PipelineHtmlDiffHasFunctionalChange -DiffLines $fileLines.ToArray())) { return $true }
    return $httpBehaviorChanged
}

function Test-PipelineAcceptanceSpec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRunMarker
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ IsValid = $false; Error = "Acceptance spec is missing: $Path" }
    }

    $content = Get-Content -LiteralPath $Path -Raw
    if (-not $content.Contains($ExpectedRunMarker)) {
        return [pscustomobject]@{ IsValid = $false; Error = 'Acceptance spec is stale: its task/run marker does not match this generation attempt.' }
    }
    $importsApprovedFixture = $content -match "require\s*\(\s*'\./safe-playwright\.cjs'\s*\)" -or
        $content -match 'require\s*\(\s*"\./safe-playwright\.cjs"\s*\)'
    if (-not $importsApprovedFixture -or
        $content -notmatch '\bconst\s*\{\s*test\s*,\s*expect\s*\}\s*=') {
        return [pscustomobject]@{ IsValid = $false; Error = "Import { test, expect } from './safe-playwright.cjs'; this fixture blocks every non-local network origin." }
    }
    if ($content -notmatch '\btest\s*\(' -or $content -notmatch '\bexpect\s*\(') {
        return [pscustomobject]@{ IsValid = $false; Error = 'Acceptance spec must contain Playwright test() and expect() calls.' }
    }
    if ($content -match '(?i)\b(?:playwright\.)?(?:chromium|webkit|firefox)\.launch\s*\(|\bnewContext\s*\(' -or
        $content -notmatch '(?is)test\s*\([\s\S]{0,500}?\{\s*page\b') {
        return [pscustomobject]@{ IsValid = $false; Error = 'Use the isolated page fixture supplied in the test callback; do not launch a browser or create a context.' }
    }
    $navigations = [regex]::Matches($content, '(?is)\b(?:page|browserPage)\s*\.\s*goto\s*\(([^)]*)\)')
    if ($navigations.Count -eq 0) {
        return [pscustomobject]@{ IsValid = $false; Error = "Acceptance spec must navigate with page.goto('/relative-route') using a relative local route." }
    }
    foreach ($match in $navigations) {
        $navigation = $match.Groups[1].Value.Trim()
        if ($navigation -notmatch '^''/(?!/)\S*''$') {
            return [pscustomobject]@{ IsValid = $false; Error = "Navigation must pass a relative local route beginning with '/'; found page.goto($navigation)." }
        }
    }
    if ($content -match '(?i)(?:/api/|/swagger(?:/|\b))') {
        $apiRoute = [regex]::Match($content, '(?is)\.route\s*\([\s\S]{0,200}(?:/api/|swagger)[\s\S]{0,700}?\.fulfill\s*\(')
        $firstNavigation = [regex]::Match($content, '(?is)\b(?:page|browserPage)\s*\.\s*goto\s*\(')
        if (-not $apiRoute.Success -or ($firstNavigation.Success -and $apiRoute.Index -gt $firstNavigation.Index)) {
            return [pscustomobject]@{ IsValid = $false; Error = 'Specs that exercise local APIs must register a matching page.route() handler and call route.fulfill() before navigation. Unmocked local APIs remain blocked.' }
        }
    }
    if ($content -match '(?i)\broute\.(?:continue|fallback|fetch)\s*\(') {
        return [pscustomobject]@{ IsValid = $false; Error = 'Acceptance spec may use only deterministic route.fulfill() or route.abort() mocks; network continuation and fetch are prohibited.' }
    }

    return [pscustomobject]@{ IsValid = $true; Error = $null }
}
