# Stage 2 — always-on advertising measurement and Stripe provenance

Policy revision: **2026-09-01**, effective prospectively when deployed. Internal and Google advertising measurement are always enabled for new traffic. This is a product-policy statement, not a conclusion about legal compliance. Final wording and jurisdiction-specific affirmative-consent or opt-out requirements require legal review.

No Google transport or OAuth is added. Production must keep `ADS_DELIVERY_MODE=off` and `GOOGLE_ADS_UPLOAD_ENABLED=false`. No historical backfill is authorized.

## Prospective state

New server-created checkout contexts use:

- `Measurement.AdvertisingConsent=true` (legacy field name for internal measurement)
- `Measurement.AdvertisingMeasurementPolicyVersion=internal-always-on-2026-08-31`
- `Measurement.GoogleAdvertisingConsent=true`

The server ignores false or missing browser advertising fields when creating new snapshots. Purchases copy immutable snapshots. Deserialization does not default historical snapshots to true, and no AttributionTouches, AttributionSnapshots, Purchases, AdsDeliveries or AdvertisingConsents records are rewritten.

New AttributionTouches record both policy versions and enabled booleans. Click IDs and UTM/campaign capture remain unchanged and contain no added PII.

## Browser and Consent Mode

The shared privacy UI on Social, Blazor and privacy.html has no advertising toggle. It discloses always-on advertising measurement and retains independent analytics enable/disable controls.

The analytics wrapper forces its `advertising` compatibility property true. Old localStorage false cannot override it. On startup:

- analytics_storage follows the existing analytics policy (default granted; browser withdrawal remains supported)
- ad_storage = granted
- ad_user_data = granted
- ad_personalization = denied

Google Signals and advertising-personalization configuration remain disabled. Event filtering, URL sanitization, page deduplication and video behavior are unchanged.

## AdvertisingConsents retained as historical audit

The AdvertisingConsents table and all historical rows are retained without backfill or deletion. Runtime no longer creates, updates or requires preference/evidence rows. The compatibility API returns HTTP 410 Gone and does not write. Its old route/property names remain only to make the retirement explicit.

The table therefore no longer needs runtime IAM or HostUrl/CORS configuration for new deployments, unless retained operational tooling reads historical records. Do not remove the table or historical permissions until retention/legal review is complete.

Always-on policy is not labeled as a user-granted action. `AdvertisingConsentEvidence` is null for new snapshots.

## Ads eligibility and local dry-run

New policy-versioned records need no user consent row in either EvaluateCurrentAsync or EvaluateGoogleDeliveryAsync. Historical unversioned records retain their original consent and withdrawal checks.

Every non-consent safety gate remains: positive Paid first non-renewal subscription, valid transaction identity, live Stripe provenance, verified Stripe account, Production environment, nonlocal DynamoDB, activation cutoff, seven-day delivery budget, 30-day attribution window, pinned version/destination, prioritized valid click ID and synthetic-marker rejection. No transport is implemented; Eligible never means sent.

Sidecar capture occurs only after a newly persisted verified purchase. It returns before creation when mode is off or options are invalid. The timer evaluates existing AdsDeliveries only and never backfills Purchases.

Local testability is narrowly expanded: `ADS_DELIVERY_MODE=dry-run` plus explicit `USE_LOCAL_DYNAMODB=true` may omit destination customer/action IDs. Eligibility version and timezone-qualified cutoff remain required. This exception cannot apply to canary/enabled or nonlocal execution. Sidecars store empty destination fields and evaluation reaches NON_PRODUCTION_ENVIRONMENT (or earlier Stripe-test safety); synthetic clicks remain rejected. No Google account or fake destination ID is required.

MEASUREMENT_LEDGER_ENABLED=true remains required for verified purchase persistence and the five-minute evaluator. ADS_EXPECTED_STRIPE_ACCOUNT_ID is not syntax-validity input; a Stripe test event returns false before account lookup. Production live verification still requires the real platform account ID.

## GA4 and configuration

GA4 payment identity, transaction_id, value, currency, PaidAt, GA client/session IDs, dedupe and transport are unchanged. For new policy-versioned snapshots only, its ad_user_data field is GRANTED; ad_personalization remains DENIED.

Production requirements are unchanged: delivery off, legacy upload false, production labels, explicit nonlocal DynamoDB, valid eligibility version/cutoff/destination and expected Stripe account. Local dry-run uses the separate configuration documented above. No Data Manager credentials are introduced.

## Verification

Run targeted advertising, purchase, Stripe, attribution and DynamoDB tests, JavaScript analytics/privacy tests, server/client builds and `git diff --check`. For integration, use an isolated loopback DynamoDB instance and Stripe test fixtures with both Google delivery flags disabled. Verify sidecar creation, then confirm Stripe-test/local/synthetic gates prevent sendability. Never use a production database for this test.
