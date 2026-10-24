# Generation authorization independent of acquisition attribution

Base commit: `36d324ac52bf16feef5a3ee9bfee3d24328c469e`.

## Root cause

Eight `/init/gen` generation entry points treated `BindAuthenticatedIdentityAsync()`
as their project permission check. `BindSocialIdentity` first loads `Funnels` by
the browser-supplied FunnelId. An absent row returns `409 funnel_not_found`, and
the client immediately threw its project-verification error, without checking
the authenticated account's saved project. The same browser acquisition context
was also reused as the generation ProjectId even when `cid` selected a different
workspace.

Binding is an anonymous-browser-to-account attribution operation: it associates
visitor/session, Funnel and Project with a canonical account, saves legacy
mappings when supplied, and assigns the account's original acquisition once.
It is not the authorization source for a returning user's saved workspace.

## Authorization contract

`AuthorizeGenerationAsync(cid)` still attempts attribution binding. A safe status
and reason are logged; the response body is no longer logged verbatim. Binding
success, a missing Funnel, network failure, or absent acquisition browser state
all proceed to an independent authenticated server check. Binding never grants
permission. Invalid authentication, unknown projects, another account's project,
and unavailable ownership verification remain blocked by that check.

`AuthorizeGenerationProject` resolves the JWT principal to the canonical Users
row. `GenerationProjectAuthorization` reuses `DashboardPromptQuery` to establish
ownership of the exact persisted `Prompt.CompanyIdeaId`, including historical
Prompt.UserId values containing the username from the canonical Users row.
Client-supplied usernames, emails and UserIds do not participate.

An existing owned `Projects.LegacyCompanyIdeaId` mapping preserves the existing
ProjectId and attribution fields for accounting. A legacy workspace without a
direct mapping next resolves the exact persisted `Prompt.InviteId` through the
authenticated account's `Subscriptions.InviteId -> ProjectId` relationship. The
referenced Project must still belong to that account; any saved Funnel or CID
relationship must agree. Ambiguous, foreign or broken links fail closed. This
recovers acquisition projects that only saved `LegacyCompanyIndustryId`, without
depending on browser state, names or the latest subscription. The lookup does not
change subscription status or payment/credit eligibility.

A legacy workspace without either association uses its existing CompanyIdeaId as
the project identity; no synthetic
Project, Funnel, or acquisition is written. Conflicting mappings fail closed.
Generation endpoints recheck ownership independently; an owned Projects mapping
to another account's Prompt cannot confer access to that CID.

The attribution binder also refuses to transfer already-owned Funnel/Project
records, with conditional writes protecting against a concurrent owner change.

## Affected entry points

| Feature | Generation authorization |
| --- | --- |
| Social Post | Canonical CID preflight; server ownership before quota, provider and usage |
| Website | Canonical CID preflight replaces the create-on-miss legacy resolver; website generation, image description, background extraction and fallback image check ownership |
| Print Materials | Canonical CID preflight and server ownership/quota gates |
| Events & Activations | Canonical CID preflight and server ownership/quota gates |
| Brand Logo | Canonical CID preflight and server ownership/quota gates |
| Digital Marketing | Canonical CID preflight and server ownership/quota gates |
| Package Label | Canonical CID preflight and server ownership before existing quota |
| Commercial Video | Canonical CID preflight for finalization and audio; scene/audio endpoints recheck ownership; existing scene quota remains |

The active AI Recommendations component routes to these modals. Its old embedded
generation helpers are not wired as the `/init/gen` generation callbacks.
Main/secondary image generation does not use the failing binding gate; its
authorization path is unchanged. The shared Runware image endpoint uses the new
ownership lookup only for `ai_recommendations` (Website fallback images).

The audit found no server quota gate in Print, Events, Brand or Digital generation.
Those now use the existing quota service before provider work. Existing usage
units and accounting semantics are unchanged, including existing null units in
those handlers; changing their credit measurement is outside this fix.

Website's authenticated `GetWorkspaceAIImageDescription` entry point ignores body
UserId and checks quota. The existing image-description entry point and its trusted
queue-context behavior remain unchanged for the protected background video flow.
Website background extraction now receives the canonical project and a bearer
token. Prompts, selected/fallback image behavior, persistence, provider payloads,
video scenes, planning and composition are unchanged.

No generation retry was added. Authorization does not invoke providers, persist
assets, or write usage. Existing provider job IDs and idempotent usage recording
remain responsible for billing and persistence.

## Attribution follow-up required

The direct/composite match diagnostics compare localStorage keys with each other;
they do not check DynamoDB. Both copies may agree on a stale, deleted, or
different-environment FunnelId. Legacy Prompt workspaces also need not have an
acquisition Funnel. Returning-user authorization now supports that condition.
The precise reason the observed production Funnel is absent cannot be established
from these logs or repository code alone. Inspect the affected environment's
record lifecycle separately. This change does not fabricate or repair attribution
history, Purchases, AdsDeliveries, GA4, Google Ads or Stripe records.

## Verification

New tests use in-memory DynamoDB and HTTP doubles, including actual Social and
Website handlers. They cover binding success/missing Funnel/network failure,
missing browser state, canonical and legacy owners, pagination, unknown/foreign
CID, forged client identity, conflicting mappings, binding ownership protection,
quota rejection, Commercial Video and other recommendation gates, and exactly
one provider request and one canonical usage record for a successful operation.

Run with .NET SDK 8.0.300:

```powershell
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore
dotnet build DesignMode.AI.Client/DesignMode.AI.Client.csproj --no-restore
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj --no-restore
git diff --check
```

The machine defaults to SDK 10.0.400, which failed static-asset compression for
AWSSDK.Core.wasm. Validation used a temporary SDK 8 selector, not a committed
tooling change. Existing Telerik licensing, nullable and assembly-version
warnings remain. No browser/UI QA or real provider calls were performed.

Final results: all 47 new focused tests passed. Client and Server builds passed
with zero errors. Full suite: 855 passed, 18 skipped (opt-in integration tests),
5 failed. Four failures are the existing
`FunnelRecoveryVisualTests.Sequence_UsesResponsiveSharedLayoutAndSafeResume`
cases (-1, 0, 1, 2); the fifth is
`GenWorkspaceModalLayoutTests.WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable`.
Their tests and the relevant RecoveryEmailLayout/app.css sources are unchanged
from BASE_COMMIT. These unrelated failures were not repaired. `git diff --check`
passed. The temporary SDK selector was removed after verification.

## Shared file risk

Changes to `AttributionContextService`, `AttributionStore`, `Gen.WebsiteImages`,
`DesignModeFunctions` and `RunwareVideoFunctions` are limited to authorization
integration, safe binding diagnostics, ownership protection and test injection
points. Review these shared-file changes when integrating other ongoing work.
`Gen.razor.cs`, protected attribution persistence models, payment systems and the
background video pipeline were not changed.

## Cost attribution correction (2026-09-21)

The original fix missed subscription-linked acquisition Projects without a direct
LegacyCompanyIdeaId mapping. Website and Social still wrote costs, but under the
legacy CID, leaving the original Project's cost aggregate incomplete. The exact
owned Prompt/invite/subscription relationship now retains the original Project,
Funnel and acquisition IDs. This is read-only and does not manufacture attribution.

Verification: 58 focused tests passed, including Website/Social provider and usage
checks with the original Project/Funnel IDs, invalid-link rejection, primary-map
precedence and subscription pagination. An opt-in, read-only local integration
test also passed against the affected existing Project. Full suite: 866 passed,
19 skipped, the same 5 unrelated failures listed above. No browser QA or paid
provider calls were used.

The three confirmed mislinked local usage rows were relinked in one conditional
DynamoDB transaction after checking the persisted ownership chain and saving a
local backup. Their existing costs, units, jobs, statuses and timestamps were
verified unchanged. No Project, Funnel, Subscription, purchase or ad record was
modified. This one-time local repair is separate from the read-only runtime fix.
