# AI Credits exhaustion recovery

The shared dialog uses the authenticated `ai-usage-summary` response. A known limit with remaining credits <= 0 opens it. Paid generation clients also recognize HTTP 429 only when the JSON `code` is exactly `ai_generation_limit_exhausted`, refresh the same summary, and open the dialog even when the requested operation costs more than the positive remaining balance. Other 429 responses retain normal error handling.

The current checkout creates new subscriptions and does not safely change an existing subscription. There is no credit-pack entitlement path. Consequently all billing actions are disabled. No checkout, payment, entitlement, schema, or production configuration changes are made by this feature.

Optional server configuration (also supported under `Values:`):

| Setting | Default / requirement |
| --- | --- |
| `AI_CREDIT_PACK_ENABLED` | false |
| `AI_CREDIT_PACK_UNITS` | Required positive number |
| `AI_CREDIT_PACK_STRIPE_PRICE_ID` | Required existing price; never returned to the browser |
| `AI_CREDIT_PACK_DISPLAY_PRICE` | Required display text; no invented default price |

Complete configuration reveals a disabled Add Credits card. Configuration alone cannot enable purchases. Enabling a real action requires signature-verified payments, canonical user resolution, idempotent entitlement persistence, and integration into the existing user-level quota calculation. None of those are invented here.

Upgrade options come from the server summary's canonical plan. Starter sees Plus ($79/month) and Extreme ($129/month); Plus sees Extreme; canonical Pro is displayed as Extreme and has no higher option. Unknown plans receive no upgrade options. Allowances reuse `AI_GENERATION_LIMIT_PLUS` and `AI_GENERATION_LIMIT_PRO`. A future safe subscription-change implementation must reuse existing `StripePrices:PlusMonthly` and `StripePrices:ProMonthly` configuration and preserve subscription state; the new-subscription checkout is deliberately not called.

The Refresh credits action reloads the canonical summary and header, closing the dialog when a refreshed balance is positive. No client action grants credits, changes a plan, or automatically retries generation. Escape, close, and Maybe later dismiss the native dialog. Manual browser QA remains required; automated verification uses unit tests and server-rendered component tests only.
