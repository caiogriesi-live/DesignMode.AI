# Marketing Dashboard WORK-07 Configuration and Manual QA

WORK-07 adds read-only campaign reporting, exact transaction GCLID-to-campaign matching, and advisory budget percentages. It does not change Google Ads, Stripe, checkout, subscriptions, credits, or production infrastructure.

## Configuration readiness

| Work | Integration and exact setting | Required value and location | Required access | Status and safe verification |
|---|---|---|---|---|
| WORK-01 | Shared reporting contracts and calendar | America/New_York calendar bounds; contracts in DesignMode.AI.Shared/Models/MarketingReporting. | None beyond source reads. | Configured in code. Verify DST boundaries in focused tests. |
| WORK-02 | GOOGLE_ADS_REPORTING_CUSTOMER_ID | Account ID digits only; server environment or private Functions settings. | Google Ads API reporting access to this account. | UNVERIFIED. Run a bounded read-only campaign query. |
| WORK-02 | GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID | Optional manager account ID digits only, if an MCC is needed. | Reporting access through the manager. | NOT REQUIRED for a directly accessible account; otherwise UNVERIFIED. |
| WORK-02/07 | GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN | Required developer token; store server-side. Reuse WORK-02's existing token. | Required Google Ads API request credential. | UNVERIFIED. Confirm the existing token is configured; do not create a duplicate. |
| WORK-02 | GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID | OAuth client ID, server environment. | OAuth authorization for Ads reporting. | UNVERIFIED; exercise read-only query. |
| WORK-02 | GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET | OAuth secret, server secret store. | OAuth token refresh. | UNVERIFIED; never log or expose in client. |
| WORK-02 | GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN | Reporting-authorized refresh token, server secret store. | Read-only Ads reporting access. | UNVERIFIED; test token refresh and bounded report. |
| WORK-02 | GOOGLE_ADS_REPORTING_API_VERSION | Supported vNN, server environment. | None. | UNVERIFIED; verify supported version with a read-only query. |
| WORK-03 | StripeSecretKey, StripeWebhookSecret | Existing private settings. Dashboard uses canonical purchase records, not Stripe API calls. | Existing billing and webhook permissions; no new access for WORK-07. | NOT REQUIRED by this read-only dashboard. Do not change payment behavior. |
| WORK-03 | Stripe account/product/price/webhook identifiers | Existing configuration. | Existing payment processing access. | Existing canonical purchase provenance is a dependency; live source completeness UNVERIFIED. No Stripe query or write is added. |
| WORK-03/07 | Purchases DynamoDB table | Existing table stores Id (S) and JSON string Data. | Read-only dynamodb:Scan on that exact table. | UNVERIFIED live. Verify using isolated synthetic canonical records. |
| WORK-07 | AttributionTouches DynamoDB table | Existing touch rows keyed by Id (S), with FunnelId, original CreatedAt, and click-ID attributes. | Read-only dynamodb:GetItem for exact touch keys. | UNVERIFIED live. The existing provenance verifier requires the touch's funnel and click identifiers to match the transaction snapshot before returning the original click timestamp. |
| WORK-04 | BusinessExpensesTableName | Existing table name; default BusinessExpenses. | WORK-04 ledger uses exact-table GetItem/PutItem; dashboard read uses Scan. | WORK-07 does not change expense access. UNVERIFIED live. |
| WORK-04/06 | DESIGNMODE_COMPANY_OPERATOR_EMAILS | Comma-separated company operator emails, server only. | None. | UNVERIFIED. Test allowlisted operator and ordinary customer; denied request must not read reporting sources. |
| WORK-04/06 | JWT signing/authentication configuration | Existing server-side JWT settings, including JwtSecretKey where used. | None. | UNVERIFIED. Use app login and inspect status only; never share tokens. |
| WORK-04/06 | AWS_REGION, AWS credential provider | Test region plus local SDK profile/role; DynamoDB Local uses USE_LOCAL_DYNAMODB=true and LOCAL_DYNAMODB_URL. Production should use existing role/credential provider. | Read exact test table(s); do not grant write access for the dashboard. | UNVERIFIED. Confirm local endpoint or production role identity safely. |
| WORK-07 | MARKETING_RECOMMENDATIONS_TARGET_ROAS | Positive decimal multiple, server environment. | None; advisory only. | MISSING until business policy is approved. No default. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_ROAS | Decimal >= 0 and below target ROAS. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_TARGET_CAC | Optional positive decimal in report currency; blank disables CAC threshold. | None. | NOT REQUIRED; if configured, approve currency and value. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_NEW_CUSTOMERS | Positive integer. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_CLICKS | Positive integer. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_SPEND | Positive decimal in report currency. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_ATTRIBUTION_COVERAGE_PERCENT | Decimal 0–100; measured among canonical transactions with GCLID evidence in the selected click-date window. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_SCALE_CHANGE_PERCENT | Positive decimal percent, no higher than the configured maximum. | None; no Ads mutation access. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_REDUCE_CHANGE_PERCENT | Positive decimal percent applied as a negative advisory adjustment. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_PAUSE_CHANGE_PERCENT | Positive decimal percent applied as a negative advisory adjustment. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MAX_SCALE_CHANGE_PERCENT | Positive decimal percent. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MAX_REDUCTION_PERCENT | Positive decimal percent bounding reduce/pause changes. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_MINIMUM_REPORTING_DAYS | Positive integer of distinct campaign report dates. | None. | MISSING until approved. |
| WORK-07 | MARKETING_RECOMMENDATIONS_CONVERSION_LAG_DAYS | Nonnegative integer; selected period must be older than this lag. | None. | MISSING until approved. |
| Existing GA4 | GA4_SERVER_ENABLED | Boolean text controlling server event sending. | Measurement Protocol secret for the selected property. | Existing event delivery only; verify with consent-compliant test event/DebugView. Does not enable reports. |
| Existing GA4 | GA4_MEASUREMENT_ID | G-... ID, server only. | Measurement Protocol property access. | UNVERIFIED; verify property identity without exposing value. |
| Existing GA4 | GA4_API_SECRET | Secret, server-side secret store. | Measurement Protocol. | UNVERIFIED; never print or commit. Existing server delivery emits a consent-gated purchase event with transaction ID/value/currency/items; client funnel events include account_gate_view, sign_up, login, unlock_clicked, begin_checkout, and creative-generation events. |
| Existing GA4 | GA4_PROPERTY_ID / Data API credentials | No property-reporting implementation/setting found in the inspected path. | Future GA4 Data API reporting access would be needed. | NOT REQUIRED for WORK-07; funnel data remains unavailable. |
| WORK-07 | Currency and time zone | Google Ads customer currency_code and time_zone returned at runtime. No conversion is implemented. | Read-only report. | Ratios are only calculated for supported USD comparison and aligned calendar time zone. Shared/non-daily budgets do not get a suggested amount. |

## Local placeholder template

Set these in the server process or ignored private Functions settings. Values are placeholders, not credentials. Never commit a populated local.settings.json.

    $env:GOOGLE_ADS_REPORTING_CUSTOMER_ID = '<customer-id-digits>'
    $env:GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID = '<manager-id-if-needed>'
    $env:GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN = '<developer-token>'
    $env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID = '<oauth-client-id>'
    $env:GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET = '<secret>'
    $env:GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN = '<secret>'
    $env:GOOGLE_ADS_REPORTING_API_VERSION = '<supported-vNN>'
    $env:AWS_REGION = '<isolated-test-region>'
    $env:USE_LOCAL_DYNAMODB = 'true'
    $env:LOCAL_DYNAMODB_URL = 'http://localhost:8000'
    $env:BusinessExpensesTableName = 'BusinessExpenses'
    $env:DESIGNMODE_COMPANY_OPERATOR_EMAILS = '<operator@example.test>'

Recommendation settings use the exact names listed above. Configure all required values only after business approval. Place production values in the Azure Function App setting/secret store. Use the existing AWS role/provider and least-privilege read grants. No production resources should be provisioned or changed for WORK-07.

## DynamoDB prerequisites

WORK-07 reads the existing Purchases table and uses the existing provenance verifier to GetItem exact AttributionTouches rows. The established expense schema is unchanged: BusinessExpenses defaults to a table with partition key ExpenseId (S), no sort key; attributes are ExpenseId (String), Date (yyyy-MM-dd String), Amount (Number), Currency, Category, and Description (String). Purchases uses Id (S) and JSON string Data. Confirm test schema with:

    aws dynamodb describe-table --table-name <test-table> --region <test-region> --endpoint-url http://localhost:8000

This query must target DynamoDB Local. WORK-07 does not create or change tables. Dashboard role needs exact-table Scan for Purchases/business expenses and GetItem for AttributionTouches. No Google Ads mutation permission is required.

## Manual browser QA

Use an isolated local/test environment and synthetic canonical purchases. A fabricated GCLID is expected to remain unmatched; test a verified mapping only with a test click visible to the authorized Ads account. Never use production financial records.

Start the Functions server and Blazor client in separate terminals from this worktree:

    dotnet run --project DesignMode.AI.Server -- --port 7032

    dotnet run --project DesignMode.AI.Client --launch-profile http

Open **http://localhost:5276/init/marketing-dashboard** and sign in as an allowlisted company operator.

1. **Authorization:** dashboard report should load only for an allowlisted operator. Sign out and test an ordinary customer. Expect an access-denied response and no report data. Never copy an auth token.
2. **Campaign facts:** choose a bounded date interval. Verify campaign name/ID, daily spend, clicks, impressions, platform conversions/value, account currency, and time zone. Platform conversions must remain distinct from verified purchases.
3. **Purchase attribution:** exact-matched purchase requires the transaction snapshot GCLID, matching click date, and Google Ads click_view campaign. URL CampaignId, UTM alone, unmatched GCLID, GBRAID/WBRAID, stale (>90-day) clicks, and conflicting transactions are not verified campaign purchases.
4. **Calendar/currency:** compare report dates with the Google Ads account timezone. No currency conversion occurs. Unsupported currency/time-zone comparisons must not produce ROAS/CAC or recommendations.
5. **Recommendations:** with incomplete policy configuration, expect ConfigurationRequired and no suggested percent. With a fully approved isolated test policy and sufficient evidence, check rationale, action, percentage, and daily-budget estimate. Shared/non-daily budgets do not produce an estimate. Verify no Google Ads changes are sent.
6. **Failures:** test missing settings, revoked read access, and network failure. Expect useful coverage/error state without OAuth secrets or raw provider error contents.
7. **Responsive layout:** inspect Chromium at 1440x900 and WebKit mobile at 390x844. The campaign table may scroll inside its container; the document should not overflow horizontally.
8. **Network check:** inspect request URL/status/response shape in devtools. Confirm there are no campaign mutation requests. Mocked browser evidence is not evidence of live Google Ads or DynamoDB integration.
9. **Cleanup:** remove only known synthetic rows from an isolated test table. Never clear shared/production tables.

## Verification status

- Google Ads live read: UNVERIFIED until an authorized test identity/account is used.
- Canonical Purchases live source and exact click match: UNVERIFIED until isolated test data is used.
- Recommendation thresholds: MISSING until business-approved values are configured.
- GA4 property reporting: NOT IMPLEMENTED; server-side event delivery is separate.
- Production resources/financial systems: not modified by this work item.

The dashboard continues to show the existing shared header and /init navigation. The approved reference uses a wide dark sidebar, top section tabs, denser KPI/chart rows, and campaign plus marginal analysis visible in one desktop frame. WORK-07 adds the campaign table and recommendations within the existing dashboard shell; it does not replace the global navigation or implement the full reference-page redesign.

Google Ads GAQL field names used for campaign metrics and budget fields were checked against the [official v22 campaign budget field reference](https://developers.google.com/google-ads/api/fields/v22/campaign_budget). This code path remains read-only.
