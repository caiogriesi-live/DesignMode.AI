# Marketing Dashboard WORK-07 implementation plan

## Baseline and dependencies

- Start from verified WORK-06 commit `1af8b4684e41ca6f5aa79d5c52467a0cca1062ab` on a dedicated branch/worktree. WORK-04 (`2d96b4fb6f71f673fb11f54a4788dbd068223757`) and WORK-05 (`af283c668f50266aa8c0bd1b0ba2061e3d85552c`) are ancestors.
- Reuse `GoogleAdsReportingSource`, `PurchaseReportingSource`, `MarketingFinancialCalculator`, the operator-only WORK-06 endpoint, and the existing dashboard. Do not add credentials, reporting systems, or payment flows.
- Inspect approved project and WORK-01–06 contracts. The approved project requires transaction-level attribution evidence and says missing data must stay unavailable; its original WORK-07 text is QA-oriented, while this user-approved task extends the scope to campaign reporting and advisory recommendations.

## Data mapping and limits

- Add a read-only Google Ads campaign query for campaign ID/name/status, daily spend, clicks, impressions, conversions, conversion value, customer currency/timezone, and current campaign budget facts. Keep platform-reported conversions/conversion value separate from canonical purchases.
- Use canonical paid purchases classified by WORK-03. A purchase is campaign-attributed only when its transaction snapshot GCLID exactly matches a Google Ads `click_view` result for that campaign and click date. Never return click IDs or customer identifiers to the browser. `click_view` is single-day queryable and limited to the most recent 90 days, so older, missing, or unmapped evidence stays unattributed/partial. Do not treat a captured query-string campaign ID by itself as verified.
- Deduplicate by canonical transaction ID. Conflicts in transaction-to-campaign evidence are excluded from campaign attribution and reported as a coverage limitation. New customers are unique canonical customer keys on qualifying first subscription payments. Renewals and AI-credit purchases remain revenue only when their own transaction evidence maps; customer-origin history is not inherited.
- Campaign contribution/profitability remain unavailable while campaign-associated variable costs, refunds/chargebacks, and Stripe fees lack validated sources. Do not allocate operating expenses or account-level deductions to campaigns.
- Reporting currency remains source currency; only comparable USD campaign ratios are calculated. Report Google Ads timezone and conversion metrics' date basis. Purchases use WORK-01 `America/New_York` paid-date semantics. Make differing dates and conversion lag visible; do not present campaign conversions as verified purchases.

## Recommendation policy

- Reuse WORK-05 action classification only when verified campaign evidence is sufficient and policy is explicitly configured. Add an explicit policy for scale/reduce/pause percentages, maximum bounds, minimum click/spend/customer evidence, target/minimum ROAS and optional CAC threshold. No numeric business-policy default is approved, so production recommendation state remains `CONFIGURATION_REQUIRED` until values are supplied.
- Use policy-supplied percentages only, display the configured action and percentage as advice, and compute hypothetical daily-budget delta only when the budget is known, in a supported currency, and not shared with other campaigns. Never send campaign, budget, bid, targeting, or conversion-upload mutations.
- Add deterministic synthetic tests for every action, insufficient evidence, missing policy, range/bounds validation, currency, shared budgets, attribution joins, deduplication/conflicts, date/click-age limits, and authorization.

## Delivery and verification

1. Record the exact WORK-06 baseline test results before edits.
2. Add focused read-only campaign and GCLID reporting adapters and aggregate joins using existing contracts; preserve unavailable state for unsupported evidence.
3. Extend dashboard campaign rows and recommendations with platform/canonical metrics clearly distinguished, labels, rationale, percentages, confidence/limitations, and freshness.
4. Document exact settings, local/production locations, external prerequisites, safe Google Ads read-only validation, attribution limits, and manual QA.
5. Build affected projects sequentially; run focused, full regression, Chromium desktop and WebKit mobile tests against mocks. Perform live Google Ads validation only if authorized credentials are already available; otherwise report UNVERIFIED. Capture screenshots and compare them with the approved PNG.
6. Audit staged paths and secrets, `git diff --check`, commit only verified WORK-07 files. Do not merge, push, deploy, or modify production.
