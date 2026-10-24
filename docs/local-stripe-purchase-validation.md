# Local Stripe purchase validation

## Follow-up: live webhook wiring audit

The subsequent browser test confirms Stripe forwarding and legacy Subscriptions updates. The current source already wires purchase persistence into the registered webhook; no second invocation was added. Current local settings have MEASUREMENT_LEDGER_ENABLED=true and USE_LOCAL_DYNAMODB=true, with no endpoint override. The earlier missing-flag finding below is historical, not an explanation proven for this subsequent failure.

Concrete wiring evidence:

- `Program.cs` registers PurchaseMeasurementStore, StripeVerifiedPurchaseHandler and IStripeVerifiedPaymentResolver as singletons.
- `DesignModeFunctions` requires StripeVerifiedPurchaseHandler in its constructor and assigns the field used by StripeWebhook.
- `[Function("StripeWebhook")]` and its POST HttpTrigger register the default `/api/StripeWebhook` route. Generated functions.metadata names `DesignMode.AI.Server.DesignModeFunctions.StripeWebhook` as the entry point; no alternative registered implementation was found.
- StripeWebhook verifies the raw body, then **awaits HandleVerifiedAsync before the legacy switch**. Invalid signatures/missing settings return before this call. Resolution/write exceptions return 500 rather than proceeding through the switch.
- HandleVerifiedAsync checks the ledger flag, supported event type and existing deterministic purchase, calls ResolveAsync, then awaits RecordAsync for a qualifying payment.
- `PurchaseMeasurementStore.RecordAsync` constructs a `TransactWriteItem.Put` with literal TableName=`Purchases`, then executes `db.TransactWriteItemsAsync`. There is no separate CreatePurchase method, BatchWrite, or standalone PutItemAsync for canonical purchase creation.
- The later invoice.paid switch branch only validates the deserialized Invoice and logs `Invoice Paid`. It does **not** update Subscriptions. Those writes occur for customer.subscription.created/updated and the legacy one-time payment branch. Their success does not establish invoice qualification/persistence.

Purchases uses string HASH key Id, computed from SHA-256 of `stripe-purchase:v1:` plus the invoice/payment ID (first 16 bytes as a GUID). Its transaction condition is attribute_not_exists(Id). PurchaseCustomers is inserted atomically when absent, or conditionally updated for an earlier unclaimed first payment; an existing marker does not prevent a distinct payment from being recorded. On transaction cancellation the code rereads the deterministic purchase before declaring a duplicate. Other cancellation causes fail, and exhausted contention retries now have an explicit error log.

Other references are the store's reads/delivery status updates, PurchaseConversionDispatcher, the delivery timer, PaidWorkspaceMeasurementFunction's read, tests, and `scripts/Initialize-MeasurementTables.ps1`. The initializer provisions Purchases and PurchaseCustomers with Id String HASH and no sort key. No additional canonical creation path was found.

Both legacy Subscriptions writes and the purchase store use DynamoDbClientFactory. Current local file settings resolve to localhost:8000, region us-east-1, with the same local credentials. This proves common application routing, not that an external table viewer uses the same endpoint/region/credential namespace or that the running process loaded the latest settings. Confirm its startup selection and restart the rebuilt Functions host before retesting.

Expected invoice sequence after this audit:

```text
[STRIPE PURCHASE] webhook request received
[STRIPE PURCHASE] signature verified
[STRIPE WEBHOOK] invoice.paid received
[STRIPE PURCHASE] handler invoked
[STRIPE PURCHASE] event accepted: invoice.paid
[STRIPE PURCHASE] payment qualifies
[STRIPE PURCHASE] qualification passed
[STRIPE PURCHASE] persistence invoked
[STRIPE PURCHASE] checkout context resolved
[STRIPE PURCHASE] canonical purchase write started
[STRIPE PURCHASE] canonical purchase created
[STRIPE PURCHASE] GA4 purchase delivery queued
Invoice Paid
```

`[STRIPE WEBHOOK] legacy subscription update completed` is emitted only after an actual legacy write, on its separate event path; it is intentionally not emitted for invoice.paid. A duplicate ends purchase processing with `duplicate purchase ignored`. Skip/failure logs identify the stage without IDs, payloads, secrets or URLs.

The integration test now calls **DesignModeFunctions.StripeWebhook(HttpRequest)** with a signed invoice body, uses the real resolver with mocked Stripe invoice-history HTTP, and persists to isolated DynamoDB Local. It asserts exactly one persistence invocation, canonical creation and GA4 Pending enqueue across two signed deliveries; tampering returns 400 and creates no purchase. Removing the actual webhook-to-handler call would fail these assertions. The test does not emulate Functions host activation or prove which binary handled the user's earlier request.

Follow-up validation: all 17 relevant tests passed with no skips; full solution build passed with zero errors and 651 warnings. The separate ephemeral database on localhost:8001 was used only for tests, not the user's application database. No live Stripe/Google calls or configuration changes were made in this follow-up.

## Confirmed failure and limits of evidence

The development `local.settings.json` omitted `MEASUREMENT_LEDGER_ENABLED`. The verified handler silently returned unless this was true, so even a correctly delivered/signed payment could not create Purchases. The local ignored settings file now enables the ledger. The global default remains disabled; production settings were not changed.

At inspection, no Functions listener or Stripe forwarding process was visible, and the local Docker engine was stopped. This does not prove whether an earlier webhook arrived. No Stripe account delivery history was accessed. Actual earlier event type, signature result and correlation remain unverified. Safe diagnostics now distinguish these stages.

The local GA4_SERVER_ENABLED setting was off. This cannot prevent purchase persistence; it only prevents the downstream send. No GA4 settings or secrets were changed.

## Call chain

FunnelEnd's visible CTA uses subscription mode (`isOneTime=false`). CreateCheckoutSession saves CheckoutAttempts and AttributionSnapshots, writes `checkoutAttemptId` into both Checkout Session metadata and SubscriptionData.Metadata, then saves StripeCheckoutSessionId. ClientReferenceId contains the canonical user, not the checkout attempt. One-time sessions use session metadata; PaymentIntent metadata is not required because the resolver looks up the session.

POST /api/StripeWebhook → exact raw body + Stripe-Signature → StripeWebhookVerification / EventUtility.ConstructEvent → StripeVerifiedPurchaseHandler → StripeVerifiedPaymentResolver → PurchaseMeasurementStore → atomic Purchases/PurchaseCustomers transaction.

Subscription payments qualify from positive paid invoice.paid, excluding out-of-band invoices. The resolver reads current `parent.subscription_details` or legacy subscription details, falls back to the Stripe Subscription metadata, and checks earlier positive invoices. One-time payment_intent.succeeded must resolve a Checkout Session with mode=payment and no subscription. Subscription payment intents are not recorded a second time.

Purchases uses the deterministic opaque ID derived from invoice/payment ID, not webhook event ID. Missing attempts/snapshots fail with HTTP 500 for retry/reconciliation rather than creating malformed rows. Unsupported/unqualified events return normally. Signature failures return HTTP 400; missing webhook secret and resolution/persistence failures return HTTP 500. New logs never include payloads, keys or identifiers; legacy sensitive webhook logs were removed.

The purchase transaction sets Ga4Status=Pending when the checkout snapshot allows analytics. This is the durable delivery queue, not a separate queue service. DeliverPurchaseMeasurements runs every five minutes → PendingAsync → PurchaseConversionDispatcher → existing Measurement Protocol transport. No Purchases row means no server purchase send. Google failure never recreates a purchase.

## Manual local setup

1. Start your actual DynamoDB Local database at localhost:8000. The app uses USE_LOCAL_DYNAMODB=true, optional LOCAL_DYNAMODB_URL (default localhost:8000), and AWS_REGION (local default us-east-1). Keep the database containing the original CheckoutAttempts and AttributionSnapshots; a fresh empty database cannot correlate a prior checkout.
2. Provision Purchases, PurchaseCustomers and AttributionSnapshots, each Id String HASH, no sort key, with the existing local initializer. No schema changes were made. Confirm the existing checkout tables are present too.
3. Keep the Functions host running separately from the client; start it with `dotnet run --project DesignMode.AI.Server -- --port 7032`. AzureWebJobsStorage must be usable for the delivery timer (local configuration uses development storage, so Azurite is needed).
4. In another terminal, authenticate Stripe CLI to the same test account used by StripeSecretKey, then run:

```powershell
stripe listen --events checkout.session.completed,customer.subscription.created,customer.subscription.updated,invoice.paid,payment_intent.succeeded --forward-to http://localhost:7032/api/StripeWebhook
```

5. Set local `StripeWebhookSecret` to the signing secret printed by that listener. Do not use the Dashboard endpoint secret for CLI-forwarded requests. Restart the Functions host after changing settings. Keep the listener running while completing a new real Checkout test flow; generic Stripe trigger fixtures lack this application's checkout metadata.
6. The endpoint uses AuthorizationLevel.Function. Local Core Tools normally does not enforce function keys. If you explicitly enable local authentication, supply a function key via x-functions-key forwarding. Hosted endpoints require a function key unless authorization is changed separately. The route is case-preserved `/api/StripeWebhook`; method POST. No tunnel is needed with Stripe CLI forwarding. Stripe cannot directly call localhost; Dashboard destinations may still point to production or another endpoint, so check CLI's forwarding response rather than assuming browser success proves webhook delivery.

Use the CLI secret according to [Stripe's local webhook guidance](https://docs.stripe.com/webhooks?lang=dotnet). No signing secret was printed or committed by this task.

## Successful log sequence

```text
[STRIPE PURCHASE] webhook request received
[STRIPE PURCHASE] signature verified
[STRIPE PURCHASE] event accepted: invoice.paid
[STRIPE PURCHASE] payment qualifies
[STRIPE PURCHASE] checkout context resolved
[STRIPE PURCHASE] canonical purchase write started
[STRIPE PURCHASE] canonical purchase created
[STRIPE PURCHASE] GA4 purchase delivery queued
```

Replay should log `duplicate purchase ignored`. Missing configuration, unsupported event, zero amount, unpaid invoice, out-of-band payment, missing one-time session and missing attempt metadata have explicit safe reasons. A queued message means durable Pending state, not Google's acknowledgement.

## Inspect the purchase before checking GA4

Inspect top-level Id, TransactionId, UserId, ProjectId, FunnelId, Amount, Currency, PaymentStatus=Paid, IsFirstPayment, IsRenewal, Ga4Status and AdsStatus. The Data JSON also includes CheckoutAttemptId, AttributionSnapshotId, AcquisitionId, StripeTransactionId, StripeEventId, StripeSubscriptionId, AmountMinor, Plan, BillingInterval, BillingReason, CreatedAt, PaidAt and Snapshot. Inspect these internally only; do not paste identifiers or secrets into public diagnostics.

Expect one row per actual positive payment, one PurchaseCustomers marker for the canonical user, and no extra purchase on webhook replay. Existing marker ownership affects acquisition classification, not whether a distinct positive payment is persisted.

Only after this row exists, check GA4_SERVER_ENABLED, GA4_MEASUREMENT_ID and GA4_API_SECRET in the server environment, plus Snapshot.Measurement.AnalyticsConsent and Google client ID. Wait for the timer and inspect Ga4Status and safe MEASUREMENT logs. Submitted means HTTP acceptance, not proof of ingestion. The existing server MP payload does not set debug_mode; do not assume it will appear in DebugView merely because browser debug is enabled. Verify Realtime/reports or validate a safe payload in a test stream. Do not reset Unknown/Rejected statuses or replay to Google blindly.

## Verification scope

Qualification and signature tests use mocked Stripe HTTP and synthetic signatures. Purchase tests use an isolated in-memory DynamoDB Local instance and mocked Google transport, covering canonical creation, snapshot correlation, replay/concurrency, Pending status, first versus renewal, missing attempt, zero amount and transport failure without another purchase. No live Stripe, GA4 or production AWS calls are made.

Validation completed: 17 tests passed, zero failed or skipped, using `dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-build --filter 'FullyQualifiedName~PurchaseMeasurementTests|FullyQualifiedName~StripePurchaseQualificationTests'`. Integration tests ran with RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true, USE_LOCAL_DYNAMODB=true and LOCAL_DYNAMODB_URL=http://localhost:8001 against a separate disposable test database, not the application's localhost:8000 database. Full solution build passed with `dotnet build DesignMode.AI.sln --no-restore -p:CompressionEnabled=false -clp:ErrorsOnly` (latest build: 29 warnings, zero errors). These tests verify the signature, qualification and persistence boundaries; they do not establish delivery of the earlier real Stripe payment through a running Functions HTTP host.
