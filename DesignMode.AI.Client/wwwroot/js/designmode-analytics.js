(function (root) {
    "use strict";
    const routes = new Set(["/social-landing.html", "/designerintro", "/init/mainconcept", "/init/funnelend", "/init/success", "/init/gen"]);
    const events = new Set(["page_view", "landing_view", "social_form_start", "funnel_start", "company_profile_saved", "designer_intro_view", "account_gate_view", "sign_up", "login", "generation_started", "generation_completed", "generation_failed", "image_generation_started", "image_generation_success", "image_generation_failed", "creative_directions_viewed", "main_concept_selected", "funnel_end_viewed", "video_started", "video_completed", "unlock_clicked", "begin_checkout", "paid_workspace_viewed"]);
    const enums = {
        entry_type: ["paid", "direct", "referral", "returning", "invitation", "email"], landing_page_id: ["social"], landing_variant: ["control", "a", "b"],
        funnel_stage: ["landing", "intro", "directions", "unlock", "checkout", "workspace"], auth_state: ["anonymous", "authenticated"],
        auth_method: ["email", "google"], method: ["email", "google"], generation_category: ["social"], deliverable_type: ["creative_directions", "image", "video"],
        duration_bucket: ["under_10s", "10_30s", "over_30s"], plan: ["starter", "plus", "pro", "starter-one-time", "plus-monthly", "pro-monthly", "pronew-monthly", "plus-yearly", "pro-yearly"], billing_interval: ["monthly", "yearly", "one_time"]
    };
    function sanitizeUrl(value) {
        try { const path = new URL(value, root.location?.origin || "https://example.invalid").pathname.toLowerCase().replace(/\/$/, ""); return routes.has(path) ? path : "/other"; }
        catch { return "/other"; }
    }
    function allowParameters(parameters) {
        const safe = {};
        for (const [key, value] of Object.entries(parameters || {})) {
            if (enums[key]?.includes(value)) safe[key] = value;
            else if (key === "result_count" && Number.isInteger(value) && value >= 0 && value <= 100) safe[key] = value;
            else if (key === "value" && typeof value === "number" && Number.isFinite(value) && value >= 0) safe[key] = value;
            else if (key === "currency" && /^(USD|EUR|GBP|CAD|AUD|JPY|BRL)$/.test(value)) safe[key] = value;
            else if (key === "generation_id" && typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) safe[key] = value;
            else if (key === "provider" && value === "runware") safe[key] = value;
            else if (key === "model" && value === "runware:101@1") safe[key] = value;
            else if (key === "duration_ms" && Number.isInteger(value) && value >= 0 && value <= 3600000) safe[key] = value;
            else if (key === "attempt_count" && Number.isInteger(value) && value >= 1 && value <= 20) safe[key] = value;
            else if (key === "error_type" && ["provider_error", "empty_result", "image_load", "request_error"].includes(value)) safe[key] = value;
            else if (key === "items" && Array.isArray(value)) {
                const items = value.filter(item => item && ["starter", "plus", "pro", "subscription", "starter-one-time", "plus-monthly", "pro-monthly", "pronew-monthly", "plus-yearly", "pro-yearly"].includes(item.item_id)
                    && Number.isInteger(item.quantity) && item.quantity > 0 && typeof item.price === "number" && Number.isFinite(item.price) && item.price >= 0)
                    .map(item => ({ item_id: item.item_id, item_name: item.item_id, quantity: item.quantity, price: item.price }));
                if (items.length) safe[key] = items;
            }
        }
        return safe;
    }
    let config = {}, initialized = false, loaded = false, loadFailed = false, lastPage = null, pendingPage = null;
    const measurementDocument = root.self === root.top;
    const pendingEvents = [];
    // Prospective product policy: Google advertising measurement is always enabled; personalization remains denied.
    let consent = { analytics: true, advertising: true };
    const once = new Set();
    const consentListeners = new Set();
    function available() { return measurementDocument && config.enabled === true && /^G-[A-Z0-9]+$/.test(config.measurementId || "") && consent.analytics && loaded; }
    function dispatchNow(name, parameters) {
        try {
            if (!events.has(name) || !available()) return false; // purchase is server only.
            root.gtag("event", name, { ...allowParameters(parameters), send_to: config.measurementId,
                page_location: root.location.origin + sanitizeUrl(root.location.href), page_referrer: "", page_title: sanitizeUrl(root.location.href),
                ...(config.debug ? { debug_mode: true } : {}) });
            if (config.debug) console.debug("[ANALYTICS] event", name);
            return true;
        } catch { return false; }
    }
    function dispatch(name, parameters) {
        if (available()) return dispatchNow(name, parameters);
        if (measurementDocument && consent.analytics && !loaded && !loadFailed && events.has(name) && pendingEvents.length < 20) {
            pendingEvents.push([name, allowParameters(parameters)]);
            return true;
        }
        return false;
    }
    function dispatchAndFlush(name, parameters, timeoutMs) {
        return new Promise(resolve => {
            try {
                if (!events.has(name) || !available()) { resolve(false); return; }
                const boundedTimeout = Number.isInteger(timeoutMs) && timeoutMs > 0 && timeoutMs <= 2000 ? timeoutMs : 800;
                let settled = false;
                const finish = delivered => {
                    if (settled) return;
                    settled = true;
                    clearTimeout(timer);
                    resolve(delivered);
                };
                const timer = setTimeout(() => finish(false), boundedTimeout);
                root.gtag("event", name, { ...allowParameters(parameters), send_to: config.measurementId,
                    page_location: root.location.origin + sanitizeUrl(root.location.href), page_referrer: "", page_title: sanitizeUrl(root.location.href),
                    transport_type: "beacon", event_timeout: boundedTimeout, event_callback: () => finish(true),
                    ...(config.debug ? { debug_mode: true } : {}) });
                if (config.debug) console.debug("[ANALYTICS] flush event", name);
            } catch { resolve(false); }
        });
    }
    function trackOnce(name, parameters) { if (once.has(name)) return false; const sent = dispatch(name, parameters); if (sent) once.add(name); return sent; }
    function page(value) {
        const safe = sanitizeUrl(value);
        if (!measurementDocument) return;
        if (config.debug) console.debug("[ANALYTICS PAGE] requested:", safe, "gaReady:", available(), "lastPage:", lastPage, "queued:", !available());
        if (safe === lastPage) return;
        if (!available()) { pendingPage = safe; return; }
        if (!dispatchNow("page_view")) { pendingPage = safe; return; }
        pendingPage = null;
        lastPage = safe;
        const event = { "/social-landing.html": "landing_view", "/designerintro": "designer_intro_view", "/init/funnelend": "funnel_end_viewed" }[safe];
        if (event) dispatchNow(event, safe === "/social-landing.html" ? { landing_page_id: "social", landing_variant: config.landingVariant } : {});
        if (config.debug) console.debug("[ANALYTICS PAGE] sanitized:", safe, "dispatched: true");
    }
    function configureTag() {
        if (!measurementDocument || !consent.analytics || !config.enabled || initialized || !/^G-[A-Z0-9]+$/.test(config.measurementId || "")) return;
        initialized = true;
        root.dataLayer = root.dataLayer || [];
        root.gtag = function () { root.dataLayer.push(arguments); };
        root.gtag("consent", "default", { analytics_storage: "granted", ad_storage: "granted", ad_user_data: "granted", ad_personalization: "denied" });
        root.gtag("consent", "update", { analytics_storage: "granted", ad_storage: consent.advertising ? "granted" : "denied", ad_user_data: consent.advertising ? "granted" : "denied", ad_personalization: "denied" });
        root.gtag("js", new Date());
        root.gtag("config", config.measurementId, { send_page_view: false, allow_google_signals: false, allow_ad_personalization_signals: false,
            page_location: root.location.origin + sanitizeUrl(root.location.href), page_referrer: "", page_title: sanitizeUrl(root.location.href) });
        const script = document.createElement("script"); script.async = true;
        script.src = "https://www.googletagmanager.com/gtag/js?id=" + encodeURIComponent(config.measurementId);
        script.onload = () => {
            loaded = true;
            page(pendingPage || root.location.href);
            const queued = pendingEvents.splice(0);
            for (const [name, parameters] of queued) dispatchNow(name, parameters);
        };
        script.onerror = () => { loaded = false; loadFailed = true; pendingEvents.length = 0; };
        document.head.appendChild(script);
    }
    function setConsent(value) {
        consent = { analytics: value?.analytics === true, advertising: true };
        try { localStorage.setItem("dm_measurement_consent", JSON.stringify(consent)); } catch { }
        if (initialized) root.gtag("consent", "update", { analytics_storage: consent.analytics ? "granted" : "denied", ad_storage: consent.advertising ? "granted" : "denied", ad_user_data: consent.advertising ? "granted" : "denied", ad_personalization: "denied" });
        configureTag();
        if (consent.analytics) page(root.location.href);
        for (const listener of consentListeners) { try { listener({ ...consent }); } catch { } }
    }
    async function checkoutContext() {
        const result = { analyticsConsent: consent.analytics, advertisingConsent: true,
            advertisingMeasurementPolicyVersion: "internal-always-on-2026-08-31", googleAdvertisingConsent: consent.advertising };
        if (!available()) return result;
        const get = key => new Promise(resolve => { const timer = setTimeout(() => resolve(null), 300); root.gtag("get", config.measurementId, key, value => { clearTimeout(timer); resolve(value); }); });
        try { const values = await Promise.all([get("client_id"), get("session_id")]); result.gaClientId = values[0]; result.gaSessionId = values[1] == null ? null : String(values[1]); } catch { }
        return result;
    }
    function imageLoaded(url) {
        return new Promise(resolve => {
            let settled = false;
            const finish = loaded => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                resolve(loaded);
            };
            const timer = setTimeout(() => finish(false), 15000);
            try {
                const image = new Image();
                image.onload = () => finish(true);
                image.onerror = () => finish(false);
                image.src = url;
                if (image.complete && image.naturalWidth > 0) finish(true);
            } catch { finish(false); }
        });
    }
    root.designModeAnalytics = { track: dispatch, trackAndFlush: dispatchAndFlush, once: trackOnce, page, setConsent, checkoutContext, available, sanitizeUrl, allowParameters, imageLoaded,
        getConsent: () => ({ ...consent }), subscribeConsent: listener => { consentListeners.add(listener); return () => consentListeners.delete(listener); } };
    if (typeof module !== "undefined") module.exports = { sanitizeUrl, allowParameters, events, imageLoaded };
    if (typeof document === "undefined") return;
    try {
        const saved = JSON.parse(localStorage.getItem("dm_measurement_consent") || "null");
        if (saved && typeof saved.analytics === "boolean" && typeof saved.advertising === "boolean")
            consent = { analytics: saved.analytics, advertising: true };
        else localStorage.setItem("dm_measurement_consent", JSON.stringify(consent));
    } catch { }
    if (measurementDocument) fetch("/analytics-config.json", { cache: "no-store" }).then(r => r.ok ? r.json() : {}).then(c => { config = c; configureTag(); }).catch(() => {});
    const played = new WeakSet(), completed = new WeakSet();
    document.addEventListener("play", event => { if (sanitizeUrl(root.location.href) === "/init/funnelend" && event.target.tagName === "VIDEO" && !played.has(event.target)) { played.add(event.target); dispatch("video_started", { deliverable_type: "video" }); } }, true);
    document.addEventListener("ended", event => { if (sanitizeUrl(root.location.href) === "/init/funnelend" && event.target.tagName === "VIDEO" && !completed.has(event.target)) { completed.add(event.target); dispatch("video_completed", { deliverable_type: "video" }); } }, true);
})(typeof window !== "undefined" ? window : globalThis);
