# Add an autonomous repair pipeline test control

## Objective

Add an independently testable AUTO REPAIR PIPELINE TEST interaction to `/app/testlogic` that opens a modal with the exact requested message and can be closed, while preserving the existing test controls and their behavior.

## Requirements

- Add a button labeled `AUTO REPAIR PIPELINE TEST` to `/app/testlogic`.
- Activating the button opens a modal whose visible message is exactly `AUTONOMOUS REPAIR PIPELINE WORKS`.
- Provide a Close control consistent with the existing test modals.
- Use these stable selectors:
  - `data-testid="repair-pipeline-test-button"`
  - `data-testid="repair-pipeline-test-modal"`
  - `data-testid="repair-pipeline-test-message"`
  - `data-testid="repair-pipeline-test-close"`
- Preserve all existing test controls and behavior.
- Use the existing QA authentication harness to access the authenticated `/app/testlogic` route. Do not change production authentication or authorization behavior.
- Run independent browser checks in Chromium at 1440x900 and WebKit at 390x844.
- Verify there is no horizontal overflow at either viewport and that the mobile modal remains inside the viewport.
- Client and Server builds must succeed.
- If existing automated tests fail, classify the failures against the baseline where necessary; do not automatically attribute pre-existing failures to this feature.

## Acceptance Criteria

AC-01: Using the existing QA authentication harness, Chromium at exactly 1440x900 reaches `/app/testlogic` as an authenticated test session.

AC-02: In Chromium, `repair-pipeline-test-button` is visible and labeled `AUTO REPAIR PIPELINE TEST`; activating it displays `repair-pipeline-test-modal`.

AC-03: In Chromium, the visible `repair-pipeline-test-message` text is exactly `AUTONOMOUS REPAIR PIPELINE WORKS`; activating `repair-pipeline-test-close` closes the modal.

AC-04: Using the existing QA authentication harness, WebKit at exactly 390x844 reaches `/app/testlogic` as an authenticated test session, and its browser check executes independently of Chromium's result.

AC-05: In WebKit, the new button opens the modal, the visible message is exactly `AUTONOMOUS REPAIR PIPELINE WORKS`, and the Close control closes the modal.

AC-06: At both required viewport sizes, the page has no horizontal overflow (`document.documentElement.scrollWidth <= document.documentElement.clientWidth`). With the modal open in WebKit at 390x844, the modal remains within the viewport.

AC-07: Existing test controls remain present and their existing interactions continue to work in the browser acceptance checks.

AC-08: Client and Server project builds succeed.

AC-09: Automated test outcomes are reported, and any failures are compared with the existing baseline where needed so pre-existing failures are not attributed to this feature without evidence.

## Protected Systems

Do not modify production authentication or authorization behavior, Stripe, subscriptions, payments, AI usage accounting, production databases, secrets, routing, or deployment configuration.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.
