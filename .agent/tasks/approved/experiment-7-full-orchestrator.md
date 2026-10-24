# Add a full autonomous orchestrator test control

## Objective

Add a FULL ORCHESTRATOR TEST interaction to `/app/testlogic` that opens a closable modal displaying the exact requested message, while preserving all existing test controls and behavior. This control supports the experiment of verifying the autonomous engineering orchestrator’s full post-approval workflow through human review or a human-required outcome.

## Requirements

- Add a button labeled `FULL ORCHESTRATOR TEST` to `/app/testlogic`.
- Activating the button opens a modal displaying exactly `FULL AUTONOMOUS ORCHESTRATOR WORKS`.
- Provide a visible Close control that closes the modal.
- Use stable selectors:
  - `data-testid="full-orchestrator-test-button"`
  - `data-testid="full-orchestrator-test-modal"`
  - `data-testid="full-orchestrator-test-message"`
  - `data-testid="full-orchestrator-test-close"`
- Preserve all existing test controls and their behavior.
- Keep the existing route authentication and authorization behavior unchanged.

## Acceptance Criteria

AC-01: At desktop viewport 1440x900, `/app/testlogic` loads using the existing QA authentication approach, and `full-orchestrator-test-button` is visible with the label `FULL ORCHESTRATOR TEST`.

AC-02: At desktop viewport 1440x900, activating `full-orchestrator-test-button` displays `full-orchestrator-test-modal`, whose visible `full-orchestrator-test-message` text is exactly `FULL AUTONOMOUS ORCHESTRATOR WORKS`.

AC-03: At desktop viewport 1440x900, `full-orchestrator-test-close` is visible and activating it closes the modal.

AC-04: At mobile WebKit viewport 390x844, `/app/testlogic` loads using the existing QA authentication approach, and the new button opens the modal with the exact requested message; the Close control closes it.

AC-05: At 1440x900 and 390x844, the page has no unintended horizontal overflow. With the modal open at 390x844, the modal remains within the viewport.

AC-06: Existing test controls remain present and their existing interactions continue to work.

AC-07: The new modal exposes dialog semantics, including `role="dialog"` and `aria-modal="true"`, consistent with accessible modal behavior.

## Protected Systems

Do not modify production authentication or authorization behavior, Stripe, subscriptions, payments, AI usage accounting, production databases, secrets, routing, or deployment configuration.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.
