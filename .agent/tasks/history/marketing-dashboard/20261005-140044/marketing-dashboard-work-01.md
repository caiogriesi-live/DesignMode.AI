# Define Marketing Reporting Contracts and Period Semantics

## Objective

Define shared reporting contracts and aggregation inputs for the approved marketing dashboard metrics, periods, comparisons, attribution, and currency treatment. Map approved inputs to source fields available in the repository and explicitly represent unavailable or unverified inputs without treating them as zero or inventing business rules.

## Requirements

- Define request/response and aggregation input semantics for date-range selection: Today, last 7 days, last 30 days, month-to-date, previous month, year-to-date, and custom range. Calendar days use the `America/New_York` reporting timezone; boundaries must account for daylight-saving transitions.
- Define comparable immediately preceding equivalent periods where meaningful. Document the exact period relationship, including how custom ranges are matched, and represent cases with no meaningful comparison explicitly.
- Define separate measures for subscription initial payments and renewals, one-time AI credit purchases, gross collected amounts, refunds/chargebacks, taxes, Stripe fees, Google Ads spend, approved business expenses, overall business profit, marketing profitability, CAC, and ROAS according to the approved project definitions. Keep total revenue distinct from Google-attributed revenue.
- Define attribution inputs and output states using the approved initial Google click/acquisition basis associated with acquisition and checkout. Keep unattributed first-time paying customers and revenue separately identifiable; do not assign them to Google or define a multi-touch model.
- Use available purchase fields as identified source candidates: `PurchaseRecord.Amount`/`AmountMinor`, `Currency`, `PaidAt`, `PaymentStatus`, `PurchaseType`, `IsFirstPayment`, `IsRenewal`, `Snapshot` click identifiers (`Gclid`, `Gbraid`, `Wbraid`) and acquisition context, including `AcquisitionId`. Preserve the distinction between recorded payment currency/amount and reporting currency.
- Identify source fields needed for refunds/chargebacks, taxes, Stripe fees, Google Ads spend, and expenses. If repository inspection finds no authoritative representation, mark that input unavailable or unverified in the contract and its aggregation status; do not infer zero, fabricate source fields, or claim metric completeness.
- Define USD as the comparable currency. Non-USD records must be representable separately as non-comparable and excluded from comparable USD totals and ratios; no currency conversion is introduced.
- Provide explicit states or metadata sufficient for downstream aggregation to distinguish available, unavailable, incomplete/partial, and not-applicable inputs or results, with source/freshness context where available. An unavailable input must not be silently presented as a measured zero.
- Keep this work limited to reporting contracts, field/availability mapping, and period/aggregation semantics needed by WORK-02 through WORK-06. Do not implement source integrations, data access paths, persistence, dashboard UI, or metric aggregation execution.

## Acceptance Criteria

AC-01: Reporting contracts expose the seven approved date-range choices and a custom range, with period boundaries defined as calendar days in `America/New_York`, including daylight-saving boundary behavior.

AC-02: Contract semantics identify the immediately preceding equivalent comparison period for each supported range where meaningful, define custom-range comparison, and represent a non-applicable comparison without substituting an arbitrary period.

AC-03: The metric/input definitions separately identify subscription initial payments, subscription renewals, one-time AI credit purchases, total collected revenue, Google-attributed revenue, refunds/chargebacks, taxes, Stripe fees, Google Ads spend, approved expenses, overall business profit, marketing profitability, CAC, and ROAS. The definitions follow the approved project formulas and preserve the separation of total and Google-attributed revenue.

AC-04: Attribution contract inputs and outputs support the approved Google click/acquisition basis and distinguish Google-attributed from unattributed first-time paying customers and revenue. The contract does not encode multi-touch attribution or assign unattributed records to Google.

AC-05: A source mapping documents the available purchase fields and their roles, including amount, currency, paid timestamp, payment status, purchase type, first-payment/renewal flags, and captured attribution/acquisition context. Any required source element not found or not verifiable in the inspected repository is identified as unavailable/unverified rather than presumed present.

AC-06: Refund/chargeback, tax, fee, spend, and expense inputs each have explicit availability/status semantics. Missing inputs cannot be interpreted as zero, and downstream consumers can distinguish incomplete results from complete results.

AC-07: Contract semantics make USD-comparable and non-USD non-comparable data distinguishable; non-USD values are excluded from comparable USD totals and ratios without conversion.

AC-08: Contract and aggregation input definitions include sufficient source/freshness or completeness metadata to communicate unavailable or partial inputs and avoid presenting incomplete metrics as complete.

AC-09: Changes are confined to shared reporting contracts and documentation/mapping required to define them. No source integration, production data access path, persistence, dashboard UI, or executable financial aggregation is added as part of this work item.

AC-10: Existing Stripe purchase measurement and attribution capture behavior remain unchanged: no changes to transaction processing, verified purchase writes, refund processing, attribution capture/consent, authentication, or existing conversion measurement/delivery behavior.

## Protected Systems

Stripe purchase measurement and verified transaction flows; canonical `Purchases` writes; refund processing; attribution and consent capture; authentication/authorization; existing Google conversion measurement/delivery; production databases and financial records. Existing data may be inspected as a source-field reference only; this work defines contracts and does not alter protected systems.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.

## Planner Status

STATUS: READY_FOR_APPROVAL
