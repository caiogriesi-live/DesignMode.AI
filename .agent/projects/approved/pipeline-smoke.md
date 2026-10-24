# Pipeline Smoke Test

## Objective

Create a small, isolated Blazor smoke-test page at `/pipeline-smoke` that verifies a local counter interaction and focused browser acceptance coverage across desktop Chromium and mobile WebKit.

## Current State

- The client is a Blazor WebAssembly app with route-based Razor pages under `DesignMode.AI.Client/Pages`.
- `DesignMode.AI.Client/Pages/PipelineValidation.razor` already implements a standalone counter page at `/pipeline-validation` with an initial zero value, semantic increment button, `data-testid` selectors, and local component state. It is a close implementation precedent and must remain unchanged.
- `DesignMode.AI.Client/Pages/Counter.razor` owns `/counter` and is currently an authorization-protected login test; its prior counter markup is commented out. It must remain unchanged.
- `App.razor` uses the shared `MainLayout` as the default route layout. A new page can follow the existing route page convention without changing router or authentication configuration.
- `.agent/qa` contains Playwright-based acceptance infrastructure and browser/network isolation helpers. The existing Playwright config matches planner and generated acceptance specs; focused coverage should follow this infrastructure while explicitly launching Chromium and WebKit at the requested viewport dimensions.
- `.agent/tasks/draft/pipeline-smoke-work-01.md` already records the requested route, behavior, acceptance criteria, and protected systems as a task draft. No implementation is present at `/pipeline-smoke` in the inspected pages.
- No external API, database, credential, or production service is needed for the local counter behavior.

## Desired Outcome

Direct navigation to `/pipeline-smoke` displays the exact heading `Pipeline Smoke Test`, a counter initialized to `0`, and an accessible increment button. Each activation increases the count by one. Stable selectors support focused browser acceptance checks. The page works at Chromium 1440x900 and WebKit 390x844 with no unintended horizontal overflow and no authentication or external service dependency.

## Scope

- Add the new isolated `/pipeline-smoke` route using the client’s existing Razor page architecture.
- Provide local deterministic counter state, the required heading and accessible increment button, and stable distinct `data-testid` selectors.
- Add focused browser acceptance coverage for initial state, interaction, required browser/viewport pairs, and horizontal overflow.
- Keep changes limited to the new smoke-test page and its directly necessary acceptance coverage.

## Out of Scope

- Changes to `/pipeline-validation`, `/counter`, or `/init/dashboard`.
- Changes to WORK-04 or Marketing Performance Dashboard functionality.
- Authentication or authorization changes, API/service integration, persistence, analytics, or additional product functionality.
- Changes to payments, subscriptions, Stripe, AI usage accounting, production databases, secrets, deployment configuration, or production infrastructure.

## Architecture and Existing Systems

Implement the page as a standalone Razor route in `DesignMode.AI.Client/Pages`, using `PipelineValidation.razor` as a structural precedent while keeping its route and file untouched. Keep counter state local to the page and avoid API, service, model, and persistence changes. Use a semantic native button and stable selectors. Add focused browser checks in the existing `.agent/qa` Playwright infrastructure where practical; cover Chromium at 1440x900 and WebKit at 390x844, and measure document horizontal overflow at both sizes. Keep any test configuration changes narrowly scoped to this coverage.

## Milestones

### Milestone 1: Smoke-test page

Deliver the new unauthenticated route with the required visible heading, initial counter, accessible increment interaction, stable selectors, and local-only state.

### Milestone 2: Browser acceptance coverage

Verify direct route access, initial and incremented counter values, and no horizontal overflow in Chromium at 1440x900 and WebKit at 390x844.

## Engineering Work Items

### WORK-01: Add the Pipeline Smoke Test page and focused browser acceptance coverage

Objective:

Add the isolated `/pipeline-smoke` Blazor page and focused browser acceptance checks proving the specified counter interaction at both required browser/viewport combinations, including no horizontal overflow.

Depends On:

NONE

Protected Systems:

`/pipeline-validation`, `/counter`, `/init/dashboard`, WORK-04, Marketing Performance Dashboard, authentication and authorization, Stripe/payments/subscriptions, AI usage accounting and `AIGenerationUsage`, production databases and records, secrets and credentials, deployment configuration, and production infrastructure.

## Dependencies

WORK-01 is the only work item and has no dependencies. It can implement the route and its focused browser acceptance checks within one bounded scope. The existing client and Playwright infrastructure are available; no external integration or production configuration is required.

## Risks

- Shared application layout or global styles could introduce horizontal overflow even when the new page is individually responsive; the acceptance checks should measure the rendered document at both specified viewport sizes.
- The existing Playwright configuration may need a narrowly scoped way to exercise both browser engines; changes should not alter unrelated QA coverage.
- Route-level authorization or shared navigation behavior could prevent direct unauthenticated access; the new page must remain free of authentication requirements without changing shared authentication behavior.

## Protected Systems

Preserve `/pipeline-validation`, `/counter`, and `/init/dashboard` behavior; WORK-04 and Marketing Performance Dashboard functionality; authentication and authorization; Stripe, payments, and subscriptions; AI usage accounting and `AIGenerationUsage`; production databases and records; secrets and credentials; deployment configuration; and production infrastructure. Do not add external APIs, databases, credentials, or production service dependencies. Do not push, merge, or deploy.

## Definition of Done

- `/pipeline-smoke` can be opened directly without authentication and shows the exact `Pipeline Smoke Test` heading and counter value `0` before interaction.
- A semantic, keyboard-operable button with accessible name `Increment count` increases the displayed count by exactly one per activation.
- Stable, distinct `data-testid` selectors identify the page, heading, counter, and button and are used by focused browser acceptance checks.
- Acceptance coverage verifies the page and interaction in Chromium at 1440x900 and WebKit at 390x844, with no unintended horizontal overflow in either viewport.
- No external APIs, databases, credentials, or production services are required, and all listed protected systems remain unchanged.

## Project Status

STATUS: READY_FOR_APPROVAL
