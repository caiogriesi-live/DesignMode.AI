# GA4 image generation funnel readiness

## Configuration and existing integrations

- Browser stream: `G-22R24LF0HN` in `DesignMode.AI.Client/wwwroot/analytics-config.json` (`enabled: true`). `wwwroot/js/designmode-analytics.js` fetches this public config and initializes one GA4 tag after Blazor's scripts are registered in `wwwroot/index.html`; do not add another gtag/GTM install.
- Local setup: the checked-in public config already selects the stream. Set `debug: true` only in a local/test deployment for browser DebugView checks. The checked-in default is `false`, so production static deployment stays out of debug mode unless explicitly overridden. No browser secret is required.
- Server purchase setup: verified purchases are sent by `PurchaseConversionDispatcher` through GA4 Measurement Protocol only when `GA4_SERVER_ENABLED=true`, `GA4_MEASUREMENT_ID` matches the browser stream, `GA4_API_SECRET` is present, and the immutable checkout measurement snapshot includes analytics consent and a valid Google client ID. Put the API secret only in local ignored settings or protected production Function App settings. Do not commit it.
- Consent: browser events use the existing `designModeAnalytics` consent gate and `/privacy.html` analytics control. A saved analytics withdrawal prevents tag initialization/collection. The existing server purchase event independently checks checkout-time analytics consent. Do not bypass either control.
- Blocked analytics: JS interop and event dispatch fail closed; image generation and checkout continue. There are no analytics retries. Browser image events use a strict parameter allowlist and never send prompts, image URLs, email, exception text, or user/project IDs.
- Manual GA4 setup: verify that the browser and server target the intended web stream; use a test stream for purchase QA. Mark `purchase` as the GA4 key event and register low-cardinality custom dimensions for `provider`, `model`, and `error_type` if useful. Keep enhanced measurement automatic history/page and media events disabled where they would duplicate the application's strict wrapper. Browser `purchase` is intentionally disallowed; the verified server dispatcher is its source.

## Event semantics

| Event | Source and meaning |
|---|---|
| `image_generation_started` | One accepted request in the main Runware image regeneration handler, after duplicate-click and generation guard checks. |
| `image_generation_success` | The saved/refreshed image has loaded in the browser. |
| `image_generation_failed` | That logical request returned no image, raised a final request error, or its generated image failed to load. The current Runware HTTP handler makes one provider call; it has no automatic retry loop. |
| `begin_checkout` | Existing funnel event, sent only after Stripe checkout session creation succeeds and before redirect; carries the returned value/currency and safe plan item. |
| `purchase` | Existing server event from verified paid purchase records; the canonical transaction ledger and dispatcher prevent duplicate purchase creation/delivery. Redirects and failed/canceled payments do not produce it. |

Image events share an opaque per-request UUID and include Runware provider/model and bounded duration. Intermediate retry parameters are not emitted; the current tracked Runware route does not retry automatically. This is browser funnel measurement, not an operational API success-rate ledger. The page's existing creative-directions generation events remain distinct from this per-image event pair.

## Manual QA

1. Start the local Functions API at `http://localhost:7032/` and the Blazor client. In a local-only `analytics-config.json`, set `debug: true`; confirm the config URL returns the intended test stream ID.
2. Open the site with an analytics-consented browser profile and GA4 DebugView open (or use Google Analytics Debugger). For a test environment only, verify browser requests show `debug_mode: true`. Do not change production settings or make a live charge for QA.
3. Sign in, open the project's image generation page, and click Generate Image once. Confirm one `image_generation_started` with a UUID, `provider=runware`, and `model=runware:101@1`; its payload must not contain a prompt, image URL, user ID, or email.
4. Wait for the image to finish loading and appear in the list. Confirm one `image_generation_success` with the same UUID, and no failure event.
5. In a local/test browser, use DevTools Request Blocking to block the `CallRunwareAPI` endpoint, then initiate one generation. Confirm a start and one final `image_generation_failed` with `error_type=request_error` or `empty_result`. Remove the block afterward. A returned URL whose image cannot load should produce `error_type=image_load`.
6. Confirm duplicate clicks while generation is active are ignored: there is only one start for the accepted request. The current endpoint performs one Runware attempt, so no intermediate retry events exist; if a retry policy is later added, place retries below the logical handler and emit only one terminal result.
7. On the test checkout flow, select a plan and continue. Confirm `begin_checkout` appears only after a successful checkout session response, with its actual value, currency, and safe plan item. A failed session creation must not emit it.
8. For `purchase`, use a Stripe test-mode payment and a GA4 test stream only. Confirm the Stripe webhook records a verified paid purchase and the server dispatcher sends one GA4 purchase with the ledger transaction ID, actual amount/currency, item, and consented client ID. Failed/canceled test sessions produce none. Check canonical purchase/measurement status for duplicate webhook delivery; do not use a production charge.
9. Turn analytics off at `/privacy.html` and reload. Confirm no browser GA4 tag/event collection starts. Turn it back on when finished.

## GA4 Funnel Exploration

Create an Exploration > Funnel exploration with these ordered steps and event-name conditions: `image_generation_started`, `image_generation_success`, `begin_checkout`, `purchase`. Choose an open or closed funnel to match the analysis, use the same reporting identity/date range when comparing periods, and segment by source/medium or campaign. Use `generation_id` only to inspect request event pairing; user-based funnel conversion is not an exact request success rate. Purchases arrive from the server Measurement Protocol and should be validated separately in Realtime/reports because HTTP acceptance alone does not prove ingestion.
