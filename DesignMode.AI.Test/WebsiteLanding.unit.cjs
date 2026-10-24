// DOM-free unit test: execute only the landing's inline application script with small test doubles.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(process.argv[2], 'utf8');
const source = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(x => x[1]).find(x => x.includes('companyForm'));
assert.ok(source);
const elements = new Map();
const storage = new Map();
let capturedVariant, redirect;
function element(id) {
    if (!elements.has(id)) elements.set(id, { value: '', textContent: '', addEventListener(name, fn) { this[name] = fn; }, querySelector() { return null; }, insertBefore() {} });
    return elements.get(id);
}
const location = { hostname: 'localhost', search: '?utm_source=google&utm_campaign=web&gclid=g&gbraid=b&wbraid=w&campaignid=42&custom=keep',
    href: 'https://localhost/website-landing.html', replace(url) { redirect = url; } };
const context = {
    window: { location, crypto: require('node:crypto').webcrypto,
        designModeAnalytics: { page() {}, once() {}, track() {} },
        designModeAttribution: { markFunnelComplete() {}, async capture(base, variant) { capturedVariant = variant; }, async createSocialProject() { return { funnelId: 'funnel', projectId: 'project' }; } } },
    document: { getElementById: element, createElement: () => ({}) },
    localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
    fetch: async (url, request) => {
        assert.ok(url.endsWith('/api/SaveWebsiteCompany'));
        assert.equal(JSON.parse(request.body).Name, 'A & B + Studio');
        return { ok: true, json: async () => ({ companyId: 'saved-company' }) };
    },
    URLSearchParams, console
};
vm.runInNewContext(source, context);
(async () => {
    assert.equal(capturedVariant, 'website-funnel');
    await element('companyForm').submit({ preventDefault() {} });
    assert.equal(redirect, undefined, 'empty input must not navigate');
    element('companyName').value = 'A & B + Studio';
    element('industrySearch').value = 'Web / Design';
    await element('companyForm').submit({ preventDefault() {} });
    const target = new URL(redirect, 'https://example.test');
    assert.equal(target.pathname, '/designerintrowebsite');
    for (const [key, expected] of Object.entries({ name: 'A & B + Studio', industry: 'Web / Design', type: 'website', companyIndustryId: 'saved-company', funnelId: 'funnel', projectId: 'project', landing_variant: 'website-funnel', utm_source: 'google', utm_campaign: 'web', gclid: 'g', gbraid: 'b', wbraid: 'w', campaignid: '42', custom: 'keep' }))
        assert.equal(target.searchParams.get(key), expected, key);
    const pending = JSON.parse(storage.get('unsaved_company_industry'));
    assert.equal(pending.Name, 'A & B + Studio');
    assert.equal(pending.Industry, 'Web / Design');
    assert.equal(pending.Local, storage.get('dm_anonymous_user_id'));
    let capturedBody;
    const attributionScope = {
        window: { crypto: context.window.crypto }, location,
        document: { referrer: 'https://example.test/campaign' },
        localStorage: context.localStorage, URLSearchParams, console,
        fetch: async (url, request) => {
            capturedBody = JSON.parse(request.body);
            return { ok: true, status: 200, json: async () => ({ funnelId: capturedBody.funnelId, acquisitionId: 'acquisition' }) };
        }
    };
    vm.runInNewContext(fs.readFileSync(process.argv[3], 'utf8'), attributionScope);
    await attributionScope.window.designModeAttribution.capture('http://localhost:7032');
    assert.equal(capturedBody.landingVariant, 'social-default', 'existing Social default must remain unchanged');
    await attributionScope.window.designModeAttribution.capture('http://localhost:7032', 'website-funnel');
    assert.equal(capturedBody.landingVariant, 'website-funnel');
    assert.equal(capturedBody.referrer, 'https://example.test/campaign');
    assert.equal(capturedBody.gclid, 'g');
    assert.equal(capturedBody.gbraid, 'b');
    assert.equal(capturedBody.wbraid, 'w');
    assert.equal(capturedBody.campaignId, '42');
    assert.ok(capturedBody.visitorId && capturedBody.sessionId && capturedBody.funnelId);
    console.log('Website landing identity, validation, acquisition context and attribution: PASS');
})().catch(error => { console.error(error); process.exitCode = 1; });
