# Sandbox credit catalog and payment-enablement requirements

The user supplied these sandbox mappings. They are saved in `AICreditsSandboxCatalog`; public Payment Link pages could not be verified by the web tool. The server must retrieve and validate their Stripe Price IDs, currency, amounts, and recurrence before payments are enabled.

| Offer | Charge | AI Credits | Payment Link |
| --- | --- | --- | --- |
| Credits Starter | $20 once | +20 | https://buy.stripe.com/test_eVqdR98eFeXbgS1fo25Rm08 |
| Credits Pro | $40 once | +40 | https://buy.stripe.com/test_cNidR9fH7cP3gS14Jo5Rm07 |
| Credits Plus | $60 once | +60 | https://buy.stripe.com/test_28E5kDdyZg1f6dnb7M5Rm06 |
| Plus | $79/month | 80 per billing period | https://buy.stripe.com/test_6oU7sL3YpbKZbxH5Ns5Rm0a |
| Extreme (canonical `extreme`) | $129/month | 130 per billing period | https://buy.stripe.com/test_00w3cv8eF9CR59j1xc5Rm09 |

Pack names identify products; all three packs are shown to every paid plan in sandbox. Buying a pack would increase available credits, not used credits or the customer's subscription tier. Monthly plans define the included allowance rather than adding another full allowance whenever an upgrade occurs.

## Preview configuration

Set `AI_CREDITS_SANDBOX_CATALOG_ENABLED=true` in the local server configuration with an existing `StripeSecretKey` beginning `sk_test_` or `rk_test_`. The application does not write these settings. A missing key, live key, or disabled flag excludes the sandbox catalog. Payment links stay server-side and no checkout can be opened from this preview.

Canonical default monthly allowances are Pro=50, Plus=80 and Extreme=130. Existing quota configuration still wins; set `AI_GENERATION_LIMIT_PRO` / `AI_GENERATION_LIMIT_PLUS` / `AI_GENERATION_LIMIT_EXTREME` to 50 / 80 / 130 in the sandbox if old overrides exist. No production environment values have been changed.

The modal remains viewport-centered, scrollable on small screens, and uses a 240ms ease-in-out fade/slide/scale on each opening. Reduced-motion preferences disable the animation.

## Purchases implemented

The authenticated purchase flow and durable credit ledger now exist. See [AI Credit top-ups](ai-credit-purchases.md) for configuration, environment isolation, consumption, refunds, and deployment requirements. The catalog-only preview remains disabled for payments until that configuration is complete.

## Subscription upgrades — not implemented

The supplied subscription Payment Links create new subscriptions; they are not safe replacements for a subscription-update flow. Keep upgrade actions disabled until an authenticated endpoint resolves the current Stripe customer/subscription, verifies ownership, selects an approved higher-tier price, and updates the existing subscription item. Preserve the current billing period and used credits. Handle failed payment without granting the higher allowance or creating a second subscription. Confirm immediate prorated upgrades versus next-renewal changes before enabling the action, and synchronize only server-verified subscription/payment state back to the canonical summary.

Stripe Price IDs must be looked up in the sandbox; the public `buy.stripe.com/test_...` URL is not a `price_...` ID. No secrets should be pasted into chat or committed.
