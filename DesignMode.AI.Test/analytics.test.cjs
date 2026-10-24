const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const source = fs.readFileSync(require('node:path').join(__dirname, '../DesignMode.AI.Client/wwwroot/js/designmode-analytics.js'), 'utf8');
const pure = require('../DesignMode.AI.Client/wwwroot/js/designmode-analytics.js');
test('allowlist rejects internal IDs, PII, arbitrary values and nonfinite amounts', () => {
    assert.deepEqual(pure.allowParameters({ UserId: 'secret', email: 'secret', plan: 'customer-name', value: Infinity, result_count: 3, method: 'google' }), { result_count: 3, method: 'google' });
});
test('URL sanitizer removes all queries/fragments and unknown dynamic paths', () => {
    assert.equal(pure.sanitizeUrl('https://example.com/init/Success?session_id=secret&email=secret#token'), '/init/success');
    assert.equal(pure.sanitizeUrl('/invites/private-id?jwt=secret'), '/other');
    assert.equal(pure.sanitizeUrl('/designerintro?name=private&industry=private'), '/designerintro');
});
test('event taxonomy excludes browser purchase and arbitrary event names', () => {
    assert.equal(pure.events.has('purchase'), false);
    for (const name of ['begin_checkout','login','sign_up','generation_completed','image_generation_started','image_generation_success','image_generation_failed']) assert.equal(pure.events.has(name), true);
});
test('image generation parameters accept safe identifiers and categories only', () => {
    const id = '9d0ecabc-a593-487b-9ec3-6c3760a08964';
    assert.deepEqual(pure.allowParameters({
        generation_id: id, provider: 'runware', model: 'runware:101@1', attempt_count: 2, duration_ms: 900,
        error_type: 'provider_error', prompt: 'private prompt', email: 'private@example.com', image_url: 'https://private.invalid/image'
    }), { generation_id: id, provider: 'runware', model: 'runware:101@1', attempt_count: 2, duration_ms: 900, error_type: 'provider_error' });
    assert.deepEqual(pure.allowParameters({ generation_id: 'not-a-guid', provider: 'other', error_type: 'raw provider response' }), {});
});
test('begin_checkout supports actual safe GA4 item data', () => {
    assert.deepEqual(pure.allowParameters({ value: 49, currency: 'USD', plan: 'plus-monthly', items: [
        { item_id: 'plus-monthly', item_name: 'customer email', quantity: 1, price: 49 },
        { item_id: 'arbitrary', quantity: 1, price: 49 }
    ] }), { value: 49, currency: 'USD', plan: 'plus-monthly', items: [
        { item_id: 'plus-monthly', item_name: 'plus-monthly', quantity: 1, price: 49 }
    ] });
});
test('image load probe resolves only after an image loads', async () => {
    const prior = global.Image;
    try {
        global.Image = class {
            set src(value) {
                this.url = value;
                setImmediate(() => this.onload());
            }
        };
        assert.equal(await pure.imageLoaded('https://cdn.example/image.jpg'), true);
        global.Image = class {
            set src(value) {
                this.url = value;
                setImmediate(() => this.onerror());
            }
        };
        assert.equal(await pure.imageLoaded('https://cdn.example/missing.jpg'), false);
    } finally {
        if (prior === undefined) delete global.Image;
        else global.Image = prior;
    }
});
test('checked-in client config enables the requested GA4 stream without secrets', () => {
    const config = JSON.parse(fs.readFileSync(require('node:path').join(__dirname, '../DesignMode.AI.Client/wwwroot/analytics-config.json'), 'utf8'));
    assert.equal(config.enabled, true);
    assert.equal(config.measurementId, 'G-22R24LF0HN');
    assert.equal(config.landingVariant, 'control');
    assert.equal(typeof config.debug, 'boolean');
    assert.equal(Object.keys(config).some(key => /secret|api.?key/i.test(key)), false);
});
function browser(savedConsent = null, href = 'https://example.com/social-landing.html?email=secret', framed = false) {
    const scripts = [], listeners = {};
    const stored = new Map(savedConsent == null ? [] : [['dm_measurement_consent', JSON.stringify(savedConsent)]]);
    const window = { location: { origin: 'https://example.com', href } };
    window.self = window;
    window.top = framed ? {} : window;
    const ctx = { window, URL, console, setTimeout, clearTimeout, localStorage: { getItem: key => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value) },
        document: { createElement: () => ({}), head: { appendChild: x => scripts.push(x) }, addEventListener: (name, fn) => listeners[name] = fn },
        fetch: async () => ({ ok: true, json: async () => ({ enabled: true, measurementId: 'G-TEST123', debug: false }) }) };
    vm.runInNewContext(source, ctx);
    return { window, scripts, listeners, stored };
}
test('analytics and Google advertising default allowed; page views deduplicate and queries stay private', async () => {
    const { window, scripts, stored } = browser();
    // Static HTML requests its page before async config/tag initialization completes.
    window.designModeAnalytics.page(window.location.href);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(scripts.length, 1); scripts[0].onload();
    assert.deepEqual(JSON.parse(stored.get('dm_measurement_consent')), { analytics: true, advertising: true });
    const consentDefault = window.dataLayer.find(x => x[0] === 'consent' && x[1] === 'default');
    assert.equal(consentDefault[2].analytics_storage, 'granted');
    assert.equal(consentDefault[2].ad_storage, 'granted');
    assert.equal(consentDefault[2].ad_user_data, 'granted');
    assert.equal(consentDefault[2].ad_personalization, 'denied');
    window.designModeAnalytics.page(window.location.href);
    window.designModeAnalytics.page('/social-landing.html?different=secret');
    const sent = () => window.dataLayer.filter(x => x[0] === 'event');
    assert.equal(sent().filter(x => x[1] === 'page_view').length, 1);
    assert.equal(sent().filter(x => x[1] === 'landing_view').length, 1);
    assert.equal(sent().filter(x => x[1] === 'begin_checkout').length, 0);
    window.designModeAnalytics.track('begin_checkout', { plan: 'plus', value: 49, currency: 'USD' });
    assert.equal(sent().filter(x => x[1] === 'begin_checkout').length, 1);
    assert.equal(JSON.stringify(window.dataLayer).includes('secret'), false);
    window.designModeAnalytics.setConsent({ analytics: false });
    assert.equal(window.designModeAnalytics.track('login'), false);
});
test('static Social HTML explicitly uses canonical page tracking and never enables automatic GA page views', () => {
    const html = fs.readFileSync(require('node:path').join(__dirname, '../DesignMode.AI.Client/wwwroot/social-landing.html'), 'utf8');
    assert.equal((html.match(/designModeAnalytics\.page\(window\.location\.href\)/g) || []).length, 1);
    assert.match(source, /send_page_view:\s*false/);
});
test('full Social to new Blazor document and SPA navigation preserves known routes without stale or other pages', async () => {
    const landing = browser(null, 'https://example.com/social-landing.html?gclid=secret#private');
    landing.window.designModeAnalytics.page(landing.window.location.href);
    await new Promise(resolve => setImmediate(resolve)); landing.scripts[0].onload();
    const landingEvents = landing.window.dataLayer.filter(x => x[0] === 'event');
    assert.deepEqual(Array.from(landingEvents, x => x[1]), ['page_view', 'landing_view']);
    assert.equal(JSON.stringify(landingEvents).includes('secret'), false);

    // The preload is a distinct iframe document and must never create /other or load GA.
    const warmup = browser(null, 'https://example.com/warmup', true);
    warmup.window.designModeAnalytics.page(warmup.window.location.href);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(warmup.scripts.length, 0);
    assert.equal(warmup.window.dataLayer, undefined);

    // Full navigation creates a fresh wrapper state. Blazor and an early action arrive before GA readiness.
    const app = browser(null, 'https://example.com/designerintro?funnelId=secret');
    app.window.designModeAnalytics.page(app.window.location.href);
    assert.equal(app.window.designModeAnalytics.track('account_gate_view', { funnel_stage: 'intro' }), true);
    await new Promise(resolve => setImmediate(resolve)); app.scripts[0].onload();
    app.window.designModeAnalytics.page(app.window.location.href);
    app.window.location.href = 'https://example.com/init/mainconcept?projectId=secret';
    app.window.designModeAnalytics.page(app.window.location.href);
    app.window.designModeAnalytics.page('/init/mainconcept#duplicate');
    app.window.designModeAnalytics.track('creative_directions_viewed', { result_count: 3 });
    app.window.location.href = 'https://example.com/init/funnelend?token=secret';
    app.window.designModeAnalytics.page(app.window.location.href);

    const events = app.window.dataLayer.filter(x => x[0] === 'event');
    const pageLocations = events.filter(x => x[1] === 'page_view').map(x => x[2].page_location);
    assert.deepEqual(Array.from(pageLocations), [
        'https://example.com/designerintro',
        'https://example.com/init/mainconcept',
        'https://example.com/init/funnelend'
    ]);
    for (const name of ['designer_intro_view', 'account_gate_view', 'creative_directions_viewed', 'funnel_end_viewed'])
        assert.equal(events.filter(x => x[1] === name).length, 1, name);
    assert.equal(events.some(x => x[2].page_location.endsWith('/other')), false);
    assert.equal(JSON.stringify(events).includes('secret'), false);
});
test('a saved analytics withdrawal is honored and prevents automatic initialization', async () => {
    const { window, scripts } = browser({ analytics: false, advertising: false });
    window.designModeAnalytics.page(window.location.href);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(scripts.length, 0);
    assert.equal(window.designModeAnalytics.track('login'), false);
    assert.equal(window.dataLayer, undefined);
    const context = await window.designModeAnalytics.checkoutContext();
    assert.equal(context.analyticsConsent, false);
    assert.equal(context.advertisingConsent, true);
    assert.equal(context.googleAdvertisingConsent, true);
});
test('privacy page exposes withdrawal through the centralized consent API', () => {
    const privacy = fs.readFileSync(require('node:path').join(__dirname, '../DesignMode.AI.Client/wwwroot/privacy.html'), 'utf8');
    assert.match(privacy, /designModeAnalytics\.setConsent\(\{ \.\.\.window\.designModeAnalytics\.getConsent\(\), analytics: enabled \}\)/);
    assert.doesNotMatch(privacy, /analytics: enabled, advertising: false/, 'Analytics controls must preserve the separate advertising choice');
    assert.equal((privacy.match(/designmode-analytics\.js/g) || []).length, 1);
    assert.equal(/\bgtag\s*\(/.test(privacy), false);
});

test('internal and Google measurement are always on, including old saved denials', async () => {
    for (const saved of [null, { analytics: true, advertising: true }, { analytics: false, advertising: false }]) {
        const b = browser(saved);
        await new Promise(resolve => setImmediate(resolve));
        const context = await b.window.designModeAnalytics.checkoutContext();
        assert.equal(context.advertisingConsent, true);
        assert.equal(context.advertisingMeasurementPolicyVersion, 'internal-always-on-2026-08-31');
        assert.equal(context.googleAdvertisingConsent, true);
        for (const call of b.window.dataLayer || []) if (call[0] === 'consent') {
            assert.equal(call[2].ad_storage, 'granted');
            assert.equal(call[2].ad_user_data, 'granted');
            assert.equal(call[2].ad_personalization, 'denied');
        }
    }
});

test('attempted Google withdrawal is ignored while analytics withdrawal remains independent', async () => {
    const b = browser(); await new Promise(resolve => setImmediate(resolve));
    b.window.designModeAnalytics.setConsent({ analytics: true, advertising: true });
    let update = b.window.dataLayer.findLast(x => x[0] === 'consent');
    assert.equal(update[2].ad_storage, 'granted'); assert.equal(update[2].ad_user_data, 'granted');
    assert.equal(update[2].ad_personalization, 'denied');
    b.window.designModeAnalytics.setConsent({ analytics: false, advertising: false });
    update = b.window.dataLayer.findLast(x => x[0] === 'consent');
    assert.equal(update[2].analytics_storage, 'denied'); assert.equal(update[2].ad_user_data, 'granted');
    assert.equal((await b.window.designModeAnalytics.checkoutContext()).advertisingConsent, true);
});
test('blocked analytics script is nonblocking and does not dispatch', async () => {
    const { window, scripts } = browser(); await new Promise(resolve => setImmediate(resolve));
    scripts[0].onerror();
    assert.equal(window.designModeAnalytics.track('funnel_start'), false);
});
test('pre-navigation delivery uses beacon transport and resolves from the GA callback', async () => {
    const { window, scripts } = browser(); await new Promise(resolve => setImmediate(resolve)); scripts[0].onload();
    const delivery = window.designModeAnalytics.trackAndFlush('begin_checkout', { plan: 'pro', value: 59, currency: 'USD' });
    const queued = window.dataLayer.findLast(x => x[0] === 'event' && x[1] === 'begin_checkout');
    assert.equal(queued[2].transport_type, 'beacon');
    assert.equal(queued[2].event_timeout, 800);
    assert.equal(typeof queued[2].event_callback, 'function');
    assert.equal(JSON.stringify(queued).includes('secret'), false);
    queued[2].event_callback();
    assert.equal(await delivery, true);
    assert.equal(window.dataLayer.filter(x => x[0] === 'event' && x[1] === 'begin_checkout').length, 1);
});
test('pre-navigation delivery times out safely and unavailable analytics resolves immediately', async () => {
    const active = browser(); await new Promise(resolve => setImmediate(resolve)); active.scripts[0].onload();
    assert.equal(await active.window.designModeAnalytics.trackAndFlush('unlock_clicked', { plan: 'pro' }, 1), false);
    const withdrawn = browser({ analytics: false, advertising: false }); await new Promise(resolve => setImmediate(resolve));
    assert.equal(await withdrawn.window.designModeAnalytics.trackAndFlush('unlock_clicked', { plan: 'pro' }), false);
});
test('checkout instrumentation follows successful URL validation, never page load', () => {
    const razor = fs.readFileSync(require('node:path').join(__dirname, '../DesignMode.AI.Client/Pages/Init/FunnelEnd.razor'), 'utf8');
    const method = razor.slice(razor.indexOf('private async Task StartCheckout'), razor.indexOf('public class GetUserIdRequest'));
    assert.ok(method.indexOf('TrackAndFlushAsync("unlock_clicked"') > method.indexOf('isProcessing = true'));
    assert.ok(method.indexOf('TrackAndFlushAsync("begin_checkout"') > method.indexOf('string.IsNullOrWhiteSpace(checkoutResult.Url)'));
    assert.ok(method.indexOf('TrackAndFlushAsync("begin_checkout"') < method.indexOf('NavigationManager.NavigateTo('));
    assert.equal((method.match(/TrackAndFlushAsync\("unlock_clicked"/g) || []).length, 1);
    assert.equal((method.match(/TrackAndFlushAsync\("begin_checkout"/g) || []).length, 1);
    assert.ok(method.indexOf('if (isProcessing)') < method.indexOf('TrackAndFlushAsync("unlock_clicked"'));
});
