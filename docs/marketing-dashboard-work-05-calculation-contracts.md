# Marketing Dashboard WORK-05 calculation contract

WORK-05 supplies a deterministic, read-only aggregation layer for later reporting endpoints and the WORK-06 dashboard. It does not add UI, source writes, payment behavior, Ads mutations, or production infrastructure. Inputs are existing purchase projections, account-level Google Ads daily spend, and explicitly supplied optional cost bases. Tests use in-memory facts only.

## Definitions and output rules

All calculations use `decimal`; monetary outputs round to two fractional digits and ratios to four, both with `MidpointRounding.AwayFromZero`. Counts remain integers. A ratio with a zero or negative denominator is unavailable. Unsupported input is null with a state and explanation, never zero. Reporting currency is USD; foreign currency facts remain grouped in `NonUsdAmounts` without conversion.

| Metric | WORK-05 definition | Current limitation |
| --- | --- | --- |
| Advertising Spend | Sum of USD account-level daily Ads spend for selected inclusive dates | Google Ads source has no campaign split today |
| Eligible Revenue | USD qualifying positive paid purchase facts | Partial until source history and adjustments are validated |
| ROAS | Transaction-snapshot Google-attributed eligible revenue / positive USD Ads spend | Revenue facts are transaction-attributed, but account-level spend is not campaign-matched |
| New Customers | Distinct `CustomerKey` with an attributed first subscription payment in selected period | Conservative subset; excludes all-channel or non-Google acquisition |
| CAC | USD account Ads spend / distinct attributed new customers | Not campaign-matched; unavailable without new-customer denominator |
| ARPC | USD eligible revenue / distinct customers with eligible USD payments in period | Period payer average, not lifetime value |
| Contribution Before Ads | Revenue - explicitly supplied variable costs excluding advertising | Existing expense model does not classify variable/fixed costs, so currently unavailable |
| Contribution After Ads | Contribution Before Ads - Ads spend | Same missing variable-cost basis |
| Contribution per Customer | Contribution After Ads / eligible customers | Requires supported cost basis and customers |
| Payback | CAC / explicitly supplied monthly contribution per customer | No recurring revenue assumption; unavailable without this separately supported basis |
| Campaign profitability | Campaign attributed revenue - supported campaign variable costs - campaign spend | Existing Ads source supplies no campaigns or campaign-attributed conversions |
| Marginal ROAS/CAC | Selected minus equal-duration prior period revenue/spend/customer deltas | Observational only; positive denominator changes required; not causal |

Purchase customer keys are internal aggregation inputs and must never be serialized to dashboard clients. Duplicate/conflicting transactions are not deduplicated by this calculator; source projections must guarantee transaction uniqueness or mark coverage partial before aggregation. No currency conversion or arbitrary allocation is performed.

## Campaign actions

`CampaignRecommendationPolicy` requires a positive `TargetRoas`, nonnegative `MinimumRoas`, and minimum customer count. When a positive `TargetCac` is configured and measured CAC exceeds it, the action is REDUCE. Otherwise, ROAS at or above target yields SCALE, ROAS below minimum yields PAUSE, and the interval yields HOLD. Invalid or absent policy/evidence yields UNAVAILABLE. Recommendations are advisory, carry limitations/rationale, and perform no Ads API mutation.

## Source readiness and configuration audit

Values below describe repository-defined integration points only. Actual local process values and production settings were not read or verified. Preserve existing names and store credentials in the developer's secret store or the deployed Function App settings; never commit them.

| Field | Required information | Work item / integration | Exact setting | Type / format | Local location | Production location | Minimum permissions | Status | Safe verification |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Reporting date basis | Inclusive New York calendar dates; previous equal-length interval | WORK-01 / reporting contracts | None | IANA zone `America/New_York` | Client selection passed to server | Same contract | None | Unit tests cover independent UTC boundaries and DST |
| Shared report contracts | Measures, states, periods and source coverage | WORK-01 / shared models | None | .NET DTOs | Build | Build/deploy with app | None | Compile shared project |
| Google Ads reporting customer | Target account identity | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_CUSTOMER_ID` | Digits only, no hyphens | Server process environment / user secrets | Function App setting | Google Ads reporting access to target account | UNVERIFIED | Call existing read-only daily spend source with a bounded period |
| Google Ads manager account | Manager login identity when target account is managed | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID` | Digits only; optional for direct accounts | Server process environment | Function App setting | Read access via manager relationship | UNVERIFIED | Same read-only request; omit only for direct account |
| Google Ads developer token | API developer access | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN` | Secret string | Secret manager/environment | Function App secret setting | Google Ads API access | UNVERIFIED | Successful read-only report response; do not print token |
| Google OAuth client | API OAuth application | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID`, `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET`, `GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN` | Client ID and secrets | Secret manager/environment | Function App secret settings | OAuth user authorized for Google Ads read/reporting | UNVERIFIED | Short-period read-only spend query |
| Google Ads API version | Endpoint version | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_API_VERSION` | `vNN` | Server environment | Function App setting | None | UNVERIFIED | Confirm configured version is accepted by report source |
| Stripe revenue source | Canonical positive paid `Purchases`; not a full balance ledger | WORK-03 / Stripe purchase reporting | No new reporting key; existing `StripeSecretKey` is used by payment paths, not this purchase-table scan | Stripe secret key is sensitive; DynamoDB records read-only | Existing local developer secret config | Existing Function App secret setting | Reporting runtime needs read access to `Purchases`; do not grant write for reporting | PARTIAL | Use isolated/sample purchase records or approved read-only source scan; do not issue payment |
| Stripe account identity | Account binding and live/test mode evidence | WORK-03 / Stripe | No reporting account-ID setting found in this source path | Stripe account identifier | Verify through existing Stripe account verification process | Verify deployed account separately | Read-only account inspection | UNVERIFIED | Confirm account identity without creating transactions |
| Stripe products/prices | Product IDs are not used by purchase aggregation | WORK-03 / Stripe | No reporting Product/Price ID dependency found | N/A | Not required for aggregation | Not required for aggregation | None | NOT REQUIRED | Existing purchase record classification tests |
| Stripe webhook | Existing canonical purchase ingestion dependency, not invoked by calculation | WORK-03 / Stripe | `StripeWebhookSecret` | Webhook signing secret | Existing secret configuration | Function App secret setting | Webhook verification only | UNVERIFIED | Existing signature/replay tests; do not send live event |
| Purchases table | Canonical purchase source | WORK-03 / DynamoDB | Literal `Purchases` | DynamoDB table, `Data` string payload currently scanned; report reader scans table, so no exact key requirement for this read path | Local DynamoDB or isolated AWS account/region | Existing production table; no provisioning here | `dynamodb:Scan` on table (read-only); AWS credential chain | UNVERIFIED | Read a bounded test environment; scan currently reads whole table |
| Business Expenses table | Operator expense data | WORK-04 / DynamoDB | `BusinessExpensesTableName` or default `BusinessExpenses` | DynamoDB table, partition key `ExpenseId` (String), no sort key; `Date`, `Currency`, `Amount` (Number), `Category`, `Description` attributes | Local DynamoDB or isolated AWS table | Function App setting and table in configured region | `Scan`, `GetItem`, `PutItem` on exact table; listing currently scans with Date/Currency filters; create/edit uses PutItem | UNVERIFIED | Create/read/edit/delete only isolated test item/table |
| DynamoDB connection | Region and local/remote choice | WORK-04 / DynamoDB | `AWS_REGION`; `USE_LOCAL_DYNAMODB`; optional `LOCAL_DYNAMODB_URL` | AWS region; boolean; absolute HTTP(S) URL | Process env; local endpoint default is `http://localhost:8000` when local true | Function App settings; AWS mode requires explicit region | AWS credential chain with least-privilege table policy | UNVERIFIED | Startup selection check then isolated table operation |
| Expense operator access | Email allowlist and JWT identity | WORK-04 / authorization | `DESIGNMODE_COMPANY_OPERATOR_EMAILS` | Comma-separated email addresses | Server environment | Function App setting | No AWS access for client; server enforces JWT and allowlist | UNVERIFIED | Authorized operator succeeds; unauthenticated/customer request denied |
| GA4 Measurement Protocol | Existing consent-aware purchase event delivery | Existing GA4 / GA4 | `GA4_SERVER_ENABLED`, `GA4_MEASUREMENT_ID`, `GA4_API_SECRET` | Boolean string `true`; `G-...`; secret | Server environment/secret store | Function App settings/secret store | GA4 Measurement Protocol secret; consent checks remain active | UNVERIFIED | Validate debug/test events in GA4; HTTP acceptance alone is not ingestion proof |
| GA4 event data | Purchase conversion only from verified purchase dispatch with analytics consent; first-payment session association is constrained | Existing GA4 / GA4 | No separate funnel analytics reporting setting found | `purchase` event with transaction ID, value, currency, item | Existing client/server flow | Same deployed purchase dispatcher | Analytics consent and valid client/session evidence | PARTIAL | Inspect GA4 DebugView/Realtime using test-mode purchase flow and consent; never use live payment for QA |
| Funnel tracking | Existing social-content funnel and attribution snapshot infrastructure is reusable as input evidence | Existing GA4 / funnel | No standalone GA4 funnel-reporting setting found | Existing FunnelId/snapshot data | Existing app | Existing app | Existing authorization | PARTIAL | Verify event/funnel rows in isolated test data; GA4 does not itself prove revenue coverage |

### Safe templates

PowerShell session examples (fill placeholders locally; do not paste secret values into source control):

```powershell
$env:GOOGLE_ADS_REPORTING_CUSTOMER_ID = '<customer-id-digits>'
$env:GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID = '<manager-id-digits-if-needed>'
$env:GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN = '<secret>'
$env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID = '<oauth-client-id>'
$env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET = '<secret>'
$env:GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN = '<secret>'
$env:GOOGLE_ADS_REPORTING_API_VERSION = '<supported-vNN>'
$env:AWS_REGION = '<region>'
$env:DESIGNMODE_COMPANY_OPERATOR_EMAILS = '<operator@example.test>'
```

Local DynamoDB example: set `USE_LOCAL_DYNAMODB=true`, `AWS_REGION=us-east-1`, optionally `LOCAL_DYNAMODB_URL=http://localhost:8000`, then create the `BusinessExpenses` table with on-demand capacity and string partition key `ExpenseId`; no sort key is used. The current query filters `Date` and `Currency` after keying only by `ExpenseId`, so this is a table scan-based index design rather than a date/currency key schema. Production provisioning is explicitly out of scope. For AWS mode, omit `USE_LOCAL_DYNAMODB` or set false, specify the actual `AWS_REGION`, and use the standard AWS SDK credential chain. Do not copy local settings files to production.

Existing local GA4 values already supplied by the operator (`GA4_MEASUREMENT_ID` and `GA4_API_SECRET`) do not establish that server delivery is enabled or verified; check `GA4_SERVER_ENABLED` and safe GA4 DebugView evidence separately. This document does not record their values.

## WORK-06 handoff and visual mapping

The supplied ROAS reference maps to later UI components as follows: KPI cards consume `MarketingFinancialMeasures`; spend/revenue/customer and ROAS trend charts use selected/comparison period series from read-only sources; campaign table consumes `CampaignFinancialReport`; marginal panels consume `MarginalPerformanceReport`; recommendation cards display action, rationale, limitations, source state, and confidence/coverage as available. Current account-level Ads source cannot populate real campaign rows. Do not fill missing campaign data with illustrative reference-image amounts. Keep the existing dark navigation and light content shell, white cards, subtle borders, blue accents, readable spacing, and responsive table/chart layout. WORK-05 implements no page or visual redesign.

## Human QA and safe verification

The calculator tests use synthetic facts and never call Google, Stripe, GA4, AWS, or production. To validate source integration, use an isolated local DynamoDB table and test purchase facts, a read-only Google Ads report identity, and a read-only Stripe/Dynamo source identity approved by the account owner. Compare one bounded period, verify source states and date bounds, and confirm unavailable fees/refunds/cost bases stay null. Avoid production financial rows during manual QA. The repository currently exposes no marketing report endpoint, so end-to-end dashboard acceptance belongs to WORK-06 after an authorized API/UI is added.
