# Add distinct read-only Google Ads reporting integration

## Objective

Provide the company marketing reporting system with date-granular Google Ads spend through a separately authorized, read-only integration. The minimum output is daily spend amount and currency, the reporting date, and the Google Ads account/customer identity queried. Include any additional reporting fields only when explicitly supported by the approved reporting contract; generic measures alone do not imply additional required fields. Surface source freshness, errors, and partial or unavailable data without changing conversion delivery, provenance, or GA4 behavior.

## Requirements

- Use the existing shared marketing reporting period and source-coverage contracts where applicable, including `MarketingReportingPeriod`, `ReportingSourceCoverage`, `ReportingDataState`, and `MarketingReportingSource.GoogleAds`; conform to the approved contract rather than redefining reporting semantics in this work item.
- Retrieve Google Ads reporting data for requested inclusive calendar-date ranges at day granularity. Provide the date, spend amount, currency, and account/customer identity for each result; preserve returned currency and identity without inferring or converting currencies. Additional fields are required only if explicitly present in the approved reporting contract.
- Configure reporting account/customer identity, credentials, and reporting authorization independently from legacy conversion-upload configuration and Ads delivery/provenance sidecars. Existing upload settings do not establish reporting authorization.
- Restrict the integration to reporting/read operations. Do not add campaign/account mutations, conversion uploads, or any invocation of upload operations.
- Report source freshness and explicit complete, partial, unavailable, error, or stale status. Determine `Stale` objectively: when `FreshThrough` is present and is earlier than the requested period's `EndExclusive`, mark the source `Stale`, because the source does not establish freshness through the full requested interval. Do not infer `Complete` solely from freshness; completeness requires source coverage evidence. If `FreshThrough` is absent, do not label the source stale from age alone; report the state supported by coverage/configuration/error evidence and explain unknown freshness. Never represent failed, missing, or incomplete source data as zero spend or complete coverage.
- `ReportingSourceCoverage.State` is one value. Apply this precedence when conditions overlap: retrieval `Error` first, then configuration/access `Unavailable`, then incomplete `Partial`, then `Stale` for otherwise covered results whose `FreshThrough` is earlier than requested `EndExclusive`, otherwise `Complete` only with coverage evidence. Preserve freshness independently in `FreshThrough` and describe any overlapping stale condition in `Explanation` (for example, an error/unavailable/partial result may also have an old `FreshThrough`). Thus stale partial, stale unavailable, and stale error keep their primary state while retaining the freshness fact. Missing `FreshThrough` alone does not imply stale; explain unknown freshness. Never represent failed, missing, or incomplete source data as zero spend or complete coverage.
- Any endpoint or externally callable request path introduced by this work item to retrieve company Google Ads reporting data must enforce the approved DesignMode administrator/company-operator authorization server-side. If this work item adds only an internal source adapter with no endpoint or externally callable path, endpoint exposure and authorization enforcement remain required in the dependent reporting API work item; do not expose the adapter directly.
- Keep Google credentials server-side and out of logs, API responses, and client-visible configuration.
- Connected-source validation, if performed, must use the separately authorized company account/customer with read-only reporting access only. If that external prerequisite is unavailable, expose an unavailable/configuration state without claiming connected-source validation.
- Do not implement financial aggregation, purchase reporting, expenses, or financial dashboard UI in this work item.

## Acceptance Criteria

AC-01: For a requested reporting period, the Google Ads source returns daily spend results containing the reporting calendar date, spend amount, returned currency, and queried account/customer identity. Dates and inclusive period boundaries follow the approved reporting contract. No additional reporting field is required unless explicitly defined by that contract.

AC-02: The result exposes Google Ads source coverage and freshness using the approved reporting contract. When conditions overlap, the single `State` value follows this precedence: failed retrieval `Error`, missing configuration/access `Unavailable`, incomplete coverage `Partial`, stale otherwise-covered result `Stale`, and `Complete` only with coverage evidence and no earlier condition. For any overlapping stale condition, `FreshThrough` and/or `Explanation` preserve the freshness fact while `State` preserves the primary error, unavailable, or partial condition. If `FreshThrough` is earlier than requested `EndExclusive` and no higher-precedence condition applies, state is `Stale`; absent `FreshThrough` alone does not imply `Stale`. None of these conditions is presented as complete zero spend.

AC-03: Reporting credentials, account/customer identity, and reporting authorization are configured independently of conversion-upload settings and delivery/provenance data. A reporting request does not depend on upload being enabled or on conversion delivery eligibility/provenance.

AC-04: The reporting service accepts an injected/fake reporting transport (or equivalent isolated request seam). Automated request-level tests verify that reporting uses only the reporting/read method and resource, and cannot invoke conversion-upload or other write methods. These tests run without production account credentials and demonstrate no dependency on `IGoogleConversionTransport` or its upload operation.

AC-05: Missing reporting configuration and representative authorization, API, and partial-result failures produce safe, explicit source states without exposing credentials or sensitive token material in logs or responses.

AC-05a: Any endpoint or externally callable request path added by this work item enforces the approved company administrator/operator boundary server-side. If implementation is limited to an internal source adapter, it is not externally exposed, and the dependent reporting API enforces that boundary before returning company Google Ads data.

AC-06: Existing GA4 measurement, Ads conversion delivery/provenance behavior, and consent/measurement behavior remain unchanged; existing relevant conversion and measurement tests continue to pass as part of normal project verification.

AC-07: The new reporting source has focused automated coverage for daily granularity and boundaries, spend/currency/account identity preservation, freshness and stale rule, complete/partial/unavailable/error states, and strict separation from conversion upload. Verification is limited to tests and authorized read-only source access; no transaction or write operation is used.

## Protected Systems

Google credentials, account identity and permissions; existing Google Ads conversion delivery and provenance; GA4 measurement; consent and measurement behavior. No conversion uploads, write access, or modification to existing conversion/provenance configuration or behavior. Do not modify authentication, production financial records, or deployment configuration.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.


