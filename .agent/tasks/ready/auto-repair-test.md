# TASK

Add a simple autonomous repair experiment to `/app/testlogic`.

This experiment validates that independent QA can detect an incorrect feature
implementation and that the Repair Agent can fix it without human diagnostic
instructions.

# BASE BRANCH

master-caio

# FEATURE

Add a button with visible text:

Auto Repair Test

Required selector:

data-testid="auto-repair-test-button"

When clicked, open a modal.

Required selector:

data-testid="auto-repair-test-modal"

The modal must contain exactly:

AUTONOMOUS REPAIR WORKS

Add a Close button:

data-testid="auto-repair-test-close"

Clicking Close must close the modal.

# DESIGN CONTRACT

Reuse existing DesignMode button and modal styling.

Do not create a new design system.

Must work at:

- Desktop: 1440x900
- Mobile WebKit: 390x844

No horizontal overflow.

# ACCEPTANCE CRITERIA

AC-01

`/app/testlogic` loads successfully.

AC-02

`auto-repair-test-button` is visible.

AC-03

Clicking `auto-repair-test-button` opens:

`auto-repair-test-modal`

AC-04

The modal message contains exactly:

AUTONOMOUS REPAIR WORKS

AC-05

`auto-repair-test-close` is visible.

AC-06

Clicking Close closes the modal.

AC-07

At 390x844 the modal remains inside the viewport.

AC-08

The page has no horizontal overflow.

AC-09

The feature causes no unexpected browser page errors.

# PROTECTED SYSTEMS

Do not modify:

- authentication
- Stripe
- subscriptions
- AI generation
- AIGenerationUsage
- database
- routing

# GIT

Create a feature branch from latest local `master-caio`:

feature/auto-repair-test-YYYY-MM-DD-HHmmss

Commit the completed implementation locally.

DO NOT PUSH.
DO NOT MERGE.
DO NOT DEPLOY.