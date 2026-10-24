# Account-wide AI credit accounting audit

Base commit: `3fb2b3582de36a78c0b7bb1a27946c4b0d0998a8`

Branch: `feature/account-wide-ai-credits-2026-09-26-083023`

## Confirmed cause and correction

`DesignModeFunctions.CallRunwareAPI` previously treated acquisition attribution as
the condition for charging ordinary main, secondary, and social images. A request
without a valid user/project/funnel tuple skipped both `EvaluateAsync` and
`AIUsageRecorder.StartAsync`, then still called Runware. Only recommendations,
package-label images, and persisted website-image requests rejected invalid
context. This explains why the original acquisition project could charge while
later Dashboard projects did not.

Gen also initialized its ordinary image context from browser acquisition storage.
That context could be absent or belong to a different project from the open CID.
This could both bypass accounting and attribute usage to the wrong owned project.

All shared paid image features now resolve ownership through the existing
`GetOwnedGenerationProjectAsync` path. An owned legacy Prompt/CID is sufficient;
an acquisition funnel is not required. Missing authentication or ownership rejects
before the provider call. Server-resolved project/funnel values replace client
claims. Gen authorizes its current `CompanyIdeaId` before constructing the image
request, including initial generation, regeneration, and modal image calls.

## Existing canonical model

- Storage is DynamoDB, not an EF DbContext or migration-driven credit database.
- `Purchases` contains verified user-owned subscription payments and billing-period
  metadata. `AIUsageSummaryService` selects the latest qualified subscription
  purchase for the canonical authenticated user, excluding credit top-ups.
- With the ledger disabled, remaining allowance is the configured plan limit minus
  successful `Paid` usage within that billing period, filtered by **UserId**, across
  every project. Pagination is consumed. Job/feature deduplication is preserved.
- With the existing ledger enabled, `AICreditLedger` owns `wallet:<UserId>`.
  Its base-period usage and purchased balance are authoritative for availability.
  Top-ups are granted once with the purchase in a DynamoDB transaction. Base credits
  are allocated first, then purchased credits. Neither wallet identity nor the
  reservation's user/job/feature identity is project-scoped.
- `AIGenerationUsage.ProjectId` and `FunnelId` remain attribution, not wallet keys.
  `GenerationJobId`, provider task identity, and feature retain their existing
  accounting/deduplication meanings. The authenticated canonical user owns charges;
  body-supplied UserId is not trusted.
- Project creation stores project/acquisition relationships; paid Dashboard
  workspaces can instead be represented by owned Prompt records. Neither path
  grants an allowance, copies a wallet, or clears usage. Reopening a project does
  not change the account summary query.

## Chargeable entry-point audit

All costs remain supplied by the existing `AIUsageQuotaOptions.UnitsFor` mapping.
No GenerationUnits values or defaults changed.

| Feature | Existing accounting path | Result |
| --- | --- | --- |
| Main/secondary/social images, image regeneration | CallRunwareAPI → summary → recorder | Fixed missing-context bypass; current project is now authorized |
| Enhancement recommendations and package-label images | CallRunwareAPI → summary → recorder | Preserved costs and ownership; shares the mandatory gate |
| Website saved-image fallback | Persisted website image ownership → CallRunwareAPI | Preserved saved-image identity and project attribution |
| Website generation | GenerateWebsiteFromImage → owned project → summary → recorder | Already account-wide |
| Social post generation | GenerateSocialMediaPost → owned project → summary → recorder | Already account-wide |
| Branding/logo | GenerateBrandingLogo → owned project → summary → credit job | Already account-wide; child provider telemetry does not add a second charge |
| Digital marketing | GenerateDigitalMarketing → owned project → summary → credit job | Already account-wide |
| Package Label | GeneratePackageLabel → owned project → summary → recorder | Already account-wide |
| Print Materials | GeneratePrintMaterialPdf → owned project → summary → recorder | Already account-wide |
| Events & Activations | GenerateEventActivation → owned project → summary → credit job | Already account-wide |
| Commercial Video | CallRunwareFinalizeSceneVideo → owned project → summary → recorder | Existing first-scene charge and funded continuation semantics retained |

Non-chargeable acquisition previews, reference-image calls, supporting prompt/text
operations, and commercial overlay telemetry retain their existing semantics.
They were not assigned new credit costs. The old unauthenticated shared-image
helper in Success has its generation invocation commented out; CreateNewImage's
image call is also commented out. SocialMediaBefore and WebsiteImagesBefore have
no component consumers in the repository. Direct calls to the paid shared-image
endpoint can no longer use these missing-context request shapes to bypass quota.

## UI

The Dashboard/workspace header, account page, and credit modal use
`AIUsageMeter`, `AIUsageRefreshService`, and the authenticated `ai-usage-summary`
endpoint. This endpoint accepts no project balance scope. Existing generation
completion/modal callbacks refresh that account summary; no client-side deduction
or page reload was added. The separate routed Designer Dashboard's hard-coded
25-credit badge was replaced with the same account meter.

## Concurrency, retries, and failures

The existing ledger uses conditional wallet revisions and atomic reservation/job
writes across service instances, so different projects compete for the same funds.
An already reserved job is rejected before another provider call. Confirmed usage
write failure releases its reservation before rejecting; quota failure returns the
existing 429 code; duplicate jobs return 409; reservation infrastructure failure on
the shared-image path returns 503. Provider failure keeps existing Failed usage
semantics and releases a ledger hold when recorded. Success consumes the hold.
Failure to persist completion can retain a hold for reconciliation. Existing
timeout/cancellation and provider-completion behavior was not redesigned.

**Deployment limitation:** atomic holds and durable duplicate rejection require the
existing `AI_CREDITS_LEDGER_ENABLED=true` deployment mode. The disabled legacy mode
counts successful usage after completion and does not prevent simultaneous
requests from overspending. This pre-existing configuration distinction remains;
no production settings were changed or inferred. Enabling a ledger where its table
has not been provisioned requires the existing operator rollout procedure in
[ai-credit-purchases.md](ai-credit-purchases.md), including draining in-flight jobs.
This patch does not provision tables, introduce another wallet, or rewrite history.

## SHARED FILE RISK

- `DesignMode.AI.Server/DesignModeFunctions.cs`: high overlap; only the shared image
  ownership gate changed. Provider payloads, prompts, units, and completion remain.
- `DesignMode.AI.Client/Pages/Init/Gen.razor.cs`: high overlap; removes cached
  acquisition context and authorizes the current project in the image helper.
- `DesignerLeftNav.razor` and `DesignerDashboard.razor`: only the static credit badge
  and its obsolete numeric parameter changed.
- Existing generation authorization test fixture, endpoint tests, ledger tests,
  and package-label source regression were extended for the shared gate.

No wallet/summary calculation, purchase/subscription fulfillment, pricing, database schema,
historical data, auth routing, advertising, or recovery-email implementation changed.

## Follow-up: missing generation records

The subsequent asset-recording investigation found another gap: the shared
`CallRunwareAPIWithReferenceImage` endpoint had no usage lifecycle at all. It now
writes a durable usage start before calling Runware and records success/failure,
provider task/job identity, requested image count/dimensions, and returned cost.
Gen supplies authenticated current-project context; supplied projects must be owned.
Anonymous acquisition calls remain supported and are recorded without invented user
or project attribution. Reference-image operations retain their existing zero-credit
semantics; recording an operation does not introduce a new charge.

`AIUsageRecorder.StartAsync` now propagates usage-start failures for all `Paid` work,
including child recommendation assets, regardless of ledger enablement. Previously
it swallowed these failures when the ledger was disabled, allowing unrecorded
provider work. This is an additional shared-file risk in
`DesignMode.AI.Server/Services/AIUsageRecorder.cs`: paid generation now stops if
accounting storage is unavailable. Existing best-effort completion updates remain;
a failed outcome update can leave a Started record needing reconciliation.

Asset storage and generation accounting are separate:

| Data | DynamoDB tables |
| --- | --- |
| Canonical/acquisition projects | `Projects` |
| Legacy workspaces and company metadata | `Prompt`, `CompanyIndustry` |
| Text ideas/recommendation ideas | `Ideas` |
| Image metadata | `ImageIdeas`, `ConceptImages`, `VideoImages`, `InviteClientImages` |
| Saved recommendation output/version metadata | `Website`, `Social`, `BrandingLogo`, `DigitalMarketing`, `PackageLabel`, `PrintMaterials`, `EventActivations` |
| Provider operations and credit usage | `AIGenerationUsage` (exact case) |
| Account wallet/reservations | `AICreditLedger`, or `AICreditLedgerTest` in test mode |

Image/PDF/HTML binaries are stored separately, including S3 and local SavedImages
paths depending on the saver/environment. Some asset tables contain version and
company metadata rather than every generated file URL. `AIGenerationUsage` is one
record per tracked operation, not necessarily one per file: image batches use
ImageCount, and recommendation bundles have a chargeable parent plus child provider
records. Summing charges per saved file would double-charge those bundles.

Follow-up verification: 78 targeted tests passed; the full suite passed 996 tests,
skipped 19, and retained the same six unrelated failures noted below. New tests
cover reference-image workspace/preview accounting, ownership rejection, provider
failure, and storage failure blocking provider calls with the ledger disabled.
Changes remain local; no deployment or historical backfill was performed.

## Follow-up: provider USD cost persistence

Customer credit units and provider expense are separate columns. Runware image
operations now parse the raw returned `cost` values as decimals into `ActualCost`,
including shared workspace images, acquisition images, and reference images. The
old non-nullable double DTO could turn an absent provider cost into a false zero.
A batch with missing costs now retains an unknown total rather than reporting a
partial total or inventing a price. Returned explicit zero remains zero.

OpenAI completions use `EstimatedCost` with `InputTokens`, `CachedInputTokens`,
`OutputTokens`, runtime `Model`, `PricingVersion`, `ProviderRequestId`, and `Currency`.
The shared recorder now recognizes both Chat Completions and Responses usage
shapes. Its existing rate table was not changed. This table estimates text-token
costs; it does not separately price image-generation tool output. Unknown pricing
or absent token usage still yields no estimate, not a fabricated cost. These are
not guaranteed full invoice costs for image-tool operations.

The usage store retries transient throttling/server failures during the same cost
metadata write up to three attempts. It does not retry the generation/provider
call. Exhausted completion failures log the usage ID and reconciliation requirement.
Permanent persistence failures still need reconciliation; this is not a durable
outbox or a historical data backfill.

Additional SHARED FILE RISK: `AIGenerationUsageStore.cs` (bounded metadata retries),
`AIUsageRecorder.cs` (format detection and cost-failure diagnostics), and
`OpenAIUsageParser.cs` (both usage formats). New `RunwareUsageParser.cs` isolates
decimal cost extraction. Tests assert DynamoDB USD cost fields across projects,
actual versus estimated cost, missing versus zero cost, token counts, and retries.

Provider-cost verification: 69 focused tests passed after the final parser guard.
The preceding full-suite run passed 1004 tests, skipped 19, and retained the same
six unrelated recovery/layout failures. Image-tool model/rate coverage remains an
explicit gap; the handlers do not select a concrete image-tool model, and this
change does not invent one or modify the protected provider rate catalog.

## Validation

Focused regressions cover three projects in both ledger modes, unchanged balances
when another owned project is introduced, reopening projects, exhaustion followed
by a new project for every shared image feature, canonical user/project attribution,
foreign/missing context, provider failure, durable duplicate rejection, reservation
failure, base/purchased allocation across projects, and independent users. Existing
tests cover cross-instance concurrent wallet reservation and the other paid
generation endpoint ownership/quota checks.

Build/test commands use `-p:CompressionEnabled=false` to work around the installed
.NET 10 SDK's existing static-web-asset compression negotiation failure. Azure
Functions build also requires access to the user's NuGet configuration.

Results:

- `dotnet build DesignMode.AI.sln --no-restore -p:CompressionEnabled=false` passed.
- `dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore -p:CompressionEnabled=false`
  completed with 991 passed, 19 skipped, and 6 unrelated failures. All credit and
  generation authorization regressions passed, including the new tests.
- The six failures are four `FunnelRecoveryVisualTests.Sequence_UsesResponsiveSharedLayoutAndSafeResume`
  rows (expected `background-size:contain`) and two `GenWorkspaceModalLayoutTests`
  cases (MakeVideoModal close markup and workspace CSS). Their tests and relevant
  production sources are unchanged from BASE_COMMIT. Protected recovery/layout
  systems were not modified to address them.
- `git diff --check` passed. No live provider calls, production changes, browser QA,
  schema changes, or historical data operations were performed.
