# Define Marketing Reporting Contracts and Source Semantics

## Objective

Define the missing reporting DTOs and contracts for the approved marketing dashboard metrics, including supported periods and comparisons, source-field mappings, attribution semantics, and explicit source quality/completeness states. Reuse existing purchase and attribution contracts; document unsupported associations and non-USD treatment without changing source behavior or adding unapproved business rules.

## Requirements

- Define reporting contracts sufficient to represent selected and comparison periods, reporting currency, approved revenue and profitability measures, source freshness, source coverage/completeness, attribution limitations, errors/partial availability, and separately identified non-comparable currency amounts.
- Support the approved date selections: Today, last 7 days, last 30 days, month-to-date, previous month, year-to-date, and custom range. Interpret date ranges as inclusive `America/New_York` calendar days; represent the corresponding instant boundaries consistently across daylight-saving transitions.
- Define and document comparison-period semantics for each approved date selection, consistent with the approved requirement for meaningful, immediately preceding equivalent comparisons. The task does not prescribe specific preset mappings; do not treat an unapproved mapping as a product requirement.
- Map canonical positive paid purchase inputs to existing `PurchaseRecord` fields: `PaymentStatus`, `PurchaseType`, `BillingReason`, `IsFirstPayment`, `IsRenewal`, `IsTestCreditPurchase`, `Amount`, `AmountMinor`, `Currency`, `PaidAt`, `TransactionId`, and supported attribution references/snapshot fields. Preserve subscription revenue, AI credit revenue, and total collected revenue as distinct approved measures; exclude test credit purchases from AI Credit Revenue.
- Reuse existing attribution evidence (`AttributionSnapshot` and its acquisition/click identifiers and fields) and checkout/purchase references. State that Google attribution requires approved transaction-level evidence; customer-origin attribution alone does not transfer to later credit purchases or upgrades. Do not introduce new capture fields, touch joins, or segmentation.
- Define source mapping and known gaps for deductions and other reporting inputs. Canonical purchase rows are positive payment facts, not a complete adjustment ledger. Refunds, chargebacks, Stripe fees, and taxes may be represented only when supported source facts and reliable associations exist; unknown coverage or association must be marked unavailable/partial/unsupported, never silently treated as zero. Document that unsupported adjustment-to-purchase or adjustment-to-Google-revenue associations prevent complete marketing profitability and no allocation rule is authorized.
- Define explicit data-quality/completeness states that distinguish at minimum complete, partial, unavailable, and error/stale source conditions, and permit explanation of coverage gaps and association limitations. Do not imply production source completeness before authorized read-only validation.
- Specify that non-USD records and amounts remain separately identified, are not converted, and are excluded from comparable USD totals and ratios. Taxes are excluded from revenue and are not a marketing expense, consistent with the approved project.
- Keep contracts limited to missing reporting semantics. Do not recreate or alter attribution capture, checkout snapshots, canonical purchase measurement, adjustment/payment source records, access controls, or source writes.

## Acceptance Criteria

AC-01: Reporting contracts are defined for the approved measures and carry selected period, comparison period, USD reporting basis, freshness, source coverage/completeness, attribution limitations, and partial/unavailable/error information without representing missing source values as zero.

AC-02: The contract documentation or types enumerate all seven approved date selections and specify inclusive `America/New_York` calendar-day boundaries, including consistent start/end instants over daylight-saving transitions.

AC-03: The reporting contract documents comparison semantics for each approved date selection and makes clear how each comparison is immediately preceding and equivalent to its selected period, without introducing a preset mapping not approved by the project.

AC-04: Source mapping identifies the existing `PurchaseRecord` fields used for payment classification, revenue amount/currency/date, transaction identity, test credit exclusion, and existing attribution linkage. It reuses existing purchase and attribution structures without defining replacement capture or payment-measurement contracts.

AC-05: The documented attribution semantics require transaction-level approved evidence for Google attribution and explicitly leave later AI-credit purchases/upgrades unattributed absent such evidence; no match type, device, network, inherited attribution, or new touch join is introduced.

AC-06: Source-gap documentation states that canonical purchases do not establish complete refunds, chargebacks, Stripe fees, tax exclusion, or historical source coverage; unsupported or uncertain deduction associations remain explicit and prevent a complete marketing profitability claim without an invented allocation.

AC-07: Non-USD source records are separately represented and excluded from comparable USD totals and ratios without currency conversion. Taxes are excluded from revenue and are not treated as a marketing expense.

AC-08: The change is confined to reporting contracts and their source/semantic documentation; no source writes, existing attribution/checkout/purchase contracts or behavior, authentication/access controls, payment flows, or financial records are changed.

## Protected Systems

Existing attribution capture and storage, checkout snapshots, canonical purchase measurement and purchase records, authentication and authorization, Stripe/payment processing, financial source data and source writes, AI-credit purchase/reversal behavior, Google conversion delivery/provenance, and production records. Contract work must not change source writes or access controls.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.


