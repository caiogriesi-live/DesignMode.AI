param(
    [string]$TaskId = "orchestrator-test"
)

$ErrorActionPreference = "Stop"

# ==================================================
# Paths
# ==================================================

# Repository root = two levels above .agent/scripts
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot 'pipeline-helpers.ps1')

# Permanent repository-local QA environment
$PlaywrightRoot = Join-Path $RepoRoot ".agent/qa"

$QaPrompt = Join-Path $RepoRoot ".agent/prompts/qa.md"
$Task = Join-Path $RepoRoot ".agent/tasks/approved/$TaskId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/$TaskId"

$DevelopmentResult = Join-Path $ResultDir "development.md"
$QaResult = Join-Path $ResultDir "qa.md"
$CodexOutput = Join-Path $ResultDir "qa-codex-output.txt"
$EvidenceSummary = Join-Path $ResultDir "qa-evidence-summary.txt"

# ==================================================
# Resolve implementation workspace
# ==================================================

$DevelopmentText = Get-Content -LiteralPath $DevelopmentResult -Raw

$DevelopmentBranch = Get-PipelineStageResultField -Text $DevelopmentText -FieldName 'BRANCH' -ResultName "Developer result for '$TaskId'"
$ImplementationRoot = $null
$CurrentWorktree = $null

foreach ($Line in @(git -C $RepoRoot worktree list --porcelain)) {

    if ($Line -match '^worktree\s+(.+)$') {
        $CurrentWorktree = $Matches[1]
        continue
    }

    if ($Line -match '^branch\s+refs/heads/(.+)$') {
        if ($Matches[1] -eq $DevelopmentBranch) {
            $ImplementationRoot = $CurrentWorktree
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ImplementationRoot)) {
    throw "Could not locate implementation worktree for branch '$DevelopmentBranch'."
}

Write-Host "Implementation branch: $DevelopmentBranch"
Write-Host "Implementation root:   $ImplementationRoot"

$PlaywrightPreflight = Join-Path $ResultDir "playwright-preflight.txt"

$RestoreLog = Join-Path $ResultDir "restore.log"
$TestBuildLog = Join-Path $ResultDir "test-build.log"
$ClientBuildLog = Join-Path $ResultDir "client-build.log"
$ServerBuildLog = Join-Path $ResultDir "server-build.log"
$TestLog = Join-Path $ResultDir "tests.log"
$DiffCheckLog = Join-Path $ResultDir "diff-check.log"
$AcceptanceLog = Join-Path $ResultDir "acceptance.log"
$ClientHostLog = Join-Path $ResultDir "client-host.log"
$ClientHostErrorLog = Join-Path $ResultDir "client-host-error.log"

$ClientHostUrl = "http://127.0.0.1:5276"
$ClientTestUrl = $ClientHostUrl

$ClientProject = Join-Path $ImplementationRoot "DesignMode.AI.Client/DesignMode.AI.Client.csproj"
$ServerProject = Join-Path $ImplementationRoot "DesignMode.AI.Server/DesignMode.AI.Server.csproj"
$TestProject = Join-Path $ImplementationRoot "DesignMode.AI.Test/DesignMode.AI.Test.csproj"

$AcceptanceSpec = $null
$ImplementationAcceptanceSpec = $null
$TaskText = Get-Content -LiteralPath $Task -Raw
$FeatureDiff = git -C $ImplementationRoot diff master-caio...HEAD -- . 2>&1 | Out-String
$AcceptanceApplicable = Test-PipelineAcceptanceApplicable -TaskText $TaskText -DiffText $FeatureDiff
$GeneratedAcceptanceSpec = Join-Path $PlaywrightRoot "generated-$TaskId.spec.cjs"
$AcceptanceConfig = Join-Path $PlaywrightRoot "generated-$TaskId.config.cjs"
$AcceptanceBrowserEvidence = Join-Path $ResultDir 'browser-evidence'
$AcceptanceGenerationOutput = Join-Path $ResultDir "acceptance-generation-output.txt"

Write-Host ""
Write-Host "DESIGNMODE AUTO QA"
Write-Host "=================="
Write-Host "Task: $TaskId"
Write-Host ""

# ==================================================
# Preconditions
# ==================================================

if (!(Test-Path $QaPrompt)) {
    throw "QA prompt not found: $QaPrompt"
}

if (!(Test-Path $Task)) {
    throw "Approved task not found: $Task"
}

if (!(Test-Path $DevelopmentResult)) {
    throw "Developer result not found: $DevelopmentResult"
}

$ImplementationTask = Join-Path $ImplementationRoot ".agent/tasks/approved/$TaskId.md"
Copy-PipelineArtifact -Source $Task -Destination $ImplementationTask | Out-Null
Copy-PipelineArtifact -Source $DevelopmentResult -Destination (Join-Path (Join-Path $ImplementationRoot ".agent/results/$TaskId") (Split-Path $DevelopmentResult -Leaf)) | Out-Null

if (!(Test-Path $ClientProject)) {
    throw "Client project not found: $ClientProject"
}

if (!(Test-Path $ServerProject)) {
    throw "Server project not found: $ServerProject"
}

if (!(Test-Path $TestProject)) {
    throw "Test project not found: $TestProject"
}

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

# ==================================================
# Helper: execute deterministic command
# ==================================================

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [string]$WorkingDirectory = $ImplementationRoot
    )

    Write-Host ""
    Write-Host "--------------------------------------------------"
    Write-Host $Name
    Write-Host "--------------------------------------------------"

    $OriginalLocation = Get-Location

    New-Item -ItemType File -Force -Path $LogPath | Out-Null

    try {
        Set-Location $WorkingDirectory

        $global:LASTEXITCODE = 0

        & $Command 2>&1 |
            Tee-Object -FilePath $LogPath |
            Out-Host

        $ExitCode = $LASTEXITCODE

        if ($null -eq $ExitCode) {
            $ExitCode = 0
        }
    }
    catch {
        ($_ | Out-String) |
            Tee-Object -FilePath $LogPath -Append |
            Out-Host

        $ExitCode = 1
    }
    finally {
        Set-Location $OriginalLocation
    }

    Write-Host ""
    Write-Host "$Name exit code: $ExitCode"

    return [int]$ExitCode
}

# ==================================================
# Playwright infrastructure discovery
# ==================================================

Write-Host ""
Write-Host "Checking Playwright infrastructure..."
Write-Host ""

$NodePath = $null
$NpxPath = $null

$PackageJson = Join-Path $PlaywrightRoot "package.json"

$NodeCommand = Get-Command node -ErrorAction SilentlyContinue

if ($NodeCommand) {
    $NodePath = $NodeCommand.Source
}

$NpxCommand = Get-Command npx -ErrorAction SilentlyContinue

if ($NpxCommand) {
    $NpxPath = $NpxCommand.Source
}

$PackageJsonExists = Test-Path $PackageJson

$PlaywrightDeclared = $false
$PlaywrightPackage = "NONE"

if ($PackageJsonExists) {
    try {
        $Package = Get-Content $PackageJson -Raw |
            ConvertFrom-Json

        if (
            $Package.devDependencies -and
            $Package.devDependencies.PSObject.Properties.Name -contains "@playwright/test"
        ) {
            $PlaywrightDeclared = $true
            $PlaywrightPackage = "@playwright/test"
        }
        elseif (
            $Package.dependencies -and
            $Package.dependencies.PSObject.Properties.Name -contains "@playwright/test"
        ) {
            $PlaywrightDeclared = $true
            $PlaywrightPackage = "@playwright/test"
        }
        elseif (
            $Package.devDependencies -and
            $Package.devDependencies.PSObject.Properties.Name -contains "playwright"
        ) {
            $PlaywrightDeclared = $true
            $PlaywrightPackage = "playwright"
        }
        elseif (
            $Package.dependencies -and
            $Package.dependencies.PSObject.Properties.Name -contains "playwright"
        ) {
            $PlaywrightDeclared = $true
            $PlaywrightPackage = "playwright"
        }
    }
    catch {
        Write-Warning "Could not parse package.json: $($_.Exception.Message)"
    }
}

$LocalPlaywrightTest =
    Join-Path $PlaywrightRoot "node_modules/@playwright/test"

$LocalPlaywrightCore =
    Join-Path $PlaywrightRoot "node_modules/playwright"

$LocalPlaywrightPresent =
    (Test-Path $LocalPlaywrightTest) -or
    (Test-Path $LocalPlaywrightCore)

$NpmInstallStatus = "NOT_NEEDED"
if ($AcceptanceApplicable -and -not $LocalPlaywrightPresent -and $NodePath -and $PackageJsonExists -and (Test-Path (Join-Path $PlaywrightRoot "package-lock.json"))) {
    $NpmCommand = Get-Command npm -ErrorAction SilentlyContinue
    if ($NpmCommand) {
        $OriginalLocation = Get-Location
        try {
            Set-Location $PlaywrightRoot
            & $NpmCommand.Source ci --no-audit --no-fund
            $NpmInstallStatus = "EXIT_$LASTEXITCODE"
            $LocalPlaywrightPresent = (Test-Path $LocalPlaywrightTest) -or (Test-Path $LocalPlaywrightCore)
        } finally { Set-Location $OriginalLocation }
    } else { $NpmInstallStatus = "NPM_NOT_FOUND" }
} elseif ($AcceptanceApplicable -and -not $LocalPlaywrightPresent) { $NpmInstallStatus = "DEPENDENCIES_UNAVAILABLE" }
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $env:LOCALAPPDATA "ms-playwright"

# ==================================================
# Playwright CLI verification
# ==================================================

$PlaywrightCliWorks = $false
$PlaywrightVersion = "UNAVAILABLE"

if ($NpxPath -and $LocalPlaywrightPresent) {
    $OriginalLocation = Get-Location

    try {
        Set-Location $PlaywrightRoot

        $VersionOutput =
            & $NpxPath --no-install playwright --version 2>&1

        $VersionExitCode = $LASTEXITCODE

        if ($VersionExitCode -eq 0) {
            $PlaywrightCliWorks = $true
            $PlaywrightVersion =
                ($VersionOutput | Out-String).Trim()
        }
    }
    catch {
        $PlaywrightCliWorks = $false
    }
    finally {
        Set-Location $OriginalLocation
    }
}

# ==================================================
# Browser binary verification
# ==================================================

$ChromiumReady = $false
$WebKitReady = $false

$BrowserProbeStatus = "NOT_RUN"
$BrowserProbeOutput = ""

if ($NodePath -and $LocalPlaywrightPresent) {

    $ProbeScript = @'
const fs = require("fs");

let pw;

try {
    try {
        pw = require("playwright");
    }
    catch {
        pw = require("@playwright/test");
    }

    const chromiumPath = pw.chromium.executablePath();
    const webkitPath = pw.webkit.executablePath();

    console.log(
        JSON.stringify({
            chromiumPath,
            chromiumExists: fs.existsSync(chromiumPath),
            webkitPath,
            webkitExists: fs.existsSync(webkitPath)
        })
    );
}
catch (error) {
    console.error(
        JSON.stringify({
            error:
                error && error.stack
                    ? error.stack
                    : String(error)
        })
    );

    process.exit(1);
}
'@

    $OriginalLocation = Get-Location

    try {
        Set-Location $PlaywrightRoot

        $BrowserProbeOutput =
            $ProbeScript |
            & $NodePath - 2>&1

        $BrowserProbeExitCode = $LASTEXITCODE

        if ($BrowserProbeExitCode -eq 0) {

            $BrowserProbeJson =
                ($BrowserProbeOutput | Out-String).Trim() |
                ConvertFrom-Json

            $ChromiumReady =
                [bool]$BrowserProbeJson.chromiumExists

            $WebKitReady =
                [bool]$BrowserProbeJson.webkitExists

            $BrowserProbeStatus = "PASS"
        }
        else {
            $BrowserProbeStatus = "FAIL"
        }
    }
    catch {
        $BrowserProbeStatus = "FAIL"
        $BrowserProbeOutput = $_.Exception.Message
    }
    finally {
        Set-Location $OriginalLocation
    }
}


if ($AcceptanceApplicable -and $NodePath -and $NpxPath -and $LocalPlaywrightPresent -and (-not $ChromiumReady -or -not $WebKitReady)) {
    $OriginalLocation = Get-Location
    try {
        Set-Location $PlaywrightRoot
        & $NpxPath --no-install playwright install chromium webkit
        $BrowserInstallExit = $LASTEXITCODE
        if ($BrowserInstallExit -eq 0) {
            $BrowserProbeOutput = $ProbeScript | & $NodePath - 2>&1
            if ($LASTEXITCODE -eq 0) {
                $BrowserProbeJson = ($BrowserProbeOutput | Out-String).Trim() | ConvertFrom-Json
                $ChromiumReady = [bool]$BrowserProbeJson.chromiumExists
                $WebKitReady = [bool]$BrowserProbeJson.webkitExists
                $BrowserProbeStatus = "PASS_AFTER_INSTALL"
            }
        }
    } finally { Set-Location $OriginalLocation }
}

# ==================================================
# Write Playwright preflight
# ==================================================

$PlaywrightInfrastructureReady =
    $NodePath -and
    $NpxPath -and
    $LocalPlaywrightPresent -and
    $PlaywrightCliWorks -and
    $ChromiumReady -and
    $WebKitReady

$PreflightLines = @(

    "PLAYWRIGHT QA PREFLIGHT"
    "======================="
    ""

    "REPO_ROOT: $RepoRoot"
    "IMPLEMENTATION_ROOT: $ImplementationRoot"
    "IMPLEMENTATION_BRANCH: $DevelopmentBranch"
    "PLAYWRIGHT_ROOT: $PlaywrightRoot"

    "NODE: $(if ($NodePath) { $NodePath } else { 'NOT_FOUND' })"
    "NPX: $(if ($NpxPath) { $NpxPath } else { 'NOT_FOUND' })"

    "PACKAGE_JSON: $(if ($PackageJsonExists) { 'FOUND' } else { 'NOT_FOUND' })"

    "PLAYWRIGHT_DECLARED: $PlaywrightDeclared"
    "PLAYWRIGHT_PACKAGE: $PlaywrightPackage"

    "LOCAL_PLAYWRIGHT_PRESENT: $LocalPlaywrightPresent"
    "NPM_INSTALL_STATUS: $NpmInstallStatus"

    "PLAYWRIGHT_CLI_WORKS: $PlaywrightCliWorks"
    "PLAYWRIGHT_VERSION: $PlaywrightVersion"

    "BROWSER_PROBE: $BrowserProbeStatus"

    "CHROMIUM_READY: $ChromiumReady"
    "WEBKIT_READY: $WebKitReady"

    "PLAYWRIGHT_INFRASTRUCTURE_READY: $PlaywrightInfrastructureReady"
)

if ($BrowserProbeOutput) {

    $PreflightLines += ""
    $PreflightLines += "BROWSER_PROBE_OUTPUT:"
    $PreflightLines +=
        ($BrowserProbeOutput | Out-String).Trim()
}

$PreflightLines |
    Set-Content `
        -Path $PlaywrightPreflight `
        -Encoding UTF8

Get-Content $PlaywrightPreflight |
    ForEach-Object {
        Write-Host $_
    }

Write-Host ""

if ($PlaywrightInfrastructureReady) {
    Write-Host "Playwright infrastructure: READY"
}
else {
    Write-Host "Playwright infrastructure: INCOMPLETE"
}

# ==================================================
# Deterministic QA execution
#
# IMPORTANT:
#
# These stages intentionally run ONE AT A TIME.
#
# Do not parallelize Client build, Server build,
# tests, or browser acceptance.
# ==================================================

Write-Host ""
Write-Host "Starting deterministic QA execution..."
Write-Host ""

# --------------------------------------------------
# 1. Refresh generated .NET state and restore dependencies
# --------------------------------------------------

# Remove only generated bin/obj directories directly under these three projects.
$ProjectOutputRoots = @(
    (Join-Path $ImplementationRoot "DesignMode.AI.Client"),
    (Join-Path $ImplementationRoot "DesignMode.AI.Server"),
    (Join-Path $ImplementationRoot "DesignMode.AI.Test")
)

$ImplementationRootFull =
    [System.IO.Path]::GetFullPath($ImplementationRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

foreach ($ProjectRoot in $ProjectOutputRoots) {
    $ProjectRootFull = [System.IO.Path]::GetFullPath($ProjectRoot)
    if (-not $ProjectRootFull.StartsWith(
    $ImplementationRootFull,
    [System.StringComparison]::OrdinalIgnoreCase
)) {
        throw "Refusing to refresh generated output outside repository: $ProjectRootFull"
    }
    foreach ($GeneratedName in @("bin", "obj")) {
        $GeneratedPath = Join-Path $ProjectRootFull $GeneratedName
        if (Test-Path -LiteralPath $GeneratedPath) {
            $GeneratedFull = [System.IO.Path]::GetFullPath($GeneratedPath)
            if ((Split-Path -Leaf $GeneratedFull) -ne $GeneratedName -or (Split-Path -Parent $GeneratedFull) -ne $ProjectRootFull) {
                throw "Refusing to remove unexpected path: $GeneratedFull"
            }
            Remove-Item -LiteralPath $GeneratedFull -Recurse -Force
        }
    }
}

$RestoreExitCode = Invoke-LoggedCommand `
    -Name "DOTNET RESTORE" `
    -LogPath $RestoreLog `
    -Command {
        $RestoreCodes = @()
        foreach ($Project in @(
            "DesignMode.AI.Client/DesignMode.AI.Client.csproj",
            "DesignMode.AI.Server/DesignMode.AI.Server.csproj",
            "DesignMode.AI.Test/DesignMode.AI.Test.csproj"
        )) {
            dotnet restore $Project --nologo
            $RestoreCodes += [int]$LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { break }
        }
        if ($RestoreCodes | Where-Object { $_ -ne 0 }) { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 }
    }
$RESTORE_EXIT_CODE = [int]$RestoreExitCode

# 2. Client build
# --------------------------------------------------

$ClientBuildExit = Invoke-LoggedCommand `
    -Name "CLIENT BUILD" `
    -LogPath $ClientBuildLog `
    -Command {
        dotnet build `
            "DesignMode.AI.Client/DesignMode.AI.Client.csproj" `
            --no-restore `
            --nologo `
            -v:q
    }

# --------------------------------------------------
# 3. Server build
#
# Runs only after Client build has completely exited.
# --------------------------------------------------

$ServerBuildExit = Invoke-LoggedCommand `
    -Name "SERVER BUILD" `
    -LogPath $ServerBuildLog `
    -Command {
        dotnet build `
            "DesignMode.AI.Server/DesignMode.AI.Server.csproj" `
            --no-restore `
            --nologo `
            -v:q
    }

# --------------------------------------------------
# 4. Build the test project so --no-build tests use refreshed outputs.
# --------------------------------------------------

$TestBuildExit = Invoke-LoggedCommand `
    -Name "TEST PROJECT BUILD" `
    -LogPath $TestBuildLog `
    -Command {
        dotnet build `
            "DesignMode.AI.Test/DesignMode.AI.Test.csproj" `
            --no-restore `
            --nologo `
            -v:q
    }

# 5. Existing automated tests
# Runs against the test project build immediately above.
# --------------------------------------------------

$TestsExit = Invoke-LoggedCommand `
    -Name "AUTOMATED TESTS" `
    -LogPath $TestLog `
    -Command {
        dotnet test `
            "DesignMode.AI.Test/DesignMode.AI.Test.csproj" `
            --no-build `
            --no-restore `
            --nologo `
            --logger "console;verbosity=minimal"
    }

# --------------------------------------------------
# 4. Git diff check
# --------------------------------------------------

Write-Host ""
Write-Host "--------------------------------------------------"
Write-Host "GIT DIFF CHECK"
Write-Host "--------------------------------------------------"

$DiffCheckPreference = $ErrorActionPreference
try {
    # Windows PowerShell can promote Git's stderr line-ending warnings to
    # NativeCommandError records. Continue locally so Git's native exit code
    # remains authoritative while both output streams are logged and displayed.
    $ErrorActionPreference = "Continue"
    New-Item -ItemType File -Force -Path $DiffCheckLog | Out-Null
    $global:LASTEXITCODE = 0

    git diff --check master-caio -- . ':(exclude).agent/scripts/run-qa.ps1' 2>&1 |
        Tee-Object -FilePath $DiffCheckLog |
        Out-Host

    $DiffCheckExit = $LASTEXITCODE
    if ($null -eq $DiffCheckExit) {
        $DiffCheckExit = 0
    }
}
catch {
    ($_ | Out-String) |
        Tee-Object -FilePath $DiffCheckLog -Append |
        Out-Host
    $DiffCheckExit = 1
}
finally {
    $ErrorActionPreference = $DiffCheckPreference
}

Write-Host ""
Write-Host "GIT DIFF CHECK exit code: $DiffCheckExit"

if ($DiffCheckExit -eq 0 -and (Get-Item $DiffCheckLog).Length -eq 0) {
    "PASS - git diff --check produced no errors." |
        Set-Content -Path $DiffCheckLog -Encoding UTF8
}

# Generate task-specific Playwright acceptance

$AcceptanceRunMarker = "$TaskId-$([guid]::NewGuid().ToString('N'))"
$AcceptanceSpec = Join-Path $ResultDir "acceptance-$AcceptanceRunMarker.spec.cjs"
$ImplementationAcceptanceSpec = Join-Path $ImplementationRoot ".agent/results/$TaskId/acceptance-$AcceptanceRunMarker.spec.cjs"
$AcceptanceGenerationPrompt = @"
Generate browser acceptance for the CURRENT approved task only. Read exactly
.agent/tasks/approved/$TaskId.md and the current implementation at
$ImplementationRoot. Write exactly .agent/results/$TaskId/acceptance-$AcceptanceRunMarker.spec.cjs.
The first line must be this exact freshness marker: // acceptance-run: $AcceptanceRunMarker
Derive routes, selectors, scenarios, and expected behavior from that approved
file. Ignore unrelated tasks, experiments, fixtures, examples, and old specs.
<<<<<<< HEAD
Use CommonJS: const { test, expect } = require('./safe-playwright.cjs'); and use
its isolated { page } fixture in each test callback. Never import Playwright
directly, launch a browser, create a browser context, or hard-code an origin.
Pass a single-quoted relative path literal directly to every page.goto() call.
For example: await page.goto('/pipeline-validation');
Do not use route variables or constants, template strings, concatenation, absolute
URLs, protocol-relative URLs, or any other computed navigation arguments. The
route must begin with one '/' and must not begin with '//'.
For data-backed flows, register exact page.route() handlers before navigation
and fulfill local API responses with deterministic fixtures; never submit writes
to a live API. The shared fixture allows only the runner's temporary local app
origin and blocks all other network origins. Never contact production APIs,
financial systems, or external hosts. Specs that mention `/api/` or `/swagger`
must include explicit page.route() and route.fulfill() mocks. Unmocked local
API requests stay blocked by the runner.
=======
Use relative routes and import test/expect from './safe-playwright.cjs'. Use its
page fixture only; do not launch browsers manually. Write independent tests
runnable in Chromium desktop 1440x900 and WebKit mobile 390x844. For data-backed
flows, install deterministic route mocks before navigation and never submit
writes. The runner blocks all network origins except the temporary local client
host. Never contact production APIs, financial systems, or external hosts.
Use route.fulfill() or route.abort() for mocks; never use route.continue(),
route.fallback(), route.fetch(), or another network request from the spec.
When checking date filters, derive the test date from the UI default or set the
entry and filter dates to the same fixed fixture date so tests do not expire.
>>>>>>> codex/4-marketing-dashboard-work-04-20261008-181415
Do not mock, bypass, or claim verification of a server-side authorization
criterion from a client-side route mock. Keep unauthenticated and ordinary
customer denial checks in deterministic server tests that invoke the real
authorization boundary with isolated test data; if such evidence is absent,
report that criterion as unverified rather than passing it.
Do not edit app, task, package, lock, or other files. Do not install anything.
Write the spec and report its exact path and criteria; do not claim success if
no file was created.
"@

$AcceptanceGenerationExit = 1
if ($AcceptanceApplicable) {
    Remove-Item -LiteralPath $ImplementationAcceptanceSpec -Force -ErrorAction SilentlyContinue
    try {
        $AcceptanceGenerationPrompt | codex exec -C $ImplementationRoot --model "gpt-6-luna" --approve-for-me --output-last-message $AcceptanceGenerationOutput -
        $AcceptanceGenerationExit = $LASTEXITCODE
    } catch { $_ | Out-String | Set-Content -Path $AcceptanceGenerationOutput -Encoding UTF8 }
}

$AcceptanceSpecValid = -not $AcceptanceApplicable
$AcceptanceValidationError = $null
if ($AcceptanceApplicable) {
    if ($AcceptanceGenerationExit -ne 0) { $AcceptanceValidationError = "Generator exited $AcceptanceGenerationExit. See $AcceptanceGenerationOutput." }
    elseif (-not $NodePath) { $AcceptanceValidationError = 'Node.js is unavailable; configure Node.js and safely resume QA.' }
    else {
        $validation = Test-PipelineAcceptanceSpec -Path $ImplementationAcceptanceSpec -ExpectedRunMarker "// acceptance-run: $AcceptanceRunMarker"
        if (-not $validation.IsValid) { $AcceptanceValidationError = $validation.Error }
        else {
            Push-Location $ImplementationRoot
            try { & $NodePath --check $ImplementationAcceptanceSpec; if ($LASTEXITCODE -ne 0) { $AcceptanceValidationError = 'Node syntax validation failed. Fix the spec before resuming.' } }
            finally { Pop-Location }
        }
    }
    if (-not $AcceptanceValidationError) {
        Copy-PipelineArtifact -Source $ImplementationAcceptanceSpec -Destination $AcceptanceSpec | Out-Null
        Copy-PipelineArtifact -Source $AcceptanceSpec -Destination $GeneratedAcceptanceSpec | Out-Null
        $AcceptanceSpecValid = $true
    } else {
        "ACCEPTANCE SPEC VALIDATION FAILED`r`n`r`n$AcceptanceValidationError" | Set-Content -LiteralPath $AcceptanceLog -Encoding UTF8
        Write-Host "ERROR: $AcceptanceValidationError"
    }
}
# ==================================================
# 5. Browser acceptance
#
# Start Client host -> wait for readiness -> run
# Playwright -> ALWAYS stop the temporary host.
# ==================================================

$AcceptanceExit = 999
$ClientHostProcess = $null
$ClientHostReady = $false
$PreviousAllowedOrigin = $env:DESIGNMODE_QA_ALLOWED_ORIGIN

if (!$AcceptanceApplicable) {
    "BROWSER ACCEPTANCE: NOT_APPLICABLE - no browser-observable UI or HTTP behavior changed." | Set-Content -Path $AcceptanceLog -Encoding UTF8
    $AcceptanceExit = 0
    Write-Host "Browser acceptance: NOT APPLICABLE"
}
elseif (!$PlaywrightInfrastructureReady) {
    @"
BROWSER ACCEPTANCE NOT RUN

Reason:
Playwright infrastructure preflight did not pass.

PLAYWRIGHT_INFRASTRUCTURE_READY:
$PlaywrightInfrastructureReady

CHROMIUM_READY:
$ChromiumReady

WEBKIT_READY:
$WebKitReady
"@ | Set-Content -Path $AcceptanceLog -Encoding UTF8

    $AcceptanceExit = 996

    Write-Host ""
    Write-Host "Browser acceptance: BLOCKED"
}
elseif (!$AcceptanceSpecValid -or !(Test-Path $GeneratedAcceptanceSpec)) {
    @"
BROWSER ACCEPTANCE NOT RUN

Reason:
Task-specific acceptance spec was not generated or did not validate.

Expected:
$AcceptanceSpec
"@ | Set-Content -Path $AcceptanceLog -Encoding UTF8

    if ($AcceptanceValidationError) {
        Add-Content -LiteralPath $AcceptanceLog -Value "`r`nDIAGNOSTIC:`r`n$AcceptanceValidationError"
    }
    $AcceptanceExit = 995

    Write-Host ""
    Write-Host "Browser acceptance: SPEC NOT FOUND"
}
else {
    Write-Host ""
    Write-Host "--------------------------------------------------"
    Write-Host "START CLIENT QA HOST"
    Write-Host "--------------------------------------------------"

    Remove-Item $ClientHostLog -Force -ErrorAction SilentlyContinue
    Remove-Item $ClientHostErrorLog -Force -ErrorAction SilentlyContinue

    try {
        $ClientHostProcess = Start-Process `
            -FilePath "dotnet" `
            -ArgumentList @(
                "run",
                "--project",
                $ClientProject,
                "--no-build",
                "--no-launch-profile",
                "--",
                "--urls",
                $ClientHostUrl
            ) `
            -WorkingDirectory $ImplementationRoot `
            -RedirectStandardOutput $ClientHostLog `
            -RedirectStandardError $ClientHostErrorLog `
            -PassThru `
            -WindowStyle Hidden

        Write-Host "Client host PID: $($ClientHostProcess.Id)"
        Write-Host "Waiting for: $ClientTestUrl"

        $Deadline = (Get-Date).AddSeconds(60)

        while ((Get-Date) -lt $Deadline) {
            if ($ClientHostProcess.HasExited) {
                Write-Host ""
                Write-Host "Client host exited before becoming ready."
                break
            }

            try {
                $Response = Invoke-WebRequest `
                    -Uri $ClientTestUrl `
                    -Method Get `
                    -UseBasicParsing `
                    -TimeoutSec 5 `
                    -ErrorAction Stop

                if ($Response.StatusCode -ge 200 -and $Response.StatusCode -lt 500) {
                    $ClientHostReady = $true
                    break
                }
            }
            catch {
                try {
                    $RootResponse = Invoke-WebRequest `
                        -Uri $ClientHostUrl `
                        -Method Get `
                        -UseBasicParsing `
                        -TimeoutSec 5 `
                        -ErrorAction Stop

                    if ($RootResponse.StatusCode -ge 200 -and $RootResponse.StatusCode -lt 500) {
                        $ClientHostReady = $true
                        break
                    }
                }
                catch {
                    # Host is not ready yet.
                }
            }

            Start-Sleep -Seconds 1
        }

        if (!$ClientHostReady) {
            @"
BROWSER ACCEPTANCE NOT RUN

Reason:
The local Client QA host did not become reachable.

EXPECTED_URL:
$ClientTestUrl

HOST_PID:
$(if ($ClientHostProcess) { $ClientHostProcess.Id } else { "NOT_STARTED" })

HOST_EXITED:
$(if ($ClientHostProcess) { $ClientHostProcess.HasExited } else { "UNKNOWN" })

See:
.agent/results/$TaskId/client-host.log
.agent/results/$TaskId/client-host-error.log
"@ | Set-Content -Path $AcceptanceLog -Encoding UTF8

            $AcceptanceExit = 998

            Write-Host ""
            Write-Host "Browser acceptance: CLIENT HOST NOT READY"
        }
    else {
        Write-Host ""
        Write-Host "Client QA host: READY"

        Copy-PipelineArtifact -Source (Join-Path $PSScriptRoot 'safe-playwright.cjs') -Destination (Join-Path $PlaywrightRoot 'safe-playwright.cjs') | Out-Null
        $networkPolicyLog = Join-Path $ResultDir 'network-policy-tests.log'
        $networkPolicyExit = Invoke-LoggedCommand -Name 'PLAYWRIGHT NETWORK POLICY REGRESSION TESTS' -LogPath $networkPolicyLog -WorkingDirectory $PlaywrightRoot -Command {
            & $NodePath --test network-policy.test.cjs
        }
        if ($networkPolicyExit -ne 0) {
            throw "Browser network-policy regression tests failed; see $networkPolicyLog."
        }

        $escapedBaseUrl = ConvertTo-Json -InputObject $ClientHostUrl -Compress
        $escapedOutputDir = ConvertTo-Json -InputObject $AcceptanceBrowserEvidence -Compress
        @"
const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({
  testDir: __dirname,
  testMatch: 'generated-$TaskId.spec.cjs',
  outputDir: $escapedOutputDir,
  reporter: 'list',
  use: { baseURL: $escapedBaseUrl },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium', viewport: { width: 1440, height: 900 } } },
    { name: 'webkit', use: { browserName: 'webkit', viewport: { width: 390, height: 844 }, isMobile: true } }
  ]
});
"@ | Set-Content -LiteralPath $AcceptanceConfig -Encoding UTF8
        $env:DESIGNMODE_QA_ALLOWED_ORIGIN = $ClientHostUrl

        $PlaywrightPreference = $ErrorActionPreference

        try {
            # Windows PowerShell can promote native stderr output from npx/Playwright
            # to NativeCommandError records. Let the native process exit code remain
            # authoritative while Invoke-LoggedCommand captures the output.
            $ErrorActionPreference = "Continue"

            $desktopLog = Join-Path $ResultDir 'acceptance-chromium.log'
            $mobileLog = Join-Path $ResultDir 'acceptance-webkit.log'
            $desktopExit = Invoke-LoggedCommand -Name 'PLAYWRIGHT CHROMIUM DESKTOP 1440x900' -LogPath $desktopLog -WorkingDirectory $PlaywrightRoot -Command {
                & $NpxPath --no-install playwright test --config $AcceptanceConfig --project=chromium --workers=1
            }
            $mobileExit = Invoke-LoggedCommand -Name 'PLAYWRIGHT WEBKIT MOBILE 390x844' -LogPath $mobileLog -WorkingDirectory $PlaywrightRoot -Command {
                & $NpxPath --no-install playwright test --config $AcceptanceConfig --project=webkit --workers=1
            }
            @("CHROMIUM_EXIT_CODE: $desktopExit", (Get-Content -LiteralPath $desktopLog -Raw), "WEBKIT_EXIT_CODE: $mobileExit", (Get-Content -LiteralPath $mobileLog -Raw)) | Set-Content -LiteralPath $AcceptanceLog -Encoding UTF8
            $AcceptanceExit = if ($desktopExit -eq 0 -and $mobileExit -eq 0) { 0 } else { 1 }
        }
        finally {
            $ErrorActionPreference = $PlaywrightPreference
        }
        }
    }
    catch {
        $_ |
            Out-String |
            Set-Content -Path $AcceptanceLog -Encoding UTF8

        $AcceptanceExit = 997

        Write-Host ""
        Write-Host "Browser acceptance infrastructure failed:"
        Write-Host $_
    }
    finally {
        Remove-Item -LiteralPath $GeneratedAcceptanceSpec -Force -ErrorAction SilentlyContinue
        if ($ClientHostProcess) {
            try {
                if (!$ClientHostProcess.HasExited) {
                    Write-Host ""
                    Write-Host "Stopping Client QA host..."
                    Write-Host "PID: $($ClientHostProcess.Id)"

                    Stop-Process `
                        -Id $ClientHostProcess.Id `
                        -Force `
                        -ErrorAction Stop

                    $ClientHostProcess.WaitForExit()
                }

                Write-Host "Client QA host stopped."
            }
            catch {
                Write-Warning "Could not stop Client QA host PID $($ClientHostProcess.Id): $($_.Exception.Message)"
            }
        }
    }
}

# Always remove the temporary Playwright copy, including preflight-blocked runs.
Remove-Item -LiteralPath $GeneratedAcceptanceSpec -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $AcceptanceConfig -Force -ErrorAction SilentlyContinue
if ($null -eq $PreviousAllowedOrigin) { Remove-Item Env:DESIGNMODE_QA_ALLOWED_ORIGIN -ErrorAction SilentlyContinue }
else { $env:DESIGNMODE_QA_ALLOWED_ORIGIN = $PreviousAllowedOrigin }

# ==================================================
# Deterministic evidence summary
# ==================================================

$EvidenceSummary =
    Join-Path $ResultDir "qa-evidence-summary.txt"

$SummaryLines = @(

    "DESIGNMODE DETERMINISTIC QA EVIDENCE"
    "===================================="
    ""

    "TASK: $TaskId"
    ""

    "PLAYWRIGHT_INFRASTRUCTURE_READY: $PlaywrightInfrastructureReady"
    "CHROMIUM_READY: $ChromiumReady"
    "WEBKIT_READY: $WebKitReady"
    ""

    "RESTORE_EXIT_CODE: $RESTORE_EXIT_CODE"
    "CLIENT_BUILD_EXIT_CODE: $ClientBuildExit"
    "TEST_PROJECT_BUILD_EXIT_CODE: $TestBuildExit"
    "SERVER_BUILD_EXIT_CODE: $ServerBuildExit"
    "TEST_EXIT_CODE: $TestsExit"
    "DIFF_CHECK_EXIT_CODE: $DiffCheckExit"
    "ACCEPTANCE_EXIT_CODE: $AcceptanceExit",
    "ACCEPTANCE_APPLICABLE: $AcceptanceApplicable"
    ""

    "CLIENT_BUILD_LOG: .agent/results/$TaskId/client-build.log"
    "SERVER_BUILD_LOG: .agent/results/$TaskId/server-build.log"
    "TEST_LOG: .agent/results/$TaskId/tests.log"
    "DIFF_CHECK_LOG: .agent/results/$TaskId/diff-check.log"
    "ACCEPTANCE_LOG: .agent/results/$TaskId/acceptance.log"
    "CLIENT_HOST_LOG: .agent/results/$TaskId/client-host.log"
    "CLIENT_HOST_ERROR_LOG: .agent/results/$TaskId/client-host-error.log"
    "PLAYWRIGHT_PREFLIGHT: .agent/results/$TaskId/playwright-preflight.txt"
    "ACCEPTANCE_SPEC: .agent/results/$TaskId/$(Split-Path -Leaf $AcceptanceSpec)"
    "ACCEPTANCE_CHROMIUM_LOG: .agent/results/$TaskId/acceptance-chromium.log"
    "ACCEPTANCE_WEBKIT_LOG: .agent/results/$TaskId/acceptance-webkit.log"
    "ACCEPTANCE_BROWSER_EVIDENCE: .agent/results/$TaskId/browser-evidence"
    "ACCEPTANCE_GENERATION_EXIT_CODE: $AcceptanceGenerationExit"
)

function Add-BoundedLogSummary {
    param([string]$Label, [string]$Path, [int]$MaxLines = 24, [int]$MaxLineChars = 700)
    $script:SummaryLines += ""
    $script:SummaryLines += "[$Label]"
    if (!(Test-Path -LiteralPath $Path)) {
        $script:SummaryLines += "(not created)"
        return
    }
    $lines = @(Get-Content -LiteralPath $Path -Tail $MaxLines -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) {
        $script:SummaryLines += "(empty)"
        return
    }
    foreach ($line in $lines) {
        $clean = [string]$line
        if ($clean.Length -gt $MaxLineChars) { $clean = $clean.Substring(0, $MaxLineChars) + "…[line truncated]" }
        $script:SummaryLines += $clean
    }
}

Add-BoundedLogSummary "PREFLIGHT (bounded)" $PlaywrightPreflight 50 500
Add-BoundedLogSummary "RESTORE (last lines)" $RestoreLog
Add-BoundedLogSummary "CLIENT BUILD (last lines)" $ClientBuildLog
Add-BoundedLogSummary "TEST PROJECT BUILD (last lines)" $TestBuildLog
Add-BoundedLogSummary "SERVER BUILD (last lines)" $ServerBuildLog
Add-BoundedLogSummary "TESTS (last lines)" $TestLog
Add-BoundedLogSummary "DIFF CHECK (last lines)" $DiffCheckLog
Add-BoundedLogSummary "ACCEPTANCE (last lines)" $AcceptanceLog
Add-BoundedLogSummary "CLIENT HOST (last lines)" $ClientHostLog
Add-BoundedLogSummary "CLIENT HOST ERRORS (last lines)" $ClientHostErrorLog

# A hard character cap protects the classifier context even if log lines are unusual.
$SummaryText = $SummaryLines -join [Environment]::NewLine
if ($SummaryText.Length -gt 250000) {
    $SummaryText = $SummaryText.Substring(0, 249900) + "`r`n[TRUNCATED]"
}
$SummaryLines = @($SummaryText -split "`r?`n")

Set-Content -LiteralPath $EvidenceSummary -Value $SummaryText -Encoding UTF8

Write-Host ""
Write-Host "Deterministic QA evidence:"
Get-Content $EvidenceSummary |
    ForEach-Object {
        Write-Host $_
    }

# ==================================================
# QA Agent prompt
#
# Codex evaluates evidence AFTER deterministic
# execution has completed.
#
# It should not rerun the deterministic pipeline.
# ==================================================

$Prompt = @"
Act as the independent QA Agent defined in:

.agent/prompts/qa.md

Authoritative task:

.agent/tasks/approved/$TaskId.md

Developer result:

.agent/results/$TaskId/development.md

The DesignMode QA orchestrator has ALREADY executed the deterministic QA
commands sequentially.

Do NOT rerun the Client build.
Do NOT rerun the Server build.
Do NOT rerun the full automated test suite.
Do NOT rerun Playwright acceptance unless the supplied evidence is missing,
corrupt, or internally inconsistent.
Do NOT run these stages in parallel.

Your job is to independently inspect and classify the evidence already
collected.

Evidence:

.agent/results/$TaskId/qa-evidence-summary.txt

The compact evidence summary contains scalar exit codes, paths, bounded
preflight data, and short relevant log tails. Use that summary and small
relevant evidence only. Do not open, inspect, or request the full raw
client/server build, test, diff, acceptance, or host logs. Raw logs remain
available on disk for human triage and are not part of your input.

Deterministic results:

RESTORE_EXIT_CODE: $RESTORE_EXIT_CODE
CLIENT_BUILD_EXIT_CODE: $ClientBuildExit
TEST_PROJECT_BUILD_EXIT_CODE: $TestBuildExit
SERVER_BUILD_EXIT_CODE: $ServerBuildExit
TEST_EXIT_CODE: $TestsExit
DIFF_CHECK_EXIT_CODE: $DiffCheckExit
ACCEPTANCE_EXIT_CODE: $AcceptanceExit
ACCEPTANCE_APPLICABLE: $AcceptanceApplicable

PLAYWRIGHT_INFRASTRUCTURE_READY: $PlaywrightInfrastructureReady
CHROMIUM_READY: $ChromiumReady
WEBKIT_READY: $WebKitReady

Inspect the current Git branch and implementation diff as needed.

Do not trust the Developer Agent's conclusion that the feature works.

Do not automatically attribute existing test failures to this feature.
If test failures cannot be causally classified by QA alone, record them
accurately for the Triage Agent.

If restore fails before compilation due NuGet.Config access denial, package source authentication, or service connectivity, classify it as TEST_INFRASTRUCTURE. Do not record it as a feature regression.

For browser acceptance distinguish between:

- feature acceptance failure
- browser/test infrastructure failure
- successful runtime verification

For any approved criterion requiring server-side authorization, browser mocks
are not evidence. Confirm deterministic server tests invoke the real authorization
boundary and reject both unauthenticated and ordinary customer requests. If
those tests/evidence do not exist, mark the authorization criterion unverified
and do not report feature QA PASS.

Write the structured QA result to:

.agent/results/$TaskId/qa.md

Include at minimum:

STATUS:
FAILED_CRITERIA:
ACCEPTANCE:
DESKTOP_PLAYWRIGHT:
MOBILE_WEBKIT:
AUTHORIZATION_TESTS:
CLIENT_BUILD:
SERVER_BUILD:
EXISTING_TEST_FAILURES:
CONSOLE_ERRORS:
NETWORK_ERRORS:
REPAIR_REQUIRED:
NOTES:

If browser acceptance and relevant deterministic verification pass but existing
unclassified tests fail:

STATUS: PASS_WITH_TEST_FAILURES_PENDING_TRIAGE

If everything relevant passes:

STATUS: PASS

If actual feature acceptance fails:

STATUS: FAIL

Use ACCEPTANCE: PASS only when every applicable browser criterion passed. When
browser acceptance is not applicable, write ACCEPTANCE: NOT APPLICABLE. Set each
browser field to PASS only when its current run completed successfully; report
FAIL or BLOCKED otherwise. When the approved task includes server authorization,
set AUTHORIZATION_TESTS: PASS only after deterministic tests exercise the real
authorization boundary for both unauthenticated and ordinary-customer requests
with isolated dependencies.

Do NOT modify application code.
Do NOT repair failures.
Do NOT weaken acceptance criteria.
Do NOT change the authoritative task.
Do NOT install packages.
Do NOT install browser binaries.
Do NOT modify package.json or lock files.
Do NOT push.
Do NOT merge.
Do NOT deploy.

You are being executed non-interactively by the DesignMode autonomous
engineering orchestrator.

Do not wait for human instructions.
"@

# The agent runs in the implementation worktree. Publish the current deterministic
# evidence there, and remove any prior report so a failed generation cannot reuse it.
$WorktreeResultDir = Join-Path $ImplementationRoot ".agent/results/$TaskId"
New-Item -ItemType Directory -Force -Path $WorktreeResultDir | Out-Null
$WorktreeQaResult = Join-Path $WorktreeResultDir 'qa.md'
if (Test-Path -LiteralPath $WorktreeQaResult -PathType Leaf) { Remove-Item -LiteralPath $WorktreeQaResult -Force }
if (Test-Path -LiteralPath $QaResult -PathType Leaf) { Remove-Item -LiteralPath $QaResult -Force }
Copy-PipelineArtifact -Source $EvidenceSummary -Destination (Join-Path $WorktreeResultDir 'qa-evidence-summary.txt') | Out-Null
# ==================================================
# Execute QA classification agent
# ==================================================

Write-Host ""
Write-Host "Starting QA classification Agent..."
Write-Host ""

$Prompt | codex exec `
    -C $ImplementationRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $CodexOutput `
    -

$CodexExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Codex exit code: $CodexExitCode"

if ($CodexExitCode -ne 0) {
    Write-Host ""
    Write-Host "QA AGENT EXECUTION FAILED"
    exit $CodexExitCode
}

if (Test-Path -LiteralPath $WorktreeQaResult -PathType Leaf) {
    Copy-PipelineArtifact -Source $WorktreeQaResult -Destination $QaResult | Out-Null
    Write-Host "Copied current QA result from implementation worktree:"
    Write-Host $WorktreeQaResult
}
elseif (!(Test-Path -LiteralPath $QaResult -PathType Leaf)) {
    Write-Host ""
    Write-Host "ERROR: Codex completed but QA result was not found in either location:"
    Write-Host $QaResult
    Write-Host $WorktreeQaResult
    exit 2
}

# ==================================================
# Validate structured QA result
# ==================================================

$QaContent = Get-Content $QaResult -Raw

$RequiredFields = @(
    "STATUS:",
    "FAILED_CRITERIA:",
    "ACCEPTANCE:",
    "DESKTOP_PLAYWRIGHT:",
    "MOBILE_WEBKIT:",
    "AUTHORIZATION_TESTS:",
    "CLIENT_BUILD:",
    "SERVER_BUILD:",
    "EXISTING_TEST_FAILURES:",
    "CONSOLE_ERRORS:",
    "NETWORK_ERRORS:",
    "REPAIR_REQUIRED:",
    "NOTES:"
)

$MissingFields = @()

foreach ($Field in $RequiredFields) {
    if ($QaContent -notmatch "(?m)^$([regex]::Escape($Field))") {
        $MissingFields += $Field
    }
}

if ($MissingFields.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: QA result is missing required fields:"

    foreach ($Field in $MissingFields) {
        Write-Host "  $Field"
    }

    exit 3
}

# ==================================================
# Final report
# ==================================================

Write-Host ""
Write-Host "========================================"
Write-Host "QA STAGE COMPLETE"
Write-Host "========================================"

Write-Host ""
Write-Host "QA result:"
Write-Host $QaResult

Write-Host ""
Write-Host "Evidence summary:"
Write-Host $EvidenceSummary

Write-Host ""
Write-Host "Playwright preflight:"
Write-Host $PlaywrightPreflight

Write-Host ""
Write-Host "Deterministic exit codes:"
Write-Host "  Restore: $RESTORE_EXIT_CODE"
Write-Host "  Client build: $ClientBuildExit"
Write-Host "  Test project build: $TestBuildExit"
Write-Host "  Server build: $ServerBuildExit"
Write-Host "  Tests: $TestsExit"
Write-Host "  Diff check: $DiffCheckExit"
Write-Host "  Acceptance: $AcceptanceExit"

Write-Host ""
Write-Host "Nothing pushed."
Write-Host "Nothing merged."
Write-Host "Nothing deployed."
