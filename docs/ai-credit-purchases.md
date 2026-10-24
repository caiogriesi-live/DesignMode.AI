# AI Credit top-ups

## Implementation and audit

Authenticated users can open the shared Add Credits dialog from the workspace header
or credit meter on Dashboard and Gen at any balance. The modal and payment polling are
mounted only on those authenticated routes, including Gen project routes. Public pages
never resume a pending checkout. Leaving the workspace cancels polling and closes the
shared modal state; Account links to the dashboard to manage credits.
Packages are exactly $20/20, $40/40,
and $60/60 credits. Primary purchase and upgrade labels use explicit white text.

Configured Plus/Extreme upgrade controls open Stripe's subscription update confirmation
for a single active monthly subscription owned by the authenticated user. The test
portal configuration allows price changes with Stripe-displayed prorations and returns
to `/init/dashboard` after confirmation. One-time Starter accounts, older subscriptions
without checkout attribution, annual plans, scheduled cancellations, and ambiguous
multiple subscriptions require assisted migration; they are never given a second
subscription automatically. Enable the measurement ledger and configure
`AI_UPGRADES_TEST_*` or `AI_UPGRADES_LIVE_*` price and portal configuration IDs.
Paid invoices resolve the configured new price to its plan. Mid-period updates preserve
the original billing-period start and usage, and cannot become acquisition conversions.
No allowance is granted by the upgrade button or return redirect.

`BILLING_ALLOWED_ORIGINS` is a comma-separated HTTPS origin allowlist for checkout
returns. Defaults are the bare and www DesignMode domains; loopback HTTP/HTTPS is
accepted only in test mode. Request-supplied paths and arbitrary origins are rejected.

The existing `StripeWebhook` verifies the signature before routing credit payments.
`payment_intent.succeeded` is the only grant event. The server retrieves its Checkout
Session and line items, verifies the legacy Payment Link ID (when present), price ID, one-time
mode, quantity one, exact USD price, paid status, and test/live mode. Before redirecting
to Stripe, it also retrieves the configured link to verify the URL and package.
An authenticated checkout creates a random `client_reference_id` bound to `Users.Id`.
An edited reference cannot select an arbitrary user: it must resolve to a server-owned
attempt for that package and environment. Direct purchases without this reference
are rejected for manual reconciliation; never distribute the bare links to customers.

`Purchases` is reused for canonical receipts (`PurchaseType=AICredits`), including
package, credits, amount/currency, user, Stripe event/session/payment identity and time.
The existing transaction-ID function deduplicates by PaymentIntent. The receipt,
wallet grant, and checkout status are one DynamoDB transaction. No project balance,
negative generation rows, subscription-limit changes, acquisition claims, GA4 purchase
deliveries, or Ads deliveries are created for top-ups.

Subscription events already append history to `Subscriptions`. Account derives
`SubscriptionSignupCount` by distinct Stripe subscription identities with an active
or trialing record. The existing paid one-time Starter activation also counts by its
distinct PaymentIntent. Incomplete/failed attempts, retries, renewals, cancellation,
and credit purchases do not add signups. History that was deleted outside the app
cannot be reconstructed by this count.

## Purchased credit expiration policy

There was no existing purchased-credit expiration policy or entitlement store.
Purchased credits have no TTL and persist through renewal, cancellation, and
resubscription. They belong to the user across projects. Existing project-access
authorization remains required. No new subscription access rights are inferred from
a top-up. An ended paid billing period supplies zero base credits; purchased credits
remain available. The pre-existing yearly/monthly period calculation is retained.
Missing or unrecognized billing intervals also supply no assumed base allowance when
the ledger is enabled, while preserving purchased value. This includes legacy one-time
Starter rows without a canonical billing period; establish their base allowance policy
before enabling ledger enforcement for those accounts. No monthly interval is invented.

Consumption uses the current plan allowance first, then purchased credits. A wallet
revision condition serializes reservations across application instances. Reservations
are made before paid provider calls and are unavailable to simultaneous requests.
Confirmed failure releases a reservation once; success consumes it once. Reused paid
generation identities are rejected. A billing-period transition never expires the
purchased balance or lets a late failure refill the new period's base allowance.

Existing best-effort usage accounting is no longer allowed to bypass a failed paid
reservation. Kit generators now record one positive-unit job and leave their provider
operation rows as cost records. Existing configured feature weights are retained.

## Stripe configuration required

Apply configuration separately in each environment. The example settings include
the verified Payment Link and Price IDs for the three supplied sandbox links; live
mappings remain empty and both feature flags default to disabled. The sandbox catalog
flag alone only shows a preview: it does not enable checkout.

For local testing, merge the test mappings into the ignored `local.settings.json`,
set `AI_CREDITS_MODE=Test`, `AI_CREDITS_LEDGER_ENABLED=true`, and
`AI_CREDITS_PURCHASES_ENABLED=true`, and use the matching test key. Start the existing
DynamoDB Local database, then run `scripts/Initialize-MeasurementTables.ps1` to create
the ledger tables. Forward events with the Stripe CLI:

```powershell
stripe listen --events checkout.session.completed,customer.subscription.created,customer.subscription.updated,invoice.paid,payment_intent.succeeded,charge.refunded,charge.dispute.created --forward-to http://localhost:7032/api/StripeWebhook
```

Use the listener's signing secret in the local webhook configuration and restart the
Functions host after configuration changes. A webhook registered against a hosted
server does not deliver events to localhost. Return to the app after the test payment
to refresh the verified balance; a checkout redirect alone never grants credits.

1. Provision `AICreditLedgerTest` for test and `AICreditLedger` for live, each with string
   hash key `Id`, no sort key, no TTL. Use on-demand billing and production backups as
   appropriate. Existing `Purchases` needs no key/index migration. Grant the service
   GetItem/PutItem/UpdateItem/TransactWriteItems access to its ledger and Purchases.
   `scripts/Initialize-MeasurementTables.ps1` can provision only a loopback DynamoDB
   Local instance; it never provisions AWS.
2. Drain in-flight generation jobs before enabling `AI_CREDITS_LEDGER_ENABLED=true`.
   The first reservation seeds base usage from existing successful usage. All writers
   must run this version before enablement; a mixed old/new rollout can bypass holds.
   Keep the ledger enabled after purchases exist. Stop sales by setting
   `AI_CREDITS_PURCHASES_ENABLED=false`, not by disabling accounting.
3. Set `AI_CREDITS_MODE=Test` or `Live`, and use the matching existing `StripeSecretKey`
   and webhook signing secret. Test checkout is blocked when
   `AZURE_FUNCTIONS_ENVIRONMENT=Production` unless the operator explicitly sets
   `AI_CREDITS_ALLOW_HOSTED_TEST_PAYMENTS=true` for hosted sandbox testing. Keep the
   Azure environment set to Production; the opt-in still requires a test key and test
   package mappings. Test and live wallets use different tables.
   Use separate deployment/database environments for test, including existing billing
   and usage data; this feature does not retrofit isolation of legacy billing records.
4. For each `20`, `40`, and `60` package, configure
   `AI_CREDITS_TEST_<quantity>_PAYMENT_LINK_ID` (`plink_...`) and
   `AI_CREDITS_TEST_<quantity>_PRICE_ID` (`price_...`). The supplied test URLs are already
   in the server catalog; optional `..._PAYMENT_LINK` overrides are validated. Public
   URL suffixes are not Stripe API Payment Link IDs. Look up IDs in the matching Stripe
   sandbox. All three prices must be one-time USD, quantity one, with no adjustable
   quantities, coupons, automatic taxes, shipping charges, or additional line items.
5. Enable `payment_intent.succeeded`, `charge.refunded`, and `charge.dispute.created`
   on the existing signed webhook. Preserve existing subscription events. A delayed
   payment adds credits only on its successful PaymentIntent event. Configure each
   environment's webhook before accepting payments. New top-ups use authenticated
   Checkout Sessions with the validated package price and return to
   `/init/dashboard?credits=return` on the initiating app origin. That redirect never grants
   credits. Previously opened Payment Links retain their existing completion settings.
6. Only after verifying the above, set `AI_CREDITS_PURCHASES_ENABLED=true`.

Turning off sales does not prevent fulfillment of already-started, correctly configured
payments. Keep package mappings and the matching Stripe credential until outstanding
payments and refunds have been reconciled.

See [Stripe Payment Link URL parameters](https://docs.stripe.com/payment-links/url-parameters)
and [Stripe event types](https://docs.stripe.com/api/events/types).

## PROD STRIPE CREDIT LINKS REQUIRED

Production links were not supplied and are not invented. For live checkout configure
`AI_CREDITS_LIVE_<quantity>_PAYMENT_LINK`, `..._PAYMENT_LINK_ID`, and `..._PRICE_ID`
for all packages. There is no fallback to test checkout. Missing/mismatched settings
leave purchase buttons disabled or return a safe configuration error.

## Refunds, disputes, and operational recovery

No existing credit-refund or dispute entitlement handling was found. Partial refunds
revoke the proportional credits, rounded up to one hundredth of a credit; a dispute
revokes the full grant. Reversal processing uses current Stripe charge state and a
monotonic cumulative revoked quantity, so duplicate and out-of-order events cannot
subtract twice or restore value. Already-spent refunded credits become debt and block
new paid generations until resolved. A new payment offsets that debt. Failed provider
operations still return their held credits. Original payment facts remain auditable.

REFUND HANDLING FOLLOW-UP REQUIRED: dispute wins and failed/reversed refunds require
audited manual review before reinstating credits. No automatic positive adjustment
is inferred from a later dispute event. Verify the refund/dispute subscriptions on
the live webhook before enabling sales and monitor unsuccessful webhook deliveries.

Unknown provider outcomes (timeouts/crashes), interrupted multi-asset jobs, or a
failure between reservation and usage persistence retain their holds. They are never
automatically expired while provider work could still complete. Reconcile the
`reservation:<usageId>` against provider/job evidence, then call the existing usage
completion path (success or confirmed failure); if no usage row was persisted, an
operator can invoke `AICreditLedger.CompleteAsync` after confirming no work ran.
Do not blindly edit wallet numbers or delete reservation/job identities. A general
automatic provider-reconciliation worker is outside this change.

## Verification

Run `dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj -p:CompressionEnabled=false`.
The compression override works around a local .NET 10 static-web-asset error; it is
not a production setting. Unit tests use offline Stripe models and an atomic DynamoDB
test double for duplicate delivery, transaction failure, concurrency, rollover,
base-first consumption, refunds/debt, configuration isolation and signup history.

Before enabling sales in an environment, verify all three sandbox packages through
real Stripe checkout, redeliver the same webhook, test a delayed payment, partial/full
refund and dispute, two simultaneous final-credit requests, and return-page polling.
No real or test payment was submitted during implementation.

### Local verification result (2026-09-24)

- Base commit: `5c9a2c342c16ebe73ecfe8e3c98fac56d592106e`.
- `dotnet build DesignMode.AI.sln --no-restore -p:CompressionEnabled=false`: passed,
  0 errors, 29 existing dependency/licensing warnings.
- Full test suite: 933 passed, 19 skipped local-integration tests, 6 failures in
  unchanged baseline code: four `Sequence_UsesResponsiveSharedLayoutAndSafeResume`
  cases, `LargeGenModal_OptsIntoWorkspaceLayout (MakeVideoModal.razor)`, and
  `WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable`.
- All new credit-purchase tests and credit/account tests pass.
- Rendered the actual credit dialog component with fixture data and inspected desktop,
  mobile, and tablet layouts in the browser. Cards stack on mobile, the dialog scrolls
  vertically, content has no horizontal overflow, and purchase buttons have 44px minimum
  height. This was a component preview, not a live authenticated payment test.
- Stripe end-to-end and real DynamoDB transaction tests remain environment checks;
  credentials/configuration were not changed and no checkout payment was submitted.
