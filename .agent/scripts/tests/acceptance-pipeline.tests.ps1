$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\pipeline-helpers.ps1')

$temp = Join-Path (Get-Location).Path ('.acceptance-pipeline-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    $networkGuard = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\safe-playwright.cjs') -Raw
<<<<<<< HEAD
    if ($networkGuard -notmatch 'deniedApplicationRequests\.push' -or $networkGuard -notmatch "route\.abort\('blockedbyclient'\)" -or $networkGuard -notmatch 'DESIGNMODE_QA_ALLOWED_ORIGIN' -or $networkGuard -notmatch 'classifyRequest') {
        throw 'Browser fixture no longer blocks unapproved network origins.'
=======
    if ($networkGuard -notmatch 'classifyRequest' -or $networkGuard -notmatch 'blockedOptionalRequests\.push' -or $networkGuard -notmatch 'deniedApplicationRequests\.push' -or $networkGuard -notmatch "route\.abort\('blockedbyclient'\)" -or $networkGuard -notmatch 'DESIGNMODE_QA_ALLOWED_ORIGIN') {
        throw 'Browser fixture no longer classifies and blocks external requests while preserving optional blocked requests as non-failures.'
    }
    $qaRunner = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\run-qa.ps1') -Raw
    if ($qaRunner -notmatch 'PLAYWRIGHT NETWORK POLICY REGRESSION TESTS' -or $qaRunner -notmatch 'network-policy\.test\.cjs') {
        throw 'QA runner does not execute the shared network-policy regression tests before browser acceptance.'
>>>>>>> codex/4-marketing-dashboard-work-04-20261008-181415
    }
    $qaFixture = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '..\qa\safe-playwright.cjs') -Raw
    if ($networkGuard -ne $qaFixture) { throw 'The runner fixture source and QA fixture have drifted.' }
    $qaPrompt = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '..\prompts\qa.md') -Raw
    $qaRunner = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'run-qa.ps1') -Raw
    if ($qaRunner -notmatch 'single-quoted relative path literal directly to every page\.goto\(\) call' -or
        $qaRunner -notmatch "await page\.goto\('/pipeline-validation'\)" -or
        $qaRunner -notmatch 'Do not use route variables or constants' -or
        $qaRunner -notmatch 'template strings,\s*concatenation,\s*absolute\s+URLs,\s*protocol-relative URLs' -or
        $qaRunner -notmatch 'computed navigation arguments' -or
        $qaRunner -notmatch 'page\.route\(\)' -or $qaRunner -notmatch 'route\.fulfill\(\)' -or
        $qaRunner -notmatch 'Test-PipelineAcceptanceSpec') {
        throw 'The shared QA generation prompt or pre-browser validation no longer requires safe routes and local API mocks.'
    }
    if ($qaPrompt -notmatch '(?i)Playwright acceptance tests') { throw 'The independent QA prompt no longer requires browser acceptance.' }

    $triagePrompt = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\run-triage.ps1') -Raw
    $taskRunner = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\run-task.ps1') -Raw
    if ($triagePrompt -notmatch '(?i)feature acceptance passes' -or $triagePrompt -notmatch '(?i)BRANCH_REGRESSIONS is 0' -or $triagePrompt -notmatch '(?i)NEXT_ACTION:\s*REGRESSION') {
        throw 'Triage policy no longer routes passing acceptance with zero branch regressions to Regression.'
    }
    if ($taskRunner -notmatch [regex]::Escape("'REGRESSION' { `$NeedRepair = `$false }")) {
        throw 'Task runner does not automatically advance a Regression triage action.'
    }

    foreach ($taskId in @('marketing-dashboard-work-04', 'simulated-dashboard-refresh', 'simulated-api-report')) {
        $spec = Join-Path $temp "$taskId.spec.cjs"
        $marker = "$taskId-run-current"
        $valid = @"
// acceptance-run: $marker
const { test, expect } = require('./safe-playwright.cjs');
const nameClaim = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name';
void nameClaim;
test('local task acceptance', async ({ page }) => { await page.goto('/task-route'); await expect(page.locator('main')).toBeVisible(); });
"@
        Set-Content -LiteralPath $spec -Value $valid
        $result = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if (-not $result.IsValid) { throw "$taskId valid generated spec rejected: $($result.Error)" }
        Set-Content -LiteralPath $spec -Value $valid.Replace("require('./safe-playwright.cjs')", 'require("./safe-playwright.cjs")')
        $doubleQuotedFixture = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if (-not $doubleQuotedFixture.IsValid) { throw "$taskId valid double-quoted fixture import rejected: $($doubleQuotedFixture.Error)" }
        Set-Content -LiteralPath $spec -Value $valid

        $previousEvidence = Join-Path $temp "$taskId-acceptance-previous.spec.cjs"
        Set-Content -LiteralPath $previousEvidence -Value 'preserved previous evidence'
        $promoted = Join-Path $temp "$taskId-acceptance-evidence.spec.cjs"
        $runnerCopy = Join-Path $temp "$taskId-generated.spec.cjs"
        Copy-PipelineArtifact -Source $spec -Destination $promoted | Out-Null
        Copy-PipelineArtifact -Source $promoted -Destination $runnerCopy | Out-Null
        if ((Get-Content -LiteralPath $previousEvidence -Raw).Trim() -ne 'preserved previous evidence' -or (Get-Content -LiteralPath $runnerCopy -Raw) -ne (Get-Content -LiteralPath $spec -Raw)) {
            throw "$taskId promotion overwrote existing evidence or changed the current spec."
        }

        $beforeResume = Get-Content -LiteralPath $spec -Raw
        $resume = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if (-not $resume.IsValid -or (Get-Content -LiteralPath $spec -Raw) -ne $beforeResume) { throw "$taskId safe resume did not preserve and validate the current spec." }

        Set-Content -LiteralPath $spec -Value $valid.Replace($marker, "$taskId-run-stale")
        $stale = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if ($stale.IsValid -or $stale.Error -notmatch 'stale') { throw "$taskId stale experiment fixture was accepted." }

        $invalidCases = @(
            @{ Name = 'direct Playwright import'; Source = $valid.Replace("require('./safe-playwright.cjs')", "require('@playwright/test')"); Diagnostic = 'safe-playwright' },
            @{ Name = 'absolute navigation'; Source = $valid.Replace("page.goto('/task-route')", "page.goto('http://127.0.0.1:5276/task-route')"); Diagnostic = 'relative local route' },
            @{ Name = 'protocol-relative navigation'; Source = $valid.Replace("page.goto('/task-route')", "page.goto('//unapproved.example/task-route')"); Diagnostic = 'relative local route' },
            @{ Name = 'route variable'; Source = $valid.Replace("await page.goto('/task-route');", "const route = '/task-route'; await page.goto(route);"); Diagnostic = 'relative local route' },
            @{ Name = 'dynamic route variable'; Source = $valid.Replace("await page.goto('/task-route');", "const route = window.location.pathname; await page.goto(route);"); Diagnostic = 'relative local route' },
            @{ Name = 'template-string navigation'; Source = $valid.Replace("await page.goto('/task-route');", 'await page.goto(`/task-route`);'); Diagnostic = 'relative local route' },
            @{ Name = 'computed route expression'; Source = $valid.Replace("await page.goto('/task-route');", "await page.goto('/task-' + 'route');"); Diagnostic = 'relative local route' },
            @{ Name = 'missing navigation'; Source = $valid.Replace("await page.goto('/task-route');", ''); Diagnostic = 'must navigate' },
            @{ Name = 'manual browser context'; Source = $valid.Replace("async ({ page })", "async ({ playwright })").Replace("await page.goto('/task-route');", "const browser = await playwright.chromium.launch(); const context = await browser.newContext(); await page.goto('/task-route');"); Diagnostic = 'isolated page fixture' },
            @{ Name = 'unmocked API'; Source = $valid.Replace("page.goto('/task-route')", "page.goto('/api/items')"); Diagnostic = 'page.route' },
            @{ Name = 'API mock registered after navigation'; Source = $valid.Replace("await page.goto('/task-route');", "await page.goto('/items'); await page.route('**/api/items', route => route.fulfill({ json: [] }));"); Diagnostic = 'before navigation' }
        )
        foreach ($invalidCase in $invalidCases) {
            Set-Content -LiteralPath $spec -Value $invalidCase.Source
            $invalid = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
            if ($invalid.IsValid -or $invalid.Error -notmatch $invalidCase.Diagnostic) { throw "$taskId $($invalidCase.Name) was accepted or had no actionable diagnostic: $($invalid.Error)" }
        }

        $apiMock = @"
// acceptance-run: $marker
const { test, expect } = require('./safe-playwright.cjs');
test('mocked local API acceptance', async ({ page }) => {
  await page.route('**/api/items', route => route.fulfill({ status: 200, json: [] }));
  await page.goto('/items');
  await expect(page.locator('main')).toBeVisible();
});
"@
        Set-Content -LiteralPath $spec -Value $apiMock
        $mockedApi = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if (-not $mockedApi.IsValid) { throw "$taskId explicit deterministic local API mock rejected: $($mockedApi.Error)" }

        foreach ($method in @('continue', 'fallback', 'fetch')) {
            $networkBypass = $valid.Replace("await page.goto('/task-route');", "await page.route('**/*', route => route.$method()); await page.goto('/task-route');")
            Set-Content -LiteralPath $spec -Value $networkBypass
            $rejectedBypass = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
            if ($rejectedBypass.IsValid -or $rejectedBypass.Error -notmatch 'route\.fulfill') {
                throw "$taskId acceptance accepted a route.$method() network bypass."
            }
        }

        Remove-Item -LiteralPath $spec -Force
        $missing = Test-PipelineAcceptanceSpec -Path $spec -ExpectedRunMarker "// acceptance-run: $marker"
        if ($missing.IsValid -or $missing.Error -notmatch 'missing') { throw "$taskId missing spec was accepted." }

        $triage = Get-PipelineTriageAction -Text "FEATURE_QA_STATUS: PASS_WITH_BASELINE_FAILURES`nBRANCH_REGRESSIONS: 0`nNEXT_ACTION: REGRESSION" -ResultName "$taskId simulated triage"
        if ($triage -ne 'REGRESSION') { throw "$taskId passing acceptance with zero branch regressions did not advance to Regression." }
    }
    Write-Host 'Acceptance pipeline regression tests passed for WORK-04 and two unrelated work items.'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
