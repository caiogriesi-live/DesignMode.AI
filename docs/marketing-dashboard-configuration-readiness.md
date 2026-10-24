# Marketing Dashboard configuration readiness (WORK-01–WORK-04)

This audit records source-level dependencies and the verification status available on 2026-10-09. No local or production secret values were read. A source setting is **UNVERIFIED** when its actual local/production value or provider-side permission could not be safely confirmed. Do not treat unverified source coverage as complete.

## Readiness register

| Field | Work item / integration | Exact setting or dependency | Required value / format | Local location | Production location | Minimum permissions | Status | Safe verification |
|---|---|---|---|---|---|---|---|---|
| Reporting periods and measures | WORK-01 / shared reporting | `MarketingReportingContracts.cs` | Reporting DTOs; inclusive calendar dates in `America/New_York`; comparison is preceding equal-length local-date interval; USD basis; missing measure stays null with state | No extra setting | No extra setting | None | Configured in code | Run reporting contract tests; verify DST boundary cases and null/unavailable measure states |
| Data-source coverage | WORK-01 / shared reporting | `ReportingSourceCoverage`, `ReportingDataState`, `MarketingReportingSource` | Explicit complete/partial/unavailable/error/stale states, source and freshness | No extra setting | No extra setting | None | Configured in code; source completeness still unverified | Confirm each source reports unavailable/partial rather than zero until validated |
| Purchase input | WORK-01/03 / DynamoDB canonical purchases | `Purchases` table; item key `TransactionId` (String); serialized `Data` (String) | Paid purchase facts with source currency and `PaidAt`; existing `PurchaseRecord` mapping | Existing isolated app DynamoDB environment | Existing production table and execution role | Read-only table access for reporting (`Scan`); purchase writer permissions remain existing and out of WORK-04 | UNVERIFIED provider schema/coverage | Use an authorized read-only scan in a safe account; compare known test payments and coverage metadata |
| Google Ads account | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_CUSTOMER_ID` | Digits-only customer ID, no dashes | Server `local.settings.json` `Values` or process environment | Function App application settings / secret store | Read-only reporting role on selected customer | UNVERIFIED | Run the configured daily-spend query for a small historical date interval; confirm customer ID and currency |
| Google Ads manager | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID` | Optional digits-only manager customer ID, no dashes; omit when direct access is used | Same as above | Same as above | Read-only access through manager to target account | UNVERIFIED / Not required for direct access | Repeat read-only report with manager context only if account hierarchy requires it |
| Google Ads API token | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN` | Google Ads API developer token | Local secret store or ignored `local.settings.json` | Secret store referenced by Function App setting | Google Ads API access level that permits search/reporting | UNVERIFIED | Verify a read-only report succeeds; never print the token |
| Google OAuth client | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID`, `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET`, `GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN` | OAuth client ID, secret, and refresh token for an authorized reporting identity | Local secret store or ignored `local.settings.json` | Secret store / protected Function App settings | OAuth identity with read-only access to the account; reporting API scope | UNVERIFIED | Query daily spend; verify token renewal and account-level read-only access without making mutations |
| Google Ads API version | WORK-02 / Google Ads reporting | `GOOGLE_ADS_REPORTING_API_VERSION` | Version token such as `vNN`; code requires `v` followed by digits | Same as above | Same as above | None | UNVERIFIED | Confirm the configured endpoint version is accepted by a read-only query |
| Ads reporting transport | WORK-02 / Google Ads reporting | `GoogleAdsReportingHttpTransport` | OAuth refresh grant then `googleAds:searchStream`; GAQL selects date, cost micros, currency, customer ID | Runs inside Server | Runs inside Function App | Read/report only; code has no Ads mutation operation | Configured in code; provider access UNVERIFIED | Query a bounded date range and compare cost/currency with Ads UI/export |
| Legacy Ads conversion upload | Existing measurement / Google Ads conversion delivery | `GOOGLE_ADS_UPLOAD_ENABLED`, `GOOGLE_ADS_CUSTOMER_ID`, `GOOGLE_ADS_CONVERSION_ACTION_ID`, `GOOGLE_ADS_DEVELOPER_TOKEN`, `GOOGLE_ADS_LOGIN_CUSTOMER_ID`, `GOOGLE_ADS_OAUTH_CLIENT_ID`, `GOOGLE_ADS_OAUTH_CLIENT_SECRET`, `GOOGLE_ADS_OAUTH_REFRESH_TOKEN`, `GOOGLE_ADS_API_VERSION` | Legacy conversion settings; independent from WORK-02 reporting settings | Server local settings | Function App settings | Conversion upload rights only if intentionally enabled; unrelated to reporting read access | Not required for WORK-02; Ads delivery currently disconnected in purchase dispatcher | Do not use these values as proof of reporting configuration; inspect safe status only |
| Stripe-backed revenue facts | WORK-03 / canonical purchase reporting | `Purchases` table; no new Stripe reporting credential | Reuses persisted paid `PurchaseRecord` facts, source amount/currency, paid time, billing classification and transaction-owned attribution snapshot | Existing app DynamoDB endpoint/table | Existing production purchase table through server role | Read-only `Scan` on `Purchases` for reporting | Code configured; production coverage UNVERIFIED | Query read-only; confirm values against Stripe records through an authorized reconciliation process |
| Stripe source adjustments | WORK-03 / refunds, chargebacks, fees, taxes | No implemented adjustment source setting | Approved reporting contract marks these unknown/unavailable absent reliable source and association | None | None | None for current implementation | Not Required for existing path; unavailable in current code | Ensure reports do not treat absent adjustments as zero or claim complete profit |
| Existing Stripe processing | Existing payment system / Stripe | `StripeSecretKey`, `StripeWebhookSecret`; checkout may also use `StripePrices:*` | Secret API key and webhook signing secret; existing configured Price IDs | Server local secret config | Protected Function App settings / secret store | Existing checkout/webhook permissions; not needed by read-only WORK-03 projection | UNVERIFIED actual values; no new Work-03 setting | Use Stripe test-mode diagnostics for existing flows only; do not invoke checkout/webhook during reporting verification |
| Expense ledger table | WORK-04 / DynamoDB | `BusinessExpensesTableName` (optional; code default `BusinessExpenses`) | DynamoDB table name | Server `local.settings.json` `Values` or process environment | Function App settings | `Scan`, `GetItem`, `PutItem` on this table only | UNVERIFIED environment value/table existence | Run `DescribeTable` as an operator, then use isolated test entries; app itself uses only listed data operations |
| Expense table schema | WORK-04 / DynamoDB | Partition key `ExpenseId` (String); no sort key | Attributes: `ExpenseId` S, `Date` S (`yyyy-MM-dd`), `Amount` N (decimal, invariant), `Currency` S, `Category` S, `Description` S | Create only in local DynamoDB or isolated dev AWS account | Do not provision production under this task; create through approved infrastructure process | Same as expense ledger row | Schema verified from `GetItem` key and `PutItem` item in source; deployed table UNVERIFIED | Confirm `KeySchema` is HASH `ExpenseId` with AttributeType S; verify test CRUD in isolated table |
| Expense AWS region and credentials | WORK-04 / AWS SDK | `AWS_REGION`, standard AWS SDK credential provider chain | Valid AWS region; IAM role/profile credentials; no app-specific access key variables required | `AWS_REGION`; local DynamoDB uses `USE_LOCAL_DYNAMODB=true`, optional `LOCAL_DYNAMODB_URL` (default `http://localhost:8000`) and test credentials are internally supplied | Explicit `AWS_REGION`; managed execution role and standard provider chain | Table-scoped `Scan`, `GetItem`, `PutItem` only | UNVERIFIED | Startup log reports LOCAL/AWS mode and region; perform CRUD only in isolated table |
| Operator allowlist | WORK-04 / authorization | `DESIGNMODE_COMPANY_OPERATOR_EMAILS` | Comma-separated exact email addresses; case-insensitive match | Server local settings or process env | Protected Function App setting | None; this is an authorization input, not a database permission | UNVERIFIED | Sign in as listed operator and ordinary customer; direct GET/POST/PUT as customer must return 401 before DynamoDB client creation |
| JWT verification | WORK-04 / authorization | `JwtSecretKey` plus existing JWT issuer/login setup | Secret signing key consistent with issuer; valid bearer JWT containing `ClaimTypes.Name` email | Existing local auth setup; never place in client config | Existing protected server secret config | None | UNVERIFIED | Obtain a local QA JWT using existing auth; verify expired/invalid token and missing identity are denied |
| GA4 browser stream | Existing GA4 / browser measurement | `wwwroot/analytics-config.json`: `enabled`, `measurementId`, `debug`, `landingVariant` | Public GA4 measurement ID `G-…`; low-sensitivity browser flags | Checked-in current file has `enabled: true`, measurement ID `G-22R24LF0HN`, `debug: true`, variant `control` | Deployment-specific static asset configuration; set `debug` false after validation | GA4 property access to inspect DebugView; no write credential in browser | Source value verified; property ownership/production destination UNVERIFIED | Confirm deployment targets intended property, then inspect DebugView and network collection without sending real purchase data |
| GA4 server purchase event | Existing GA4 / Measurement Protocol | `GA4_SERVER_ENABLED`, `GA4_MEASUREMENT_ID`, `GA4_API_SECRET` | Exact lowercase `true`; matching `G-…` stream ID; Measurement Protocol API secret | Server local secret config | Protected Function App setting / secret store | GA4 Measurement Protocol secret scoped to intended stream | UNVERIFIED | Use GA4 debug endpoint/test stream and safe synthetic payload; confirm no IDs, email, raw queries or secrets are logged |
| GA4 authentication and event flow | Existing GA4 | `GoogleConversionDelivery`, `PurchaseConversionDispatcher`, `DesignModeAnalyticsService` | Browser config for consent-gated browser events; API secret for server-side purchase | Client static config + server settings | Client deployment config + protected server settings | Stream/property administration only for configuration | Code configured; destination delivery UNVERIFIED | Verify one route `page_view`, funnel events and one deduplicated purchase in DebugView for a test event; verify denied analytics prevents browser collection |

### GA4 implementation notes

The browser analytics wrapper allowlists `page_view`, `landing_view`, `social_form_start`, `funnel_start`, `company_profile_saved`, `designer_intro_view`, `account_gate_view`, `sign_up`, `login`, `generation_started`, `generation_completed`, `generation_failed`, `creative_directions_viewed`, `main_concept_selected`, `funnel_end_viewed`, `video_started`, `video_completed`, `unlock_clicked`, `begin_checkout`, and `paid_workspace_viewed`. It sanitizes route data, disables automatic page views/Google signals, and queues dispatch behind analytics consent. Purchase conversion is server-side GA4 Measurement Protocol from canonical paid records, carrying opaque transaction ID, actual value/currency, payment type, safe plan item and consented Google client ID; first-payment session ID is used only within the documented 24-hour checkout window. The dispatcher invokes GA4 only; Ads conversion upload is disconnected. These capabilities are reusable by later dashboard work for event/funnel observability and transaction-level conversion facts, but browser events are not a substitute for validated revenue/ad-spend reporting or property-side attribution configuration. The configured property destination and actual event ingestion remain UNVERIFIED.

## Configuration templates

Use placeholders only. Put secrets in user secrets / an ignored local settings file or protected deployment secret store. Never commit populated settings.

Server `local.settings.json` fragment for an isolated local DynamoDB instance:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AWS_REGION": "us-east-1",
    "USE_LOCAL_DYNAMODB": "true",
    "LOCAL_DYNAMODB_URL": "http://localhost:8000",
    "BusinessExpensesTableName": "BusinessExpenses",
    "DESIGNMODE_COMPANY_OPERATOR_EMAILS": "operator@example.test",
    "JwtSecretKey": "REDACTED_FOR_GROOMING"
  }
}
```

Optional source settings (only add when that integration is being verified):

```text
GOOGLE_ADS_REPORTING_CUSTOMER_ID=<digits-only-customer-id>
GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID=<optional-manager-id>
GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN=<secret>
GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID=<oauth-client-id>
GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET=<secret>
GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN=<secret>
GOOGLE_ADS_REPORTING_API_VERSION=<vNN>
GA4_SERVER_ENABLED=false
GA4_MEASUREMENT_ID=G-<test-stream-id>
GA4_API_SECRET=<test-stream-api-secret>
```

WORK-03 reporting reads canonical `Purchases`; it does not require a new Stripe credential. Existing `StripeSecretKey`, `StripeWebhookSecret`, and `StripePrices:*` belong to existing transaction paths and remain unchanged. Do not enable Ads conversion uploads to validate Ads reporting.

## DynamoDB table creation

The application does not create tables. For local DynamoDB or an explicitly isolated development AWS account, create `BusinessExpenses` (or the configured table name) with one string HASH key named `ExpenseId`, no range key, and on-demand billing. No secondary index is required by the implementation. The list operation scans and filters `Date` plus `Currency`; edits fetch by `ExpenseId`, then replace the item. The table attributes are schemaless beyond the primary key and use the types listed above. Do not create or change production resources as part of this handoff.

Example AWS CLI for an isolated non-production account only (set region/profile explicitly and verify caller identity first):

```powershell
aws sts get-caller-identity --profile <isolated-dev-profile>
aws dynamodb create-table --profile <isolated-dev-profile> --region <region> --table-name BusinessExpenses --attribute-definitions AttributeName=ExpenseId,AttributeType=S --key-schema AttributeName=ExpenseId,KeyType=HASH --billing-mode PAY_PER_REQUEST
aws dynamodb describe-table --profile <isolated-dev-profile> --region <region> --table-name BusinessExpenses
```

For AWS IAM, grant the server role only `dynamodb:Scan`, `dynamodb:GetItem`, and `dynamodb:PutItem` on the exact expense table ARN. Do not grant DeleteItem, UpdateItem, or access to `Purchases` for WORK-04. Google Ads and Stripe source reads are separate work items.

## Local manual QA handoff

This is a manual test using an isolated local DynamoDB table and synthetic values, never production financial records.

1. Start Azurite for the Functions host storage dependency (for example `azurite --silent --location <local-data-directory>`), then start DynamoDB Local at `http://localhost:8000`, create the table above, and confirm the `describe-table` key schema.
2. Add the required settings to `DesignMode.AI.Server/local.settings.json` or the server process environment. Keep `USE_LOCAL_DYNAMODB=true`; set `DESIGNMODE_COMPANY_OPERATOR_EMAILS` to the email used by a local QA account. Use the existing local JWT/auth setup; do not copy production keys. `func start` loads the `Values` entries into the Functions process environment; direct `dotnet run` does not.
3. In a terminal, change to `DesignMode.AI.Server`, build with `dotnet build`, then start the Functions host with `func start --port 7032`. Keep this terminal open and confirm the host indexes `BusinessExpensesList`, `BusinessExpensesCreate`, and `BusinessExpensesEdit`.
4. Start the client in a second terminal: `dotnet run --project DesignMode.AI.Client`.
5. Open `http://localhost:5276/admin/expenses` (the Client launch profile binds port 5276). Sign in as the allowlisted company operator if the local auth flow requires it.
6. Create this synthetic record: date `2026-10-08`, amount `12.34`, currency `USD`, category `software`, description `QA sample – mock design tool`. Expect “Expense saved.” and exactly one ledger row with amount/currency/date preserved.
7. Click Edit on that row. Change amount to `13.45` and description to `QA sample – edited`; save. Expect one row, updated values, same expense identity, and no duplicate.
8. Change the form date to `2026-10-07` and currency to `EUR`, amount `4.56`, category `AI/API`; save. Expect the subsequent ledger query to use that saved date/currency and display only the EUR entry.
9. Filter to a date/currency with no matching record. Expect the empty-state message. Reload and verify the stored date remains `yyyy-MM-dd`.
10. At 1440×900 and 390×844 (WebKit), verify labels and controls fit, save/edit are reachable, loading appears during the request, and there is no horizontal page overflow. Use [the preserved ROAS Dashboard reference](design-references/marketing-dashboard-roas-reference.png) for its dark navigation, light canvas, white cards, and responsive tables.
11. Sign in as an ordinary customer and repeat ledger GET; attempt POST/PUT directly using browser DevTools or an API client. Expect 401/403 and no records. Repeat without a token. Server authorization must deny each request before DynamoDB client creation.
12. Inspect DevTools Network for `/api/business-expenses`; verify query uses the chosen date/currency, create uses POST, edit uses PUT with the existing ID, and failures show a useful in-page message. Check the Functions console for local DynamoDB selection and errors; do not paste bearer tokens into logs.
13. Remove only these uniquely named test rows from the isolated local table using a local-only cleanup command or DynamoDB Local UI, keyed by the known `ExpenseId`. Do not run cleanup against a shared or production table.

If local auth, Azurite, or DynamoDB Local is unavailable, do not point QA at production. Use an isolated dev AWS account/table and existing non-production authentication, or stop before persistence and treat those checks as unverified.

## Remaining configuration actions

- Confirm local operator email and JWT issuer/secret are set without exposing values; create only an isolated test table before browser acceptance.
- Platform owner: confirm the Google Ads reporting customer, optional manager, OAuth identity, developer token access level and API version; then exercise the bounded read-only query.
- Platform owner: confirm which GA4 property/stream owns `G-22R24LF0HN`, change debug mode for production, and validate browser and server events in a test stream.
- Platform owner: confirm production AWS region, execution-role table policy, expense table existence/schema, and operator allowlist through approved read-only configuration review. No production configuration or resource was changed in this work.
- Finance owner: authorize read-only reconciliation of canonical Purchases and validate historical coverage/adjustments before reporting completeness is claimed. Refunds, chargebacks, fees, and taxes are not currently established by this source path.
