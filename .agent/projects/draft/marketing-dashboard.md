# Marketing Performance Dashboard

## Objective

Deliver a company-wide, read-only financial reporting dashboard for authorized DesignMode administrators/company operators, combining Google Ads reporting, existing canonical purchase records, and a new manual business-expense ledger. Present marketing profitability separately from overall business profitability, with explicit source completeness and attribution limits.

## Current State

- `DesignMode.AI.Client/Pages/Dashboard.razor` implements `/init/dashboard` as a user-scoped project list with create/open actions. `RootDashboard.razor` routes authenticated users from `/dashboard` to it. This project dashboard exists and must be preserved; it does not currently present financial metrics.
- `DesignMode.AI.Server/DashboardProjectsFunction.cs` and `Services/DashboardPromptQuery.cs` provide an authenticated, user-scoped projects API backed by DynamoDB prompt data. Existing authentication and customer authorization do not establish company-wide financial access.
- Verified Stripe purchase measurement already writes canonical positive paid records to DynamoDB `Purchases`. Records include amount, currency, paid time, billing reason, initial/renewal indicators, user/project/funnel data, and a checkout attribution snapshot. Verified paid subscription payments and paid upgrades can be identified; records are connected to protected Stripe webhook/payment flows.
- AI-credit purchases already use the canonical purchase infrastructure, with `PurchaseType = "AICredits"`, `BillingReason = "ai_credit_topup"`, and an `IsTestCreditPurchase` flag. Refund/dispute handling can update credit reversals, but does not create a general financial adjustment record with refund/chargeback amounts for reporting.
- Attribution, UTM/query-string and Google click-ID capture, consent context, and checkout attribution snapshots already exist in `AttributionStore`, `AttributionFunctions`, purchase models, and related code. Purchase snapshots omit some touch fields, including match type, device, and network. Later AI-credit purchases do not carry acquisition snapshots. Do not rebuild capture or join touch records solely to add deferred segmentation.
- `GoogleConversionDelivery.cs` contains GA4 measurement and legacy Google Ads click-conversion code; `PurchaseConversionDispatcher` dispatches GA4 only. Ads delivery/provenance sidecars provide eligibility and dry-run evidence, not campaign reporting, spend data, or reporting authorization.
- No Google Ads reporting client/spend path, manual expense ledger, financial aggregation service/API, or financial dashboard UI was found. Existing tests cover attribution, purchase measurement, credit handling, and project dashboard behavior, but not the new reporting functionality.
- Google Ads OAuth-related setting names exist for legacy conversion upload. They do not prove reporting access, account identity, or reporting authorization. No credential values were inspected. Production Stripe/source completeness has not been validated through an authorized read-only process.

## Desired Outcome

Authorized company operators can view date-filterable financial reporting in USD using `America/New_York` calendar-day boundaries, with source freshness, coverage, and incomplete/partial states. Subscription Revenue includes legitimate collected initial subscription payments, renewals, and paid plan upgrades supported by canonical payment records. AI Credit Revenue includes legitimate paid AI-credit purchases, remains separately visible, and excludes test purchases. Total Collected Revenue includes Subscription Revenue plus legitimate AI Credit Revenue, subject to approved refund/chargeback treatment. Taxes are excluded from revenue and are not a marketing expense.

Marketing Profit is Google-attributed revenue minus refunds and chargebacks attributable to that revenue, Stripe fees attributable to that revenue, and Google Ads spend. Marketing Profit Margin is Marketing Profit divided by Google-attributed revenue, multiplied by 100. Overall Business Profit is Total Collected Revenue minus refunds and chargebacks, Stripe fees, Google Ads spend, and all approved business expenses. If refunds, chargebacks, or Stripe fees cannot reliably be associated with Google-attributed revenue, marketing profitability is incomplete or partially available; no allocation rule is invented.

Revenue is attributed to Google only where approved transaction-level attribution evidence supports it. A customer's original Google acquisition alone does not attribute later AI-credit purchases or plan upgrades. Unsupported transactions remain unattributed. Non-USD records remain separately identified and are not converted or included in comparable USD totals or ratios. Company-wide spend, revenue, expenses, profit, CAC, ROAS, and related financial data are available only to server-authorized DesignMode administrators/company operators.

## Scope

- Add a company-authorized, read-only financial reporting surface while preserving the existing project dashboard and its user-scoped behavior.
- Define only the missing reporting DTOs/contracts, period and comparison semantics, source-field mapping, and data-quality/completeness states; reuse existing attribution, purchase, and checkout snapshot data.
- Implement a distinct new read-only Google Ads reporting integration for the company account, with reporting authorization and account identity configured independently from conversion-upload/provenance infrastructure.
- Add authorized read-only reporting/classification over canonical positive paid purchase records and supported financial source data. Include qualifying subscription initial payments, renewals, paid upgrades, and legitimate paid AI-credit purchases; exclude test AI-credit purchases and keep credit revenue separate. Validate source coverage before claiming financial completeness.
- Add a new manual business-expense ledger using date, amount, currency, category, and description/vendor, with the established categories: software, AI/API, hosting/infrastructure, contractors, payroll, advertising/marketing, professional services, and other. Restrict access to authorized company operators.
- Aggregate source data according to the approved formulas, attribution rules, currency treatment, time periods, comparisons, and completeness states.
- Present financial metrics and source/data-quality states in the financial reporting UI without replacing the existing project list.
- Add focused tests for new reporting calculations and boundaries, avoiding duplicate coverage of existing attribution and payment workflows.

## Out of Scope

- Rebuilding attribution, UTM/query-string capture, Google click-ID capture, checkout attribution snapshots, canonical Stripe purchase measurement, AI-credit purchase infrastructure, or the existing social-content funnel.
- MatchType, Device, and Network segmentation in this version; no bounded `AttributionTouches` join solely to obtain those fields. These may be considered later.
- Changes to Stripe checkout, webhooks, subscription lifecycle, payment processing, purchase writes, credit grants/reversals, refund processing, or production financial transactions.
- Changes to authentication/customer authorization mechanisms, attribution capture/consent policy, GA4 measurement, Ads conversion/provenance behavior, AI usage accounting, or production infrastructure/deployment configuration.
- Google Ads campaign management, write access, or conversion uploads.
- Multi-touch attribution, inherited attribution of later purchases, invented attribution windows, or assigning unsupported/unattributed revenue to Google.
- Automated bookkeeping, tax/accounting statements, forecasting, budget optimization, external accounting integration, and currency conversion absent an approved exchange-rate policy.
- Historical data mutation or backfill. Read-only validation and reporting over available approved source records only.

## Architecture and Existing Systems

- Use the existing Blazor WebAssembly client, Azure Functions isolated-worker API, shared contracts, and DynamoDB patterns. Keep the current project dashboard/query intact and provide a distinct company-financial reporting experience/API.
- Enforce an explicit DesignMode administrator/company-operator authorization boundary server-side for every company-wide financial endpoint and expense operation. UI hiding is insufficient. Reuse existing authentication without weakening or replacing authentication or customer authorization mechanisms. Ordinary DesignMode customers must not receive company financial data.
- Define reporting contracts around existing source fields and required period/comparison semantics, source mapping, and quality/completeness indicators. Do not redesign acquisition or payment contracts without a demonstrated requirement.
- Use canonical `Purchases` records as positive paid transaction inputs, not as a complete Stripe balance/adjustment ledger. Read only supported source facts for refunds, chargebacks, taxes, and fees. Missing coverage or uncertain association must be explicit and must not be treated as zero. Validate production source completeness through an authorized read-only process before claiming complete historical financial reporting.
- Implement Google Ads reporting as a separate read-only integration. Account/customer identity, reporting authorization, and read-only access are external prerequisites. Existing conversion-upload configuration and provenance sidecars are not evidence of reporting authorization and must not be changed or repurposed to alter conversion behavior.
- Implement a new, separately scoped in-application manual expense ledger; no existing expense system was found.
- Keep aggregation outside payment transaction paths. Never expose credentials or unrelated company/customer data. Use USD for comparable metrics and `America/New_York` calendar-day boundaries; surface non-USD data separately.

## Milestones

### Milestone 1: Define reporting contracts and establish sources

Complete the missing reporting DTOs, period/comparison semantics, source mapping, and completeness states; establish company authorization; implement read-only Google Ads and purchase/source reporting paths and the new expense ledger.

### Milestone 2: Aggregate and present financial reporting

Calculate approved measures for supported periods and comparisons, and deliver the company-authorized financial UI with source freshness, currency, attribution, and completeness states while preserving the existing project dashboard.

### Milestone 3: Verify reporting correctness and access boundaries

Cover new reporting calculations, source gaps, company authorization, and UI states. Validate connected sources only through authorized read-only procedures, without transactions or conversion uploads.

## Engineering Work Items

### WORK-01: Define missing reporting contracts and source semantics

Objective:
Define the missing reporting DTOs/contracts, supported period and comparison semantics, source-field mappings, and data-quality/completeness states needed by the approved metrics. Reuse existing attribution and purchase contracts and document source gaps, including unsupported adjustment associations and non-USD treatment. Do not recreate capture or payment-measurement contracts or add business rules beyond those approved here.

Depends On:
NONE

Protected Systems:
Existing attribution, checkout snapshots, canonical purchase measurement, authentication, and financial source data. Contract work must not change source writes or access controls.

### WORK-02: Add distinct read-only Google Ads reporting integration

Objective:
Implement reporting-only access to company Google Ads account/customer data, including date-granular spend and reporting fields supported by the approved contract, with freshness/error/partial states. Keep account identity and reporting authorization separate from existing conversion-upload and provenance infrastructure.

Depends On:
WORK-01; connected-source validation additionally depends on external Google Ads account/customer identity, reporting authorization, and read-only access.

Protected Systems:
Google credentials and account permissions, existing conversion delivery/provenance, GA4 measurement. No conversion uploads, write access, or changes to consent or measurement behavior.

### WORK-03: Add authorized read-only purchase reporting and classification

Objective:
Provide company-authorized read-only reporting over canonical paid purchase records and supported financial source data. Classify legitimate initial subscription payments, renewals, and paid upgrades as Subscription Revenue; show legitimate paid AI-credit purchases separately and exclude test purchases. Attribute only where approved transaction-level evidence supports it; later credits and upgrades without supporting evidence remain unattributed. Represent unsupported or unvalidated refund, chargeback, tax, fee, and historical source coverage as incomplete/unavailable rather than changing payment flows or inferring values.

Depends On:
WORK-01

Protected Systems:
Stripe, canonical `Purchases`, production financial records, attribution data, and existing authorization. Read-only source access; no checkout, webhook, payment, purchase-write, credit grant/reversal, or ledger changes.

### WORK-04: Implement manual business-expense ledger

Objective:
Create the new company-operator-authorized expense entry, edit, and read capability using the approved fields and categories, persisted separately from canonical purchase records and available to reporting by date and currency.

Depends On:
WORK-01

Protected Systems:
Company authorization and financial records. Do not mutate production transaction records or change customer authorization.

### WORK-05: Calculate approved financial reporting measures

Objective:
Aggregate Google Ads, purchase/source reporting, and expenses for selected and comparison periods. Calculate Subscription Revenue, separate AI Credit Revenue, Total Collected Revenue, Google-attributed revenue, CAC, ROAS, Marketing Profit, Marketing Profit Margin, and Overall Business Profit according to the approved definitions. Exclude test credit purchases and taxes from revenue. Do not attribute later credit purchases or upgrades from customer origin alone. Mark marketing profitability incomplete/partially available when reliable deduction attribution is unavailable; do not invent an allocation. Keep non-USD amounts outside comparable USD totals and ratios.

Depends On:
WORK-02, WORK-03, WORK-04

Protected Systems:
Production financial and attribution data. Read-only aggregation over source records; no writes to protected sources.

### WORK-06: Add the company financial reporting UI

Objective:
Add a company-operator-authorized financial reporting surface with approved date selections (Today, last 7 days, last 30 days, month-to-date, previous month, year-to-date, custom range), meaningful preceding-equivalent comparisons, metric summaries, and freshness, completeness, attribution, error, and non-comparable currency states. Preserve the existing project dashboard and its project-list behavior.

Depends On:
WORK-05

Protected Systems:
Server-enforced company authorization and financial data. Preserve existing dashboard, authentication, and customer authorization behavior.

### WORK-07: Verify new reporting behavior and boundaries

Objective:
Add focused coverage for new formulas, period/currency boundaries, source completeness, supported transaction classifications, exclusion of test credit purchases, transaction-level attribution limits, incomplete refund/chargeback/fee association, expense authorization, company access enforcement, and rendered financial reporting states. Reuse existing attribution, purchase, credit, and project dashboard coverage rather than duplicating those workflows. Document safe read-only connected-source validation.

Depends On:
WORK-02, WORK-03, WORK-04, WORK-05, WORK-06

Protected Systems:
Production Stripe and Google Ads data, authentication, secrets, and production infrastructure. Validation must not trigger transactions, source writes, or conversion uploads.

## Dependencies

WORK-01 defines reporting-only contracts and mappings before source paths. WORK-02, WORK-03, and WORK-04 can proceed independently after WORK-01, with company authorization required for financial reads and expense operations. Google connected-source validation also requires separately established account/customer identity and read-only reporting authorization. WORK-05 depends on all three source/ledger work items; WORK-06 depends on aggregation; WORK-07 follows the new implementation. Production Stripe/source completeness must be validated through an authorized read-only process before historical completeness is claimed. The existing project dashboard and social-content funnel are reused as-is.

## Risks

- Canonical purchase records contain positive paid transactions but do not alone establish refund/chargeback net revenue, Stripe fees, tax exclusion, or complete historical coverage. Unavailable inputs must not be silently treated as zero. Historical financial completeness depends on authorized read-only source validation.
- Refunds, chargebacks, or Stripe fees may lack reliable association with Google-attributed revenue. Marketing Profit and/or margin must then be incomplete or partially available, without an invented allocation.
- Attribution snapshots support some transaction-level evidence but do not justify assigning later AI-credit purchases or upgrades from original customer source. Unsupported transactions remain unattributed.
- Google Ads reporting requires an independently authorized account/customer connection with read-only reporting permission. Conversion-upload configuration is not sufficient evidence.
- Company-wide financial access must be enforced server-side without weakening existing authentication or customer authorization. Any boundary gap risks exposing company financial information to ordinary customers.
- Non-USD records cannot be compared with USD absent an approved conversion policy; keep them separate.
- Expense accuracy depends on authorized company operators entering timely, correct records; communicate that expenses are manually recorded.

## Protected Systems

- Preserve existing authentication and customer authorization. Add only the explicit server-side DesignMode administrator/company-operator boundary for company-wide financial reporting and expense operations. UI hiding alone does not authorize access.
- Do not modify Stripe checkout, subscriptions, webhooks, transaction processing, verified purchase qualification, canonical purchase writes, refund processing, credit grants/reversals, or production financial transactions.
- Keep production purchase and attribution records read-only; do not backfill or mutate historical source data.
- Preserve attribution and checkout snapshot capture, consent, GA4 measurement, existing Ads conversion/provenance behavior, and the social-content funnel.
- Protect Google credentials and account permissions; do not expose secrets or use conversion-upload configuration as reporting authorization.
- Keep AI usage accounting, `AIGenerationUsage`, secrets, deployment configuration, and production infrastructure out of scope.

## Definition of Done

- The existing project dashboard and social-content funnel remain intact, with a separate company-authorized financial reporting experience.
- Every financial API and expense operation enforces the company administrator/operator boundary server-side. Ordinary customers cannot access company Google Ads spend, Stripe revenue, expenses, profit, CAC, ROAS, or related reporting.
- Reporting reuses existing attribution, UTM/query-string and Google click-ID capture, checkout snapshots, canonical purchase measurement, and AI-credit purchase records; it does not rebuild these systems.
- Subscription Revenue includes legitimate collected initial subscription payments, renewals, and paid plan upgrades according to canonical payment records. AI Credit Revenue is separately visible and excludes test purchases. Total Collected Revenue includes Subscription Revenue plus legitimate AI Credit Revenue, subject to approved refund/chargeback treatment.
- Taxes are excluded from revenue and are not a marketing expense. Overall Business Profit equals Total Collected Revenue minus refunds and chargebacks, Stripe fees, Google Ads spend, and all approved business expenses.
- Marketing Profit equals Google-attributed revenue minus refunds and chargebacks attributable to that revenue, Stripe fees attributable to that revenue, and Google Ads spend. Marketing Profit Margin equals Marketing Profit divided by Google-attributed revenue, multiplied by 100. If deduction association is unreliable, profitability is incomplete/partially available; no allocation is invented.
- Later AI-credit purchases and upgrades are not attributed to Google solely from original customer acquisition. Only approved transaction-level evidence supports attribution; otherwise revenue remains unattributed.
- Initial reporting does not segment by MatchType, Device, or Network and does not add an `AttributionTouches` join solely for those fields.
- Date ranges and comparisons use the approved reporting periods and `America/New_York` calendar-day boundaries. Non-USD records are identified separately and not silently converted or included in comparable USD metrics.
- Google Ads reporting is distinct, read-only reporting with separately configured account/customer identity and authorization. Connected-source validation uses authorized read-only access only; existing conversion-upload/provenance behavior remains unchanged.
- Production Stripe/source completeness is validated through an authorized read-only process before any claim of complete historical financial reporting; unavailable coverage is explicitly represented.
- Authorized operators can add/edit expenses using the approved fields and categories. Focused automated coverage verifies new reporting calculations, completeness states, and access boundaries without duplicating existing attribution/payment workflow tests.
- Stripe processing, canonical source writes, attribution/consent, GA4 and Ads conversion behavior, authentication mechanisms, AI usage accounting, secrets, deployment configuration, and production records remain protected.

## Project Status

STATUS: READY_FOR_APPROVAL
