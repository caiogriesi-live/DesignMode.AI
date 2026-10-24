# Marketing Dashboard WORK-06 implementation plan

## Reference inspection and design mapping

The approved ROAS reference is a wide desktop dashboard with the existing product's dark navigation, a light content surface, a title/date/freshness header, two rows of five white KPI cards, three time-series charts, a campaign table, and a lower marginal-analysis/recommendation area. It uses blue accents, subtle borders, compact labels, and consistent whitespace. The screenshot contains illustrative values; none will be copied into the product.

| Reference section | Existing WORK-05 contract | WORK-06 treatment |
| --- | --- | --- |
| Spend, revenue, ROAS, customers, CAC KPI cards | `MarketingFinancialMeasures` in `MarketingReportingResult` | Bind to the report response; preserve null/unavailable states and USD boundary. |
| Contribution and payback cards | Same measures | Display only when calculator marks supported; otherwise explain missing cost/adjustment inputs. |
| Spend/revenue/customer/CAC/ROAS trends | Daily Google Ads account spend and canonical purchase facts | Build from source facts for the selected period; no synthetic points. |
| Campaign performance and optimization | `CampaignFinancialReport`, `CampaignRecommendationAction` | Current Google Ads source is account-level only, so show an explicit source-unavailable state and no recommendations until campaign facts exist. |
| Marginal analysis | `MarginalPerformanceReport` | Use calculator output and its limitations; never imply causal lift from observational period comparisons. |
| GA4 funnel | No GA4 reporting contract found | Show unavailable/configuration-required; existing GA4 implementation sends events but does not read property reports. |

The existing `/init` experience has a narrow light navigation rail and light content, while the approved screenshot uses a dark left navigation. WORK-06 will follow the current `/init` shell and its blue/orange active accents, then use the reference's hierarchy, white cards, spacing, charts, and table structure. It will not replace the project dashboard or redesign unrelated pages.

## Dependency and scope findings

- WORK-05 is present at base commit `af283c668f50266aa8c0bd1b0ba2061e3d85552c`, and WORK-04 commit `2d96b4fb6f71f673fb11f54a4788dbd068223757` is an ancestor.
- WORK-05 provides tested financial calculations and purchase/account-level Google Ads readers, but no HTTP reporting endpoint. WORK-06 therefore needs a minimal operator-authorized, read-only adapter using those readers and the calculator.
- Google Ads currently provides daily account spend, not campaign metrics, clicks, impressions, or campaign attribution. No campaign-level recommendations can be truthfully populated.
- Existing GA4 is server-side Measurement Protocol event delivery (`GA4_SERVER_ENABLED`, `GA4_MEASUREMENT_ID`, `GA4_API_SECRET`); no GA4 property reporting/funnel reader exists.
- Variable costs, refunds/chargebacks, Stripe fees, and taxes are not fully available; contribution/profit/payback must remain unavailable where the calculator says inputs are missing.
- Expense reporting uses the WORK-04 operator-only DynamoDB implementation. Expense facts may be aggregated only when the same authorization gate succeeds; no financial source writes are in scope.

## Implementation steps

1. Add a bounded date-range reporting response/endpoint, invoking operator authorization before any external/DynamoDB reads and reusing WORK-05 sources/calculations.
2. Keep customer identifiers and credentials server-side; return only aggregate/daily values and source coverage.
3. Add a distinct authenticated Marketing Dashboard route/components in the existing client shell, with date selection, refresh, KPI cards, responsive SVG charts, campaign and GA4 unavailable states, and marginal analysis.
4. Add focused deterministic tests for authorization, aggregation/serialization, date filtering, missing sources, and UI formatting/state behavior; no advertising or financial writes.
5. Audit configuration and write local/prod readiness and manual QA instructions; run builds/tests and browser checks if the environment supports them, recording exact limitations.
