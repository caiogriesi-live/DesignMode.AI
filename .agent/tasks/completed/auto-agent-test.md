# TASK

Add a simple autonomous QA experiment to `/app/testlogic`.

# BASE BRANCH

master-caio

# FEATURE

Add a button with visible text:

Auto Agent Test

Required selector:

data-testid="auto-agent-test-button"

When clicked, open a modal.

Required selector:

data-testid="auto-agent-test-modal"

The modal must contain exactly:

AUTONOMOUS DEVELOPMENT WORKS

Add a Close button:

data-testid="auto-agent-test-close"

Clicking Close must close the modal.

# DESIGN

Reuse existing DesignMode button and modal styling.

Do not create a new design system.

Must work at:

- Desktop: 1440x900
- Mobile: 390x844

No horizontal overflow.

# ACCEPTANCE CRITERIA

AC-01
`/app/testlogic` loads successfully.

AC-02
`auto-agent-test-button` is visible.

AC-03
Clicking the button opens `auto-agent-test-modal`.

AC-04
The modal contains exactly:

AUTONOMOUS DEVELOPMENT WORKS

AC-05
`auto-agent-test-close` is visible.

AC-06
Clicking Close closes the modal.

AC-07
At 390x844 the modal stays inside the viewport.

AC-08
There is no horizontal page overflow.

AC-09
The feature causes no unexpected browser errors.

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

feature/auto-agent-test-YYYY-MM-DD-HHmmss

Commit the completed implementation locally.

DO NOT PUSH.
DO NOT MERGE.
DO NOT DEPLOY.