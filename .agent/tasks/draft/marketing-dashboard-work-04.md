# Implement Manual Business-Expense Ledger

## Objective

Create a separate, company-operator-authorized manual business-expense ledger that supports entering, editing, and reading expense records using the approved fields and categories. Persist ledger records separately from canonical purchase records and make them available to downstream reporting by date and currency.

## Requirements

- Provide an in-application experience for authorized company operators to create expense records, edit existing records, and read/list records.
- Each expense record must include the approved fields: date, amount, currency, category, and description/vendor.
- Restrict category values to: software, AI/API, hosting/infrastructure, contractors, payroll, advertising/marketing, professional services, and other.
- Persist expenses in a distinct ledger separate from canonical purchase records. Expense operations must not write to, alter, or represent themselves as canonical purchases.
- Make ledger records retrievable by date and currency for downstream financial reporting. Preserve the entered currency and amount; do not convert currencies.
- Enforce the explicit DesignMode administrator/company-operator authorization boundary server-side for every expense create, edit, and read operation. Hiding the experience in the client is not authorization.
- Preserve existing authentication and customer authorization mechanisms, and the current user-scoped project dashboard and project-list behavior.
- Keep the capability limited to manual expense entry, editing, and reading. Do not add deletion, bookkeeping automation, accounting statements, or financial aggregation/reporting measures.

## Acceptance Criteria

AC-01: An authorized company operator can create an expense record containing date, amount, currency, one approved category, and description/vendor, and the saved record can subsequently be read from the ledger.

AC-02: The category input accepts exactly these values: software, AI/API, hosting/infrastructure, contractors, payroll, advertising/marketing, professional services, and other. Values outside this set are rejected and cannot be persisted as expense categories.

AC-03: An authorized company operator can edit an existing expense record's approved fields and read back the updated values; editing one expense does not alter another expense or any canonical purchase record.

AC-04: An authorized company operator can read expense records filtered by date and currency. Results preserve each record's entered amount and currency, and records in other currencies are not converted or included as if they shared the requested currency.

AC-05: Unauthenticated requests and authenticated ordinary customer requests to create, edit, or read company expense records are denied server-side and return no company expense data. The authorization check applies to every expense operation, including direct API requests.

AC-06: Expense persistence is separate from canonical `Purchases`; creating or editing an expense does not create, update, or delete a purchase or invoke Stripe/payment/credit transaction behavior.

AC-07: The ledger entry/edit/read experience is usable at desktop viewport 1440x900 and mobile WebKit viewport 390x844, with no unintended horizontal overflow. Automated UI coverage uses stable `data-testid` selectors for the expense form, save action, and ledger records where those selectors materially support reliable interaction.

AC-08: Existing `/init/dashboard` project-list behavior and its user-scoped project data remain available and unchanged.

## Protected Systems

Company authorization and financial records; canonical `Purchases` and all Stripe checkout, webhook, payment, refund, subscription, and credit transaction flows; existing authentication and customer authorization; attribution and conversion measurement; AI usage accounting; secrets; production infrastructure and deployment configuration. Do not mutate production transaction records.

## Safety

Do not push.
Do not merge.
Do not deploy.

Commit implementation changes locally on the assigned feature branch.

## Planner Status

STATUS: READY_FOR_APPROVAL
