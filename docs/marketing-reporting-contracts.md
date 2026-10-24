# Marketing reporting contracts and source semantics

The shared DTOs in `DesignMode.AI.Shared.Models.MarketingReporting` carry reporting results only. They do not replace purchase measurement, attribution capture, adjustment records, or source writes. `MarketingReportingResult` describes selected and comparison periods, USD reporting basis, measures, source coverage/freshness, attribution limitations, errors, and partial availability. A missing measure is `null` with an explicit state/reason; it is never an implicit zero. `Complete`, `Partial`, `Unavailable`, `Error`, and `Stale` distinguish source quality. Production completeness must not be claimed until authorized read-only source validation establishes it.

## Period and comparison semantics

The selections are `Today`, `Last7Days`, `Last30Days`, `MonthToDate`, `PreviousMonth`, `YearToDate`, and `CustomRange`. `StartDate` and `EndDate` are inclusive calendar days in `America/New_York`. `StartInclusive` is midnight at the first date in that zone; `EndExclusive` is midnight after the final date in that zone. Resolve the two local midnights independently and represent them as instants. Do not add a fixed 24-hour duration: a local day spanning a daylight-saving change can be 23 or 25 hours.

Each comparison is the immediately preceding interval with the same number of New York calendar days as the selected interval, ending the day before it begins. Its local date and instant boundaries use the same rules. Preset dates resolve relative to the report's captured current date. The contract intentionally does not fix a preset-to-date mapping; the product's approved date resolution supplies selected dates, then the equivalent preceding interval is derived from them.

## Purchase source mapping

Reuse `DesignMode.AI.Server.Services.PurchaseRecord` and its nested `AttributionSnapshot` as the canonical positive payment fact; do not create replacement payment or attribution records. A report input qualifies only under existing canonical paid-purchase rules. Map payment classification through `PaymentStatus`, `PurchaseType`, `BillingReason`, `IsFirstPayment`, and `IsRenewal`; amount and reporting date through `Amount`, `AmountMinor`, `Currency`, and `PaidAt`; transaction identity through `TransactionId`. Existing purchase linkage includes `CheckoutAttemptId`, `AttributionSnapshotId`, `AcquisitionId`, `Snapshot`, and snapshot acquisition/click evidence (including GCLID/GBRAID/WBRAID when present).

Subscription revenue includes legitimate collected initial subscription payments, renewals, and paid plan upgrades as supported by canonical payment records. AI Credit Revenue is a separate measure and excludes rows where `IsTestCreditPurchase` is true. Total Collected Revenue remains distinct and combines eligible subscription and AI credit revenue subject to supported refund/chargeback treatment. Do not count customer-origin attribution as transaction evidence for another purchase.

Google attribution requires approved transaction-level evidence associated with that purchase. An acquisition/customer origin by itself does not transfer attribution to later AI-credit purchases or upgrades. Otherwise the revenue remains unattributed. This contract adds no capture fields, touch joins, match types, device/network rules, or inherited attribution.

## Other sources, deductions, and limits

| Input | Meaning and limitation |
| --- | --- |
| Refunds and chargebacks | Deductions only from supported source facts with reliable association to purchase; canonical purchase rows are positive payment facts, not an adjustment ledger. |
| Stripe fees | Separate expense measure only from supported fee facts and reliable associations where attribution is claimed. Purchase rows do not establish fee coverage. |
| Taxes | Excluded from revenue and not a marketing expense. Represent tax treatment only where source facts support it; purchase rows do not establish tax exclusion/completeness. |
| Google Ads spend | Source coverage/freshness must be explicit. ROAS is Google-attributed revenue divided by Google Ads spend only when both are comparable supported USD values. |
| Business expenses | Overall business profit includes supported approved business expenses; absent expense coverage is unavailable/partial, not zero. |

Overall Business Profit follows approved treatment: Total Collected Revenue less refunds/chargebacks, Stripe fees, Google Ads spend, and approved business expenses. Marketing Profit is Google-attributed revenue less refunds/chargebacks and fees attributable to that revenue and Google Ads spend. Marketing Profit Margin is Marketing Profit divided by Google-attributed revenue times 100. CAC and ROAS remain unavailable when required inputs are unavailable or incomparable. Unsupported adjustment-to-purchase or adjustment-to-Google-revenue association makes marketing profitability incomplete/partial; no allocation rule is authorized. Do not claim complete marketing profitability where that association or coverage is missing.

## Currency

Reporting currency is USD. Keep each non-USD amount and source record separately identified by its original currency; do not convert it. Exclude non-USD values from comparable USD totals, profit, CAC, ROAS, and margins. Their existence and coverage remain visible through `NonUsdAmounts` and source status. A missing USD input is unavailable/partial, not zero.
