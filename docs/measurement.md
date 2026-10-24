# Social funnel measurement

> Google Ads migration: [Stage 1 safety infrastructure](ads-stage1.md) supersedes legacy Ads delivery/configuration instructions below. Production purchase dispatch now sends GA4 only; legacy UploadClickConversions is retained but disconnected. New Ads delivery defaults off and has no HTTP/OAuth transport yet. GA4 semantics are unchanged.

> [Stage 2 always-on advertising measurement](ads-stage2.md), revised 2026-09-01 and effective on deployment: internal and Google advertising measurement are always enabled for NEW traffic. No historical backfill or Google Ads transport is enabled.

## Scope and audit findings

No active GA4, gtag, GTM, Google Ads tag, dataLayer implementation, or consent UI existed. The old checkout stored a snapshot ID but not an immutable attribution snapshot. `StripeWebhook` already verifies Stripe signatures. Its subscription-created/updated handlers maintain entitlement-style `Subscriptions` records; those are not an idempotent collected-payment ledger. `invoice.paid` previously only logged. Those entitlement handlers, prices, redirects, and AI usage/cost code are unchanged.

## Browser architecture and events

`wwwroot/js/designmode-analytics.js` owns gtag initialization, consent gates, event/parameter filtering, safe URLs, debug logging, and page-view deduplication. `DesignModeAnalyticsService` is the Blazor adapter; `AnalyticsNavigation` observes NavigationManager. Static landing and Blazor use the same JS implementation. No raw gtag calls are added to Razor.

| Event | Execution point |
|---|---|
| landing_view | First measured `/social-landing.html` page view |
| social_form_start | First nonempty company/industry input |
| company_profile_saved, funnel_start | Existing save and canonical project creation both succeeded |
| designer_intro_view | Measured `/designerintro` navigation |
| account_gate_view | Actual account modal opening |
| sign_up | Successful email account creation; not its automatic login |
| login | Successful email login or Google authentication |
| generation_started | Bound identity starts the existing direction-generation pipeline |
| generation_completed / generation_failed | That pipeline finishes; success requires existing all-invite-saves-completed flag |
| creative_directions_viewed | MainConcept renders finished directions |
| main_concept_selected | Existing selection handler invoked by a click |
| funnel_end_viewed | Measured `/init/funnelend` navigation |
| video_started / video_completed | Native video play/ended events on FunnelEnd, deduplicated per media element |
| unlock_clicked | Accepted checkout button action, after existing processing guard |
| begin_checkout | Successful checkout response with a nonempty URL, immediately before unchanged Stripe navigation |
| paid_workspace_viewed | Gen opens and an authenticated read confirms a canonical paid purchase |
| purchase | Server only, from verified payment ledger |

`unlock_clicked` and `begin_checkout` use the wrapper's pre-navigation delivery path: GA callback acknowledgement with beacon transport and an 800 ms fail-safe. The processing guard is set before the first event. `begin_checkout` remains after successful checkout URL validation, and Stripe navigation awaits acknowledgement or the bound; blocked analytics never blocks checkout indefinitely.

Google authentication currently exposes success but not whether it created an account. It is measured as `login`; no Google `sign_up` is inferred. Returning legacy customers with no canonical purchase record are not mislabeled as verified paid-workspace views. Generation events describe the direction pipeline, not individual provider billing operations or background video completion.

The only custom parameter keys accepted by browser dispatch are `entry_type`, `landing_page_id`, `landing_variant`, `funnel_stage`, `auth_state`, `auth_method`, `method`, `generation_category`, `deliverable_type`, `result_count`, `duration_bucket`, `plan`, `billing_interval`, `value`, and `currency`. Categorical values have explicit enums, not just key filtering. Numeric fields are validated. Page location/title/referrer are controlled by the wrapper. No internal IDs, customer strings, media URLs, costs, or provider IDs are sent. Not every event uses every allowed dimension.

The URL sanitizer recognizes only the six Social routes, removes all queries and fragments, and maps other paths to `/other`. `/init/Success` is normalized to `/init/success` in analytics only. Query-only navigation is deduplicated; leaving a route and returning produces a new view. Automatic config page views are disabled. The static Social page explicitly requests canonical page tracking; the hidden `/warmup` preload iframe never initializes analytics. In each top-level document, the newest route requested before the Google tag is ready is flushed once, then up to 20 initialization-time custom events are flushed in order. Blazor routes use `AnalyticsNavigation`. Success and Gen never emit purchase.

## Analytics preference and advertising consent

Browser analytics is intentionally allowed by default and GA4 initializes automatically when the public client configuration is enabled. Advertising consent remains denied by default. The effective first-visit state is:

```javascript
window.designModeAnalytics.setConsent({ analytics: true, advertising: true });
```

The wrapper persists analytics preferences in `dm_measurement_consent`; saved analytics withdrawal remains effective across static/Blazor visits. Its compatibility `advertising` property is forced true and cannot be disabled by localStorage or client input. Analytics withdrawal stops wrapper analytics events without changing Google advertising policy. Analytics failures never block the funnel.

For new checkout snapshots the server sets legacy `Measurement.AdvertisingConsent=true`, `AdvertisingMeasurementPolicyVersion=internal-always-on-2026-08-31`, and `GoogleAdvertisingConsent=true` regardless of browser false/missing input. No user-grant evidence is fabricated. Google-issued client/session identifiers and analytics handling are unchanged. Purchases copy immutable snapshots, including predeployment snapshots without alteration. New touches gain prospective policy markers; existing records are not rewritten. Final wording and jurisdiction-specific affirmative-consent or opt-out requirements need legal review.

Analytics preferences remain browser-local: later withdrawal does not rewrite checkout snapshots or cancel queued GA4 measurements. New policy-versioned records require no AdvertisingConsents row in Ads evaluation. GA4 maps ad_user_data from the new always-true Google field; identity/value/currency/PaidAt/GA IDs and delivery are unchanged. Ad personalization stays denied. No Google Ads uploads are enabled.

## Checkout attribution and canonical payments

At checkout, the server validates the existing project/user/funnel association and writes an immutable `AttributionSnapshots` record. It selects the most recent paid touch in that funnel, otherwise its original acquisition touch. All gclid/gbraid/wbraid and UTM/campaign fields are retained in the database snapshot. Original AcquisitionId remains intact; TouchId records the selected touch. Later touch changes cannot alter the purchase snapshot.

The signature-verified `StripeWebhook` invokes `StripeVerifiedPurchaseHandler` before its existing handlers:

- Subscriptions: `invoice.paid`, `status=paid`, positive `amount_paid`, and not paid out of band. Zero invoices/trials are not conversions. Subscription metadata resolves CheckoutAttemptId using current/legacy invoice shapes or Stripe subscription retrieval.
- One-time: `payment_intent.succeeded`, positive `amount_received`, and a resolved Checkout Session whose mode is `payment`, not subscription.
- Missing legacy checkout metadata is logged safely and not guessed. An attempt with no snapshot fails visibly for reconciliation; no historical snapshot is fabricated.

The resolver checks earlier positive paid invoices for the subscription, including first payments after trials. A transactional customer marker prevents subsequent new subscriptions for the same canonical user from becoming additional acquisition conversions. A renewal arriving before its first invoice can establish an unclaimed marker; an earlier verified first payment can subsequently claim acquisition once. Historical purchases outside this ledger require migration/reconciliation before treating legacy customers as new customers.

`TransactionId` is an opaque deterministic UUID derived from the Stripe invoice/payment identifier. Retries with different webhook event IDs still resolve the same purchase. Purchase creation and customer marker changes are DynamoDB transactions. `IsRenewal` applies to later subscription-cycle invoices; other additional payments are not falsely called renewals.

Actual collected amount/currency come from Stripe, not the pricing page. begin_checkout uses the server-retrieved Stripe Price; final purchase reflects discounts/actual collection. Existing USD pricing is unchanged.

## DynamoDB deployment

Provision these tables **before deploying the checkout changes**, each `Id` String partition key, no sort key or GSIs, PAY_PER_REQUEST:

- `AttributionSnapshots`: Id and Data (immutable JSON containing AcquisitionId, TouchId, click IDs, acquisition dimensions, consent/Google context, CreatedAt).
- `Purchases`: Id, TransactionId, UserId, ProjectId, FunnelId, Amount, Currency, PaymentStatus, IsFirstPayment, IsRenewal, Data, Ga4Status, AdsStatus, and optional Ga4Lease/AdsLease.
- `PurchaseCustomers`: Id = canonical Users.Id, TransactionId, FirstPaidAt, AcquisitionClaimed. Never exported to Google.

Purchase Data contains TransactionId, CheckoutAttemptId, UserId, ProjectId, FunnelId, AttributionSnapshotId, AcquisitionId, StripeTransactionId, StripeEventId, StripeSubscriptionId, AmountMinor, Amount, Currency, Plan, BillingInterval, PaymentStatus, BillingReason, IsFirstPayment, IsRenewal, CreatedAt, PaidAt, and immutable Snapshot. `CheckoutAttempts` gains BillingInterval; its existing links are preserved.

Clients use `AttributionStore.CreateClient()` and existing local/production configuration. No new hard-coded DynamoDB endpoint. Local provisioning:

```powershell
powershell -NoProfile -File scripts/Initialize-MeasurementTables.ps1
```

Production IAM needs GetItem/PutItem/Scan/UpdateItem and TransactWriteItems for the relevant tables. Scans are an initial low-volume implementation; add indexes before scaling. No production table was created automatically.

## Durable conversion delivery

`DeliverPurchaseMeasurements` runs every five minutes and scans pending purchases. Conditional leases and terminal statuses prevent concurrent duplicate sends. It does not run outbound Google calls inside checkout or webhook processing.

- GA4 Measurement Protocol: purchase uses opaque transaction_id, actual value/currency, a safe plan item, payment_type, and Google-issued client_id. Original GA session_id is used only for the first payment within 24 hours of checkout. No internal IDs are exported. Renewals are purchase revenue, not Ads acquisition uploads. Events older than 72 hours are Expired, never retimestamped.
- Google Ads REST uploadClickConversions: only IsFirstPayment with advertising consent and a paid click. The payload selects gclid, otherwise gbraid, otherwise wbraid; stores all three internally. It uses opaque orderId, actual collected value/currency, and actual payment time. OAuth refresh and developer-token headers are server only.
- Statuses: Pending, Submitted, Rejected, Unknown, Expired, NoConsent, NotEligible. Missing configuration leaves Pending. Partial Google Ads failures are rejected, not marked successful. Uncertain transport results require reconciliation rather than blind automatic replay. Never reset Unknown/Rejected without checking Google first.
- A process crash between send and acknowledgment cannot provide distributed exactly-once delivery. Stable GA transaction_id and Ads orderId provide downstream deduplication on replay. GA HTTP acceptance is not proof of ingestion; validate the payload and inspect reports before enabling production.

## Configuration still required

Public deployment-specific `wwwroot/analytics-config.json`:

- enabled (`true`), measurementId (`G-22R24LF0HN`), debug (`false` in the checked-in production-safe default; set `true` only in a local/test deployment for DebugView), landingVariant (control/a/b).

Server environment (do not commit secrets):

```text
MEASUREMENT_LEDGER_ENABLED=true
GA4_SERVER_ENABLED=true
GA4_MEASUREMENT_ID=G-...
GA4_API_SECRET=...
GOOGLE_ADS_UPLOAD_ENABLED=true
GOOGLE_ADS_CUSTOMER_ID=digits-only
GOOGLE_ADS_CONVERSION_ACTION_ID=digits-only
GOOGLE_ADS_DEVELOPER_TOKEN=...
GOOGLE_ADS_OAUTH_CLIENT_ID=...
GOOGLE_ADS_OAUTH_CLIENT_SECRET=...
GOOGLE_ADS_OAUTH_REFRESH_TOKEN=...
GOOGLE_ADS_LOGIN_CUSTOMER_ID=optional-manager-digits-only
GOOGLE_ADS_API_VERSION=supported-version-for-your-account
```

Ledger creation can be enabled independently of both Google flags. AzureWebJobsStorage must support the timer. Existing StripeSecretKey/StripeWebhookSecret remain unchanged. Restart the Functions host after environment changes.

## Manual Google setup

1. Create/use one GA4 web stream. Disable enhanced measurement's automatic history page views, forms, video, outbound-click and other auto collection so the strict wrapper owns tracking and never sends raw route queries or media URLs. Do not add a second GTM/gtag installation.
2. Put the same stream ID in browser config and server config. Create a Measurement Protocol API secret. Use a separate test stream; validate a safe test payload with GA's debug endpoint before enabling collection.
3. Use DebugView to verify funnel events. Register useful low-cardinality dimensions (plan, billing_interval, funnel_stage, landing_variant, payment_type, etc.). Mark purchase as a GA4 key event; renewal revenue remains distinguishable by payment_type.
4. Create one Google Ads imported-click/offline purchase conversion action, category Purchase, primary for bidding, dynamic value, transaction currency. For new-customer acquisition use Count=One. Obtain API developer-token/OAuth access and the conversion customer's IDs and currently supported API version.
5. Link Ads and GA4 if desired, but **do not also import GA4 purchase into Ads as another primary** when using this direct upload. sign_up, generation_completed, main_concept_selected, funnel_end_viewed, unlock_clicked and begin_checkout are observation/secondary only.
6. Enable auto-tagging and retain the existing click-ID/UTM capture. Check Ads upload diagnostics for accepted conversions, consent, attribution windows and click eligibility. Never upload test Stripe payments to production Ads.

References: [GA4 Measurement Protocol](https://developers.google.com/analytics/devguides/collection/protocol/ga4/reference), [Google Ads click uploads](https://developers.google.com/google-ads/api/docs/conversions/legacy_oci_guide), [Stripe invoice object](https://docs.stripe.com/api/invoices/object).

## Browser and webhook checklist

1. Provision tables; enable the ledger. Keep Google production uploads disabled while testing. Use Stripe test mode and a GA test stream.
2. On a fresh browser profile, confirm GA4 loads automatically, analytics storage is granted, and advertising storage/user-data/personalization remain denied. Inspect Network payloads: no queries, email, company text, internal IDs, media URLs, Stripe IDs, or AI costs. Use `/privacy.html` to disable analytics, reload the funnel, and confirm no Google tag or collection request is initiated; re-enable it and confirm initialization resumes.
3. Follow `/social-landing.html` → `/designerintro` → `/init/mainconcept` → `/init/funnelend`. Confirm one page_view per route, form_start once after nonempty input, and action events only after actions. Generation completion means directions successfully persisted.
4. Open/play/finish video; verify native play/ended events, not merely modal opening. Click unlock; begin_checkout must occur only after successful checkout creation, with server value/currency. Failure to create checkout must not emit begin_checkout.
5. Complete a test payment. Confirm Purchases links to the correct CheckoutAttempt and immutable snapshot/click; zero invoices do not create paid purchases. Existing Success → Gen route remains unchanged.
6. Retry the same Stripe event, replay it with a different event ID for the same transaction, and refresh Success/Gen. There must still be exactly one purchase. A later paid cycle creates a separate renewal and no new acquisition conversion.
7. Inspect Ga4Status/AdsStatus and safe `[MEASUREMENT]` logs. Enable only the test-ready destination. Confirm the same opaque transaction/order ID in outbound payloads. Block Google requests and confirm checkout/entitlement behavior is unaffected.
8. Confirm paid_workspace_viewed after server confirmation. If the webhook arrives after the initial workspace check, refresh once after the ledger exists. Legacy users without canonical payment history intentionally require reconciliation rather than inferred purchase events.

## Implementation file inventory

New:

- `DesignMode.AI.Client/wwwroot/analytics-config.json`
- `DesignMode.AI.Client/wwwroot/js/designmode-analytics.js`
- `DesignMode.AI.Client/Services/DesignModeAnalyticsService.cs`
- `DesignMode.AI.Client/Layout/AnalyticsNavigation.razor`
- `DesignMode.AI.Shared/Models/Attribution/MeasurementContext.cs`
- `DesignMode.AI.Server/Services/PurchaseMeasurementModels.cs`
- `DesignMode.AI.Server/Services/PurchaseMeasurementStore.cs`
- `DesignMode.AI.Server/Services/StripeVerifiedPurchaseHandler.cs`
- `DesignMode.AI.Server/Services/GoogleConversionDelivery.cs`
- `DesignMode.AI.Server/PurchaseMeasurementFunction.cs`
- `DesignMode.AI.Server/PaidWorkspaceMeasurementFunction.cs`
- `DesignMode.AI.Test/PurchaseMeasurementTests.cs`
- `DesignMode.AI.Test/analytics.test.cjs`
- `scripts/Initialize-MeasurementTables.ps1`
- `docs/measurement.md`

Updated:

- `DesignMode.AI.Client/App.razor`, `Program.cs`, `_Imports.razor`, `wwwroot/index.html`, `wwwroot/social-landing.html`
- `DesignMode.AI.Client/Pages/DesignerIntro.razor`
- `DesignMode.AI.Client/Pages/Init/MainConcept.razor`, `FunnelEnd.razor`, `Gen.razor.cs`
- `DesignMode.AI.Server/DesignModeFunctions.cs`, `Program.cs`, `Services/AttributionStore.cs`
- `DesignMode.AI.Test/AttributionStoreLocalIntegrationTests.cs` (cleanup of its new snapshot)

No AI ledger/pricing/provider implementation files were changed in this phase. Existing unrelated working-tree changes were preserved.

## Verification commands

```powershell
dotnet build DesignMode.AI.sln --no-restore -p:CompressionEnabled=false
$env:RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS='true'
$env:USE_LOCAL_DYNAMODB='true'
$env:LOCAL_DYNAMODB_URL='http://localhost:8000'
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-build
node --test DesignMode.AI.Test/analytics.test.cjs
```

Automated tests use deterministic samples, DynamoDB Local, mocked Stripe resolution and mocked Google HTTP. They do not send live Google conversions or make live Stripe payments. A credentialed end-to-end browser/payment test remains a deployment validation step.

## Paid AI generation allowance

`AIGenerationUsage` is the canonical provider-cost ledger. Customer allowance is a separate `GenerationUnits` field and is enforced only when a plan limit is configured. Set the applicable plan limits and product weights in the server environment; no production allowance is assumed when a value is absent:

```text
AI_GENERATION_LIMIT_STARTER=<choose value>
AI_GENERATION_LIMIT_PLUS=<choose value>
AI_GENERATION_LIMIT_PRO=<choose value>
AI_GENERATION_UNITS_IMAGE=<choose value>
AI_GENERATION_UNITS_WEBSITE=<choose value>
AI_GENERATION_UNITS_SOCIAL_ASSET=<choose value>
AI_GENERATION_UNITS_COMMERCIAL_VIDEO=<choose value>
AI_GENERATION_UNITS_RECOMMENDATIONS=<choose value>
```

Only successful `UsageScope=Paid` rows inside the billing period consume allowance. Intro acquisition rows remain costed but consume zero paid units. The period comes from the latest paid purchase's explicit monthly/yearly billing interval; unsupported intervals do not create an invented reset date.
