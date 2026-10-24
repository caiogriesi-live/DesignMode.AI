# Google Ads migration — Stage 1

> [Stage 2](ads-stage2.md), revised 2026-09-01, makes internal and Google advertising measurement always on for new traffic, retires preference evidence prospectively, and retains Stripe verification. It supersedes consent semantics below. Delivery remains off with no Google transport/OAuth.

Stage 1 is server-only infrastructure, **not a delivery rollout**. No Data Manager HTTP or OAuth implementation exists. No production configuration was enabled. GA4 payloads, canonical purchase identity/value, browser attribution and consent remain unchanged.

## Storage and provisioning

Provision `AdsDeliveries` in the application's DynamoDB account/region, using **on-demand (`PAY_PER_REQUEST`) capacity**, string partition key `Id` (canonical Purchase transaction UUID), no sort key, no GSI and no TTL. Runtime does not create tables. Grant its server role GetItem, PutItem and Scan on this table, plus existing Purchases/AttributionTouches read access. Do not give browser roles access.

Top-level attributes: `Id`, `Data` (serialized `AdsDeliveryRecord`), `AdsStatus` (string), `Revision` (number). `Data` contains provenance, environment/destination pinning, eligibility status/reason/version, transport version, attempt/diagnostic/request/hash placeholders, lease owner/expiry and timestamps; see the model for exact fields. It contains no click values, Stripe account IDs, customer data, credentials or raw HTTP bodies. Enum values inside Data use the default numeric .NET serialization; top-level AdsStatus is human-readable.

Use separate AWS accounts/roles or equivalently isolated deployments/databases for development and production. The existing DynamoDB factory selects the actual connection. Ads independently rejects local DB selection, including an unspecified `USE_LOCAL_DYNAMODB`. Environment labels are trusted server configuration, not cryptographic proof of the AWS account; IAM/deployment isolation must be enforced operationally.

## Configuration (server environment only)

| Variable | Meaning |
| --- | --- |
| `ADS_DELIVERY_MODE` | Default `off`. `dry-run` evaluates; `canary`/`enabled` are reserved and cannot send in Stage 1. |
| `ADS_ELIGIBILITY_VERSION` | Required version token, 1–64 alphanumeric/dot/underscore/hyphen characters. |
| `ADS_ACTIVATION_CUTOFF` | Required timestamp with explicit timezone, e.g. ISO 8601 UTC. Choose a future rollout boundary; do not backdate to activate historical rows. |
| `ADS_MEASUREMENT_ENVIRONMENT` | Exact `Production` required for eligibility. |
| `ADS_DESTINATION_CUSTOMER_ID` | Ten digits; pinned in each new sidecar. |
| `ADS_DESTINATION_CONVERSION_ACTION_ID` | 1–20 digits; pinned in each new sidecar. |
| `ADS_EXPECTED_STRIPE_ACCOUNT_ID` | Optional expected account for comparison with a signed event's explicit `account`. Absence never implies verification. |
| `AZURE_FUNCTIONS_ENVIRONMENT` | Existing host setting; exact `Production` required. |
| `USE_LOCAL_DYNAMODB` | Existing setting; explicit `false` required for Ads eligibility. |

The existing `MEASUREMENT_LEDGER_ENABLED=true` gates webhook ledger handling and the dry-run timer. Existing AWS region/credential routing and Functions timer storage prerequisites remain. No Google credentials are needed. Keep `GOOGLE_ADS_UPLOAD_ENABLED=false`: it is no longer consulted by the production purchase dispatcher, which dispatches GA4 only. Legacy upload code remains solely for compatibility tests/migration reference.

Invalid/missing configuration fails closed without changing GA4 startup/delivery. Off performs no sidecar writes/scans. Non-off modes may capture new provenance; only valid dry-run evaluates it.

## Provenance and eligibility

After signature verification and successful canonical recording, the handler captures root event `livemode`; a conflicting payment-object value makes provenance unknown. It does not read livemode from metadata, snapshots or requests. An explicit signed account must match configured expected account to set account verification true. **Platform events without `account` remain NeedsReview**: Stage 2 needs an authoritative platform-account binding mechanism, not an assumed true value.

Eligible requires all of:

- Matching nonempty canonical UUID; current version and pinned destination.
- Known versioned live Stripe provenance, verified account, nonfuture provenance time, both captured/current Production environment labels and nonlocal DB selection.
- Paid, first payment, not renewal; nonempty subscription ID and subscription billing reason. Conflicting first+renewal is NeedsReview.
- Advertising consent in the immutable checkout snapshot.
- Positive decimal amount (decimal cannot be NaN/infinite), positive minor units, USD and exact major/minor agreement.
- Valid nonfuture PaidAt, payment and provenance at/after cutoff, payment age at most seven days.
- One prioritized click selected: gclid, then gbraid, then wbraid. Known synthetic markers anywhere are rejected. Selected value is at most 512 ASCII alphanumeric/underscore/hyphen/dot characters. This does **not** prove Google click validity.
- Original server touch timestamp exists and matches the snapshot click fields/funnel. Click is not after payment; at most 30 days old at payment and evaluation. Checkout time cannot rejuvenate attribution.

Safe results are Eligible, NotEligible, Expired or NeedsReview with fixed machine reasons. No IDs/metadata are put in reasons/logs.

## Historical safety and dry-run

The timer `EvaluateAdsDryRun` runs every five minutes and scans **AdsDeliveries only**, selecting Pending/Eligible. It never scans Purchases to create sidecars. It loads the referenced purchase/touch, evaluates, conditionally saves status and reports aggregate `[ADS DRY RUN] Status:REASON: count` logs. `RunAsync` also returns the counts for tests/operators using trusted server code; no public inspection endpoint is added.

Existing purchases, duplicate webhook replays and missing provenance do not create new sidecars. Capture failure logs a safe warning and leaves GA4 unaffected; there is deliberately no automatic historical repair/backfill. A crash between purchase commit and sidecar capture requires manual review. Unknown or terminal rows are not automatically reactivated after configuration changes.

Dry-run never acquires Google credentials, performs delivery, increments attempts, or marks Accepted/Submitted. Eligible means policy eligible, **not sent**. New policy-versioned records do not require user consent evidence; historical records retain their prior gates.

## State and concurrency foundation

- Create: conditional insert Pending (`attribute_not_exists(Id)`).
- Evaluate Pending/Eligible → Eligible, NotEligible, Expired, NeedsReview, provided no active lease.
- Acquire Eligible/RetryScheduled/Accepted: five-minute opaque-owner lease; revision increments; honors next-attempt time.
- Leased Eligible/RetryScheduled → RetryScheduled, NotEligible, Expired, Rejected, NeedsReview.
- Accepted → Rejected/NeedsReview is supported. Accepted→Submitted is defined in the transition graph but blocked by Stage 1 completion code, as is transition into Accepted.
- Every replacement checks prior Revision **and** AdsStatus. A stale worker cannot overwrite a newer lease/attempt, even using its old clock. Terminal states cannot be automatically retried.

Attempt, request, payload-hash and diagnostic fields are reserved, not populated with fabricated delivery results. Stage 2 must implement acknowledgement/diagnostics-aware operations, bounded retries and fenced receipt persistence before enabling Accepted/Submitted.

## Before Stage 2

Provision/IAM-isolate the table; approve rollout cutoff/version/destination. Resolve platform-account verification. Implement advertising consent UI/evidence/current withdrawal checks separately. Then add Data Manager auth/transport, canary allowlisting, immutable payload/destination binding, attempt journaling, asynchronous diagnostics, reconciliation and bounded retry scheduling. Keep all production sending off until those tests and a controlled live validation pass. No automatic historical uploads are authorized.

## Verification

Run targeted tests with:

```powershell
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --filter 'FullyQualifiedName~AdsDeliveryTests|FullyQualifiedName~PurchaseMeasurementTests|FullyQualifiedName~StripePurchaseQualificationTests|FullyQualifiedName~Attribution|FullyQualifiedName~DynamoDbConfigurationTests'
node --test DesignMode.AI.Test/analytics.test.cjs
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj
```

Local integration tests require `RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true`, `USE_LOCAL_DYNAMODB=true` and a loopback `LOCAL_DYNAMODB_URL`. Use a disposable database, not production. Existing attribution integration tests expect their tables provisioned. Stripe and Google HTTP in this suite use mocks; the real signed-webhook path is exercised with a synthetic local signature. Tests cover the independent eligibility gates, prioritized clicks, configuration isolation, provenance, unchanged GA4 dispatch, conditional insert/update, lease contention and stale-worker fencing.
