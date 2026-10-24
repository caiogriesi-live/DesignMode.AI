# Add an isolated authenticated Playwright harness for `/app/testlogic`

## Objective

Enable autonomous Playwright acceptance tests to authenticate with a test-only identity and exercise the existing `/app/testlogic` route in Chromium and WebKit while preserving the application's production authentication and authorization behavior.

## Requirements

- Inspect and account for the existing `JwtAuthenticationStateProvider` and `AuthService` flow, including the `authToken` and `authTokenExpiration` local-storage values, and the `MainLayout` authentication and expiry checks.
- Provide a QA authentication mechanism suitable for the existing Blazor client architecture that uses no real user credentials or production secrets and is restricted to local QA/development/test execution.
- Keep `/app/testlogic` authenticated. Do not weaken or bypass `MainLayout` checks, make the route anonymous, or change production authentication or authorization behavior.
- Keep changes limited to autonomous QA infrastructure and any strictly necessary explicit development/test-only support.
- Exercise the existing `data-testid="auto-agent-test-button"` control and verify that its existing interaction works.
- Preserve the current QA build, test, evidence collection, cleanup, and safety behavior while extending browser acceptance to cover the authenticated route.
- Run independent browser acceptance for Chromium at exactly 1440x900 and WebKit at exactly 390x844 so a failure in one browser does not prevent the other browser from executing.
- Do not change unrelated application features or protected systems.

## Acceptance Criteria

AC-01: The QA authentication path uses a test-only identity and does not require real user credentials or production secrets.

AC-02: An unauthenticated visit to `/app/testlogic` continues to follow the existing `MainLayout` redirect behavior to `/login`; no anonymous access or weakened authentication check is introduced.

AC-03: With the QA test identity established, Chromium acceptance at exactly 1440x900 reaches `/app/testlogic`, confirms the final URL remains on that route rather than `/login`, and confirms `auto-agent-test-button` is visible and usable.

AC-04: With the QA test identity established, WebKit acceptance at exactly 390x844 reaches `/app/testlogic`, confirms the final URL remains on that route rather than `/login`, and confirms `auto-agent-test-button` is visible and usable.

AC-05: Activating `auto-agent-test-button` in both browsers opens the existing `auto-agent-test-modal`, which displays `AUTONOMOUS DEVELOPMENT WORKS`; activating `auto-agent-test-close` closes the modal.

AC-06: Chromium and WebKit acceptance execute independently; a failure in either browser does not prevent the other browser's acceptance from running, and results for both are recorded.

AC-07: At both required viewport sizes, the page has no unintended horizontal overflow (`document.documentElement.scrollWidth <= document.documentElement.clientWidth`).

AC-08: Existing QA build and test execution, evidence collection, temporary host/spec cleanup, and safety behavior remain operational and are not weakened.

AC-09: Relevant solution builds and automated tests pass with the QA harness changes; no production authentication, authorization, or deployment behavior changes are introduced.

## Protected Systems

Do not modify production authentication or authorization semantics, Stripe, subscriptions, payment processing, AI usage accounting, production databases, secrets, routing behavior, or deployment configuration. Do not alter `MainLayout` authentication checks to permit access for the harness.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.
