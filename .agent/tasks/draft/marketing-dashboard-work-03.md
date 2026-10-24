# Add Authorized Read-Only Purchase Reporting and Classification

## Objective

Provide company-authorized, read-only reporting inputs from canonical paid purchase records and supported financial source data. Classify legitimate subscription payments and paid AI-credit purchases for downstream reporting, apply only approved transaction-level attribution evidence, and expose source coverage gaps without changing payment or purchase behavior.

## Requirements

- Implement the purchase and supported financial-source reporting path using the approved WORK-01 reporting contracts and source semantics. This work item depends on WORK-01; do not redefine its period, comparison, attribution, currency, or completeness semantics.
- Read canonical `Purchases` as positive paid transaction facts. Use existing purchase fields, including `PaymentStatus`, `PurchaseType`, `BillingReason`, `IsFirstPayment`, `IsRenewal`, `IsTestCreditPurchase`, `Amount`, `AmountMinor`, `Currency`, `PaidAt`, `TransactionId`, and existing attribution references/snapshot fields, as applicable.
- Classify legitimate initial subscription payments, renewals, and paid plan upgrades as Subscription Revenue when supported by canonical paid purchase facts. Classify legitimate paid AI-credit purchases separately as AI Credit Revenue; exclude test AI-credit purchases. Do not treat unpaid, non-positive, or otherwise non-qualifying records as collected revenue.
- Reuse existing transaction attribution evidence. Attribute a transaction only when approved transaction-level evidence in the existing purchase/checkout snapshot supports the attribution. Do not carry a customer's original acquisition attribution onto later AI-credit purchases or paid upgrades without supporting transaction-level evidence; leave unsupported transactions unattributed.
- Read refund, chargeback, tax, and Stripe fee facts only from supported financial sources and only to the extent their coverage and transaction association are established. Use the approved source-coverage states to represent unsupported, incomplete, unvalidated, or unavailable facts and historical periods. Missing data or uncertain association must not be presented as zero or used to claim complete financial coverage.
- Keep returned source currency and transaction amounts faithful to source data. Apply WORK-01 treatment for non-USD amounts and do not perform currency conversion or invent tax, refund, chargeback, fee, or allocation values.
- Enforce explicit server-side DesignMode administrator/company-operator authorization for every externally callable company-wide purchase or financial-source reporting path introduced by this work. Ordinary DesignMode customers must not receive company financial data; UI hiding alone is insufficient.
- Keep the capability read-only and separate from payment transaction paths. Do not add financial aggregation or dashboard UI, expense-ledger behavior, attribution capture or joins, or new source/payment contracts beyond what is needed to expose this work item's reporting inputs.
- Add focused automated coverage for supported purchase classifications, test AI-credit exclusion, transaction-level attribution limits, source coverage and unavailable/partial handling, and the server-side access boundary. Use test data or isolated source seams; verification must not perform financial transactions or write to production sources.

## Acceptance Criteria

AC-01: Given canonical paid purchase records, the reporting output classifies supported legitimate initial subscription payments, renewals, and paid plan upgrades as Subscription Revenue, using existing source fields and without changing the source records.

AC-02: Given canonical paid AI-credit purchase records, legitimate purchases appear separately as AI Credit Revenue, while records marked `IsTestCreditPurchase` are excluded. Unpaid, non-positive, and non-qualifying records are not reported as collected revenue.

AC-03: Attribution in the reporting output is supported by transaction-level evidence in existing purchase/checkout attribution data. A later AI-credit purchase or upgrade without such evidence remains unattributed even when the customer's earlier acquisition was attributed.

AC-04: Supported refund, chargeback, tax, and fee facts are exposed only with source coverage and association status supported by the data. Where coverage is unsupported, incomplete, unvalidated, unavailable, or association is uncertain, the output communicates the corresponding approved incomplete/partial/unavailable state; it does not substitute zero, infer an amount, or claim complete historical coverage.

AC-05: Reported amounts retain their source currency. Non-USD records follow the approved WORK-01 separate/non-comparable treatment, and no conversion or deduction allocation is introduced.

AC-06: Every externally callable endpoint or request path added for company-wide purchase/financial-source reporting denies ordinary customer access server-side and permits only the approved DesignMode administrator/company-operator boundary. If the implementation exposes no external path in this work item, no unprotected path is introduced and any dependent endpoint must apply this boundary before returning the data.

AC-07: Focused automated tests cover initial subscription, renewal, paid upgrade, paid AI-credit, test credit, unsupported attribution, and incomplete/unavailable financial-source cases, plus authorization for any external reporting path. Tests use isolated/fake source data and do not invoke checkout, payment, webhook, purchase-write, credit grant/reversal, or production financial operations.

AC-08: Existing Stripe payment and checkout behavior, canonical `Purchases` writes, credit grant/reversal behavior, attribution capture and storage, checkout snapshots, and authentication/customer-authorization mechanisms remain unchanged. No financial aggregation, expense ledger, Google Ads reporting, or financial dashboard UI is added by this work item.

## Protected Systems

Stripe; canonical `Purchases`; production financial records and source data; attribution data and checkout snapshots; existing authentication and customer authorization; checkout, webhook, payment, purchase-write, credit grant/reversal, and ledger flows. Source access for this work is read-only. Do not change attribution capture, source writes, or payment behavior.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.

## Planner Status

STATUS: READY_FOR_APPROVAL
