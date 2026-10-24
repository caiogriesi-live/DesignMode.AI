param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$Request
)

$ErrorActionPreference = "Stop"

# ==================================================
# PATHS
# ==================================================

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

$ProjectManagerPrompt = Join-Path $RepoRoot ".agent/prompts/project-manager.md"
$DraftDir = Join-Path $RepoRoot ".agent/projects/draft"
$DraftProject = Join-Path $DraftDir "$ProjectId.md"

$ResultDir = Join-Path $RepoRoot ".agent/results/projects/$ProjectId"
$PlannerOutput = Join-Path $ResultDir "project-manager-codex-output.txt"

# ==================================================
# HEADER
# ==================================================

Write-Host ""
Write-Host "DESIGNMODE PROJECT MANAGER"
Write-Host "=========================="
Write-Host "Project: $ProjectId"
Write-Host ""
Write-Host "Objective:"
Write-Host $Request
Write-Host ""

# ==================================================
# VALIDATION
# ==================================================

if (!(Test-Path -LiteralPath $ProjectManagerPrompt -PathType Leaf)) {
    throw "Project Manager prompt not found: $ProjectManagerPrompt"
}

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    throw "ProjectId cannot be empty."
}

if ([string]::IsNullOrWhiteSpace($Request)) {
    throw "Request cannot be empty."
}

if ($ProjectId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw "ProjectId may contain only letters, numbers, dots, underscores, and hyphens."
}

New-Item -ItemType Directory -Force -Path $DraftDir | Out-Null
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

if (Test-Path -LiteralPath $DraftProject) {
    Write-Host "ERROR: Draft project already exists:"
    Write-Host $DraftProject
    Write-Host ""
    Write-Host "Delete it or use a different ProjectId."
    exit 10
}

# ==================================================
# PROJECT MANAGER REQUEST
# ==================================================

$Prompt = @"
Act as the Project Manager Agent defined in:

.agent/prompts/project-manager.md


==================================================
HUMAN OBJECTIVE
==================================================

Project ID:

$ProjectId


Objective:

$Request


==================================================
REPOSITORY CONTEXT
==================================================

Repository root:

$RepoRoot


Inspect the repository before producing the project plan.

Your inspection may include relevant:

- existing Marketing Dashboard implementation
- pages and components
- APIs and services
- models and persistence
- Stripe integration
- Google Ads or Google integration code
- expense-related functionality
- analytics and attribution systems
- tests
- configuration structure
- existing agent infrastructure

Repository inspection is READ-ONLY.

Do not modify application code.

Do not create or switch branches.

Do not commit.

Do not push.

Do not merge.

Do not deploy.


==================================================
PLANNING RULES
==================================================

Determine what already exists before proposing new work.

Do not assume the project must be built from scratch.

Do not silently invent business or financial definitions.

If an ambiguity materially changes product behavior, financial calculations,
attribution, source-of-truth data, external permissions, or protected-system
behavior, require human clarification.

Break the project into coherent milestones and bounded engineering work items.

Engineering work items must be suitable for later conversion into individual
Planner Agent tasks.

Do not execute any engineering work.


==================================================
OUTPUT
==================================================

Write the generated project plan directly to:

.agent/projects/draft/$ProjectId.md


Follow the project format defined in:

.agent/prompts/project-manager.md


The final section must contain exactly one of:

STATUS: READY_FOR_APPROVAL

or:

STATUS: NEEDS_CLARIFICATION


Do not invoke Planner.

Do not invoke Developer.

Do not invoke QA.

Do not invoke Triage.

Do not invoke Repair.

Do not invoke Regression.

Do not wait for interactive human instructions.
"@

# ==================================================
# RUN PROJECT MANAGER
# ==================================================

Write-Host "Starting Project Manager Agent..."
Write-Host ""

$Prompt | codex exec `
    -C $RepoRoot `
    --model "gpt-6-luna" `
    --approve-for-me `
    --output-last-message $PlannerOutput `
    -

$ExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Codex exit code: $ExitCode"

if ($ExitCode -ne 0) {
    Write-Host ""
    Write-Host "PROJECT MANAGER FAILED"
    exit $ExitCode
}

# ==================================================
# VERIFY OUTPUT
# ==================================================

if (!(Test-Path -LiteralPath $DraftProject -PathType Leaf)) {
    Write-Host ""
    Write-Host "ERROR: Project Manager completed but did not create:"
    Write-Host $DraftProject
    exit 20
}

$ProjectText = Get-Content -LiteralPath $DraftProject -Raw

# ==================================================
# ROUTE RESULT
# ==================================================

if ($ProjectText -match '(?im)^STATUS:\s*READY_FOR_APPROVAL\s*$') {

    Write-Host ""
    Write-Host "========================================"
    Write-Host "PROJECT READY FOR APPROVAL"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "Draft:"
    Write-Host $DraftProject
    Write-Host ""
    Write-Host "Review the project before approving it."
}
elseif ($ProjectText -match '(?im)^STATUS:\s*NEEDS_CLARIFICATION\s*$') {

    Write-Host ""
    Write-Host "========================================"
    Write-Host "PROJECT NEEDS CLARIFICATION"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "Review:"
    Write-Host $DraftProject
    Write-Host ""
    Write-Host "Answer the questions before approval."
}
else {

    Write-Host ""
    Write-Host "ERROR: Project Manager returned an unknown STATUS."
    Write-Host ""
    Write-Host "Review:"
    Write-Host $DraftProject

    exit 21
}

Write-Host ""
Write-Host "PROJECT PLANNING STAGE COMPLETE"