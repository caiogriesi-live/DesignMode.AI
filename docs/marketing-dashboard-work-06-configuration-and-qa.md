# Marketing Dashboard WORK-06 configuration and manual QA

## Readiness summary

All credential/account values remain **UNVERIFIED** unless explicitly reported by a local, safe runtime check. This repository does not establish that local or production settings are populated. Never paste secrets in this file, source control, chat, screenshots, or logs.

| Work | Current readiness | Key limits |
| --- | --- | --- |
| WORK-01 reporting contracts | Implemented in shared contracts; implementation state verified by source and tests at the WORK-05 base | USD comparison only; inclusive America/New_York days; equal-length preceding comparison. |
| WORK-02 Google Ads | Integration code exists; live account credentials and access UNVERIFIED | Current report is daily account spend only; campaign, click, impression, and conversion facts are not returned. |
| WORK-03 Stripe revenue | Purchase reporting reuses canonical `Purchases` projection; source scan works by DynamoDB, configuration/data coverage UNVERIFIED | No direct Stripe read/report call; refunds, disputes, fees, taxes, and full history are unsupported/unavailable. Existing `StripeSecretKey` is not a dashboard requirement. |
| WORK-04 expenses | Reuses operator-only `BusinessExpenses` table; local/production table data UNVERIFIED | Requires region, credentials or DynamoDB Local, and the existing operator allowlist/JWT. Advertising-category expenses are excluded from the expense subtotal. |
| WORK-05 calculations | Present at base `af283c668f50266aa8c0bd1b0ba2061e3d85552c`; focused WORK-05 tests passed in prior handoff | Unsupported contribution/profit values remain unavailable. |
| Existing GA4 | Measurement Protocol event delivery exists; settings and ingestion UNVERIFIED | No GA4 property/report API, funnel query, or property ID setting was found. The dashboard labels funnel data unavailable. |

## Exact settings and configuration locations

| Setting | Work / integration | Format and purpose | Local location | Production location | Minimum permission | Status / safe verification |
| --- | --- | --- | --- | --- | --- | --- |
| `JwtSecretKey` | WORK-04/06 authorization | Existing JWT signing secret; must match token issuer | Server process environment or private local Functions settings | Azure Function App secret setting | Sign/validate existing application JWTs | UNVERIFIED. Use an authorized login then request the report; never print value. |
| `DESIGNMODE_COMPANY_OPERATOR_EMAILS` | WORK-04/06 operator allowlist | Comma-separated exact operator email addresses | Server environment / untracked local settings | Azure Function App setting | No source access granted by itself | UNVERIFIED. Authorized operator gets report; non-operator receives HTTP 401 before source reads. |
| `GOOGLE_ADS_REPORTING_CUSTOMER_ID` | WORK-02 Google Ads | Digits only, no hyphens | Server process environment/private settings | Function App setting | Read/report access to target customer | UNVERIFIED. Call a short bounded dashboard period and verify Google Ads coverage; no writes are issued. |
| `GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID` | WORK-02 Google Ads | Digits only; optional for manager-account access | Server process environment/private settings | Function App setting | Read relationship to target customer | UNVERIFIED. Omit only for a directly accessible customer. |
| `GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN` | WORK-02 Google Ads | Secret string | Server environment/private secret store | Function App secret setting | Google Ads API reporting access | UNVERIFIED. Successful read-only spend response; never log token. |
| `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID` | WORK-02 Google Ads | OAuth client ID string | Server environment/private settings | Function App setting | OAuth application must be allowed for reporting | UNVERIFIED. Used with the refresh token below. |
| `GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET` | WORK-02 Google Ads | Secret string | Server environment/private secret store | Function App secret setting | OAuth token exchange | UNVERIFIED. Verify bounded report only. |
| `GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN` | WORK-02 Google Ads | OAuth refresh token secret | Server environment/private secret store | Function App secret setting | Google Ads account user with read/report access | UNVERIFIED. Verify bounded report only. |
| `GOOGLE_ADS_REPORTING_API_VERSION` | WORK-02 Google Ads | `vNN` | Server environment/private settings | Function App setting | None beyond supported endpoint | UNVERIFIED. Validate on a read-only query. |
| `AWS_REGION` | WORK-03/04/06 DynamoDB | AWS region such as `us-east-1` | Server environment | Function App setting | Region where source tables exist | UNVERIFIED. Server startup logs selected local/AWS mode and region without credentials. |
| `USE_LOCAL_DYNAMODB` | WORK-04/06 DynamoDB | `true` for local endpoint, otherwise unset/`false` | Server environment | Omit or `false` in production | None | UNVERIFIED. Startup selection is logged. Do not pair local mode with real AWS credentials. |
| `LOCAL_DYNAMODB_URL` | WORK-04/06 DynamoDB | Absolute local HTTP(S) URL; default `http://localhost:8000` | Server environment | Not used in AWS mode | None | Optional; verify local DynamoDB Local is reachable. |
| `BusinessExpensesTableName` | WORK-04/06 DynamoDB | Table name; defaults to `BusinessExpenses` | Server environment | Function App setting | `dynamodb:Scan` on this exact table for dashboard; WORK-04 ledger additionally needs exact-table `GetItem` and `PutItem` | UNVERIFIED. Read an isolated test table. Do not provision/modify production resources. |
| `Purchases` (literal table) | WORK-03/06 purchase reporting | Canonical records have string partition key `Id` and JSON string attribute `Data`; reporting scans full table | Isolated local DynamoDB or approved test AWS account | Existing table in app region | `dynamodb:Scan` on exact table | UNVERIFIED. Run against isolated test records; full table scan is not date-key optimized. |
| `GA4_SERVER_ENABLED` | Existing GA4 event delivery | Boolean text `true` to enable server event sending | Server process environment/private settings | Function App setting | GA4 Measurement Protocol secret for the selected property | UNVERIFIED. Safe test event/DebugView only, consent permitting. Does not enable reporting queries. |
| `GA4_MEASUREMENT_ID` | Existing GA4 event delivery | `G-...` measurement ID | Server environment/private settings | Function App setting | Measurement Protocol access to property | UNVERIFIED. Validate property identity without exposing value. |
| `GA4_API_SECRET` | Existing GA4 event delivery | Secret string | Server environment/private secret store | Function App secret setting | Measurement Protocol event delivery | UNVERIFIED. Validate with safe test event; do not log value. |
| `GA4_PROPERTY_ID` | GA4 reporting | No setting/read API found | Not applicable to current integration | Not applicable to current integration | GA4 Data API access would be needed for future reporting | **NOT REQUIRED** by event delivery; property reporting is not implemented. |
| `StripeSecretKey` | Existing checkout/payment integrations | Secret Stripe API key | Existing private local configuration | Existing Function App secret setting | Used by payment features; dashboard reader does not call Stripe | **NOT REQUIRED** for WORK-06 reporting. Do not change it. |
| `StripeWebhookSecret` | Existing webhook ingestion | Webhook signing secret | Existing private local configuration | Existing Function App secret setting | Verify existing webhook signature | Existing purchase ingestion dependency; dashboard does not call it. Value UNVERIFIED. |
| Product/Price IDs | Stripe billing | Existing billing configuration | Existing private configuration | Existing production configuration | None for dashboard reads | **NOT REQUIRED** for report aggregation. |
| Reporting currency | WORK-01/05/06 | USD is fixed for comparable metrics; original non-USD amounts are retained separately | No setting | No setting | None | **CONFIGURED IN CODE**. No conversion rate or currency selector is implied. |
| Reporting date range | WORK-01/05/06 | Presets and custom date bounds; max 366 inclusive days; date boundaries resolved in `America/New_York` | Client selection/API query | Same | None | **CONFIGURED IN CODE**. Read response `Periods` and source coverage for bounds. |
| Recommendation thresholds | WORK-05/06 | Existing calculator requires explicit target ROAS/minimum ROAS/optional target CAC/minimum customer count | No dashboard setting is configured | No Function App setting is configured | None; recommendation is advisory only | **NOT REQUIRED** while campaign facts are unavailable. UI does not create campaign actions. |
| Freshness threshold | WORK-01/02/06 | Google source `FreshThrough` may be absent; no maximum-age threshold setting found | No setting | No setting | None | **UNVERIFIED / no threshold**. UI labels coverage and generated time; absence of source freshness is not called current. |

### Local template (placeholders only)

Set these only in the Functions server process environment or private local settings. Do not put the values in the client or commit a populated `local.settings.json`.

```powershell
$env:JwtSecretKey = 'REDACTED_FOR_GROOMING'
$env:DESIGNMODE_COMPANY_OPERATOR_EMAILS = '<operator@example.test>'
$env:AWS_REGION = 'us-east-1'
$env:USE_LOCAL_DYNAMODB = 'true'
$env:LOCAL_DYNAMODB_URL = 'http://localhost:8000'
$env:BusinessExpensesTableName = 'BusinessExpenses'
$env:GOOGLE_ADS_REPORTING_CUSTOMER_ID = '<customer-id-digits>'
$env:GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID = '<manager-id-digits-if-needed>'
$env:GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN = '<secret>'
$env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID = '<oauth-client-id>'
$env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET = '<secret>'
$env:GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN = '<secret>'
$env:GOOGLE_ADS_REPORTING_API_VERSION = '<supported-vNN>'
$env:GA4_SERVER_ENABLED = 'false'
$env:GA4_MEASUREMENT_ID = '<G-...>'
$env:GA4_API_SECRET = '<secret>'
```

For a real GA4 event-delivery test, set `GA4_SERVER_ENABLED=true` only after confirming the property and using consent-compliant test events. This still will not make funnel reports available in WORK-06.

Production values belong in the Azure Function App's application settings/secret store; use the production AWS credential provider/role and explicit `AWS_REGION`, `USE_LOCAL_DYNAMODB=false`, the exact existing table names, and an operator allowlist. Never put AWS keys in Blazor. Minimum dashboard IAM is read-only `dynamodb:Scan` for `Purchases` and the configured business-expense table. WORK-04 expense create/edit additionally uses `GetItem`/`PutItem`; those permissions need not be granted to another role solely for viewing the dashboard. Do not create or change production tables in this work item.

## DynamoDB table prerequisites

The expense table defaults to `BusinessExpenses`; if a different existing table name is used, set `BusinessExpensesTableName` on the server. Required exact key schema: partition key `ExpenseId` with DynamoDB type `S`; no sort key. WORK-04 attributes are `ExpenseId` (String), `Date` (`yyyy-MM-dd`, String), `Amount` (Number), `Currency` (String), `Category` (String), and `Description` (String). The dashboard reads and filters a date range using a table scan; no GSI is assumed or required. `Purchases` is an existing source table with key `Id` (`S`), JSON string `Data`, and is scanned by the existing WORK-05 reader.

To create an **isolated local DynamoDB Local** expense table (not production), start DynamoDB Local first and run:

```powershell
aws dynamodb create-table --table-name BusinessExpenses --attribute-definitions AttributeName=ExpenseId,AttributeType=S --key-schema AttributeName=ExpenseId,KeyType=HASH --billing-mode PAY_PER_REQUEST --region us-east-1 --endpoint-url http://localhost:8000
```

The creation command has no effect on production because it explicitly targets the local endpoint. Before executing it, ensure `http://localhost:8000` is your local DynamoDB Local instance. If the table already exists, reuse the isolated table instead of deleting it. Never run a create/delete command against an AWS endpoint as part of this task.

## Local startup and manual browser acceptance

Use the WORK-06 worktree. Start DynamoDB Local with an isolated, empty/test-only database and populate only safe synthetic expense and purchase records. In PowerShell terminal 1 (server):

```powershell
dotnet run --project DesignMode.AI.Server -- --port 7032
```

In PowerShell terminal 2 (client):

```powershell
dotnet run --project DesignMode.AI.Client --launch-profile http
```

Open `http://localhost:5276/init/marketing-dashboard`. The browser's localhost API routing points to `http://localhost:7032/`. Sign in using an account whose email is in `DESIGNMODE_COMPANY_OPERATOR_EMAILS`; the existing token issuer must use the matching `JwtSecretKey`. A customer login should be used separately for the denial check.

Safe synthetic records can include test purchase records in `Purchases` with `PaymentStatus=Paid`, positive USD `Amount`/`AmountMinor`, test transaction IDs and the existing supported `PurchaseType`/`BillingReason` fields, plus a synthetic `Snapshot` with a fake click/UTM marker. Do not use real customer data or perform a live payment. Expense examples can be a USD `software` row and an `advertising/marketing` row dated inside the selected period; the latter must be excluded from the general expense subtotal so Ads spend is not counted twice.

1. **Operator access:** signed in as an allowlisted operator, load the URL; the report endpoint should return 200. Sign out and retry, then sign in as a non-allowlisted customer; the page should show an access-restricted message and the API should return 401 without reading DynamoDB/Google Ads.
2. **Date ranges:** choose Today, Last 7 days, Last 30 days, Month to date, Previous month, Year to date, then custom start/end. Press Refresh. Verify dates in the request and the displayed comparison period; a reversed range or more than 366 inclusive days is rejected.
3. **KPI data:** with approved isolated test fixtures, compare USD spend/revenue/ROAS/CAC/new customers against WORK-05 outputs. Other currency purchases must not be converted or included in USD ratios. Unsupported values (contribution, payback, profit) should read Unavailable with an explanation when required cost/deduction evidence is missing.
4. **Expenses:** use a local table record in the selected interval; inspect `BusinessExpenses` coverage and the business-expense subtotal. An `advertising/marketing` expense is excluded to avoid double counting. With missing/unreachable local table, the API should show a source error and profit must remain unavailable.
5. **Charts:** verify only observed dates are plotted, no synthetic zeros fill missing days, and the daily ROAS line derives from the WORK-05 calculator. Google-attributed revenue is based on transaction snapshot evidence only.
6. **Campaigns and recommendations:** the current Google Ads reader exposes account-level spend only. The campaign panel must show unavailable and no SCALE/HOLD/REDUCE/PAUSE result. Confirm no Google Ads write request occurs.
7. **GA4:** the funnel panel must say property reporting is unavailable. Existing `purchase` Measurement Protocol event delivery is separate; no fake funnel values should appear.
8. **Responsive layout:** inspect at 1440×900 and 390×844. Confirm cards/charts stack cleanly, table region can be scrolled within its panel if needed, page has no horizontal overflow, and bottom nav does not cover controls.
9. **Failure handling:** stop the local API or use an unauthorized token. Confirm clear error/401 feedback. In browser devtools Network, inspect `GET /api/marketing-dashboard`, status, query dates, and sanitized response; never copy Authorization headers, tokens, or financial records into issue reports.
10. **Cleanup:** remove only synthetic rows from the isolated local tables using an exact `ExpenseId`/test transaction key. Do not clear shared or production tables.

Live Google Ads calls require a configured authorized reporting identity. The server purchase source is a read-only scan of `Purchases`, not a direct Stripe API report. Browser tests using mocks are not evidence of live source integration. Current Google Ads campaign-level data and GA4 property reporting are known unavailable states, not QA failures; production credential and data completeness remain UNVERIFIED.

## WORK-06 verification evidence

- Client, Server, and Test project builds completed with .NET SDK 8.0.425; all succeeded with existing repository warnings.
- Focused WORK-06, WORK-05 reporting/calculation, and WORK-04 authorization tests: 28 passed, 0 failed.
- Playwright mocked browser acceptance: Chromium desktop (1440×900) and WebKit mobile (390×844), 2 passed, 0 failed. Screenshots are local QA artifacts at `%LOCALAPPDATA%\Temp\marketing-dashboard-work-06\chromium-1440x900.png` and `webkit-390x844.png`; they are not committed.
- The screenshots show the structure, card hierarchy, charts, and spacing from the reference, but the existing `/init` shell has a narrow light navigation rail instead of the reference's dark wide sidebar. Lower campaign and marginal-analysis panels continue below the 900px viewport. KPI values in these screenshots are mocked fixtures, not live financial data.
- Full regression: 1,044 passed, 6 failed, 21 skipped, 1,071 total. The six failures exactly match the previously recorded baseline identities: four `Sequence_UsesResponsiveSharedLayoutAndSafeResume` cases, `LargeGenModal_OptsIntoWorkspaceLayout`, and `WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable`. No new regression failure remained after updating the navigation expectation for the added route.
- `git diff --check` completed without whitespace errors. Live Google Ads, purchase-table and DynamoDB data, production permissions, and GA4 funnel reporting were not verified in this environment.
