const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const read = file => fs.readFileSync(path.join(__dirname, '../DesignMode.AI.Client/', file), 'utf8');

const policy = read('wwwroot/privacy.html');
const policyText = policy.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

test('policy discloses account email use and conditional preview-ready notifications', () => {
    assert.match(policyText, /Email Communications/);
    assert.match(policyText, /After you create an account or sign up, we may use the email address associated with your account/);
    assert.match(policyText, /requested password resets/);
    assert.match(policyText, /begin but do not complete the onboarding or design process/);
    assert.match(policyText, /may email you when your generated design preview is ready/);
    assert.match(policyText, /Not every generation results in an email/);
});

test('policy distinguishes promotional reminders and their opt-out rule', () => {
    assert.match(policyText, /follow-up reminders.*promotional or lifecycle marketing communications/);
    assert.match(policyText, /may send these reminders for eligible unfinished projects unless you have opted out of recovery reminders/);
    assert.doesNotMatch(policyText, /additional reminders are not currently sent to users generally/);
    assert.match(policyText, /stop when you subscribe or complete the relevant onboarding or design process/);
    assert.match(policyText, /does not replace affirmative marketing consent where required/);
    assert.doesNotMatch(policyText, /unlimited marketing|purely transactional|24 hours|72 hours|7 days/i);
});

test('policy accurately scopes unsubscribe and explains the confirmation step', () => {
    assert.match(policyText, /opt out of promotional recovery reminders for all projects associated with your account/);
    assert.match(policyText, /unsubscribe link in a recovery email and confirming your choice on the unsubscribe page/);
    assert.match(policyText, /does not stop service emails, such as preview-ready notifications or requested password resets/);
});

test('policy discloses SES delivery without internal email configuration or test wording', () => {
    assert.match(policyText, /recipient email addresses and message content/);
    assert.match(policyText, /Amazon Simple Email Service \(AWS SES\)/);
    assert.doesNotMatch(policy, /\[LOCAL TEST\]|FunnelRecovery|EmailSuppression|RecoveryEmailsUnsubscribedAt|AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|SES_FROM_EMAIL|JwtSecretKey|arn:aws:|\b\d{12}\b/);
    assert.match(policyText, /Effective Date: September 18, 2026/);
});

test('policy retains advertising, analytics, AI, payments and jurisdiction disclosures', () => {
    for (const disclosure of [
        'It does not replace affirmative consent where required.',
        'We do not sell personal information.',
        'We do not use customer content, prompts, or outputs to train public or third-party AI models.',
        'DesignMode does not store full payment card details on its servers.',
        'Google advertising measurement are enabled.',
        'ad personalization remains disabled.',
        'Google Ads conversion delivery is not enabled in this release.',
        'We do not send email or phone as Ads conversion identifiers;',
        'Privacy-filtered analytics is enabled by default.',
        'The Services are not intended for children under 13 years old.',
        'your information may be processed and stored in the United States or other jurisdictions'
    ]) assert.ok(policyText.includes(disclosure), disclosure);
    assert.match(policy, /Final wording and applicable consent\/opt-out requirements need jurisdiction-specific review/);
});

function browser(analytics = true) {
    const stored = new Map([['dm_measurement_consent', JSON.stringify({ analytics, advertising: false })]]);
    const nodes = new Map();
    function node() {
        const children = new Map();
        return { style: {}, setAttribute() {}, querySelector: key => {
            if (!children.has(key)) children.set(key, node()); return children.get(key);
        }, showModal() {}, close() {} };
    }
    const document = { head: { appendChild() {} }, body: { appendChild: n => { if (n.id) nodes.set(n.id, n); } },
        createElement: node, getElementById: id => nodes.get(id), addEventListener() {} };
    const window = { location: { origin: 'https://example.invalid', href: 'https://example.invalid/social-landing.html' }, addEventListener() {} };
    window.self = window; window.top = window;
    const context = { window, document, URL, console, setTimeout, clearTimeout,
        localStorage: { getItem: k => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, v) },
        fetch: async url => {
            assert.equal(url, '/analytics-config.json');
            return { ok: true, json: async () => ({ enabled: false }) };
        } };
    vm.createContext(context);
    vm.runInContext(read('wwwroot/js/designmode-analytics.js'), context);
    vm.runInContext(read('wwwroot/js/designmode-privacy.js'), context);
    return { window, stored, nodes };
}

test('Google advertising is always on and no consent evidence API is called', async () => {
    for (const analytics of [true, false]) {
        const b = browser(analytics);
        await b.window.designModePrivacy.initialize('https://api.example.invalid/');
        assert.equal(b.window.designModeAnalytics.getConsent().advertising, true);
        assert.equal(b.window.designModeAnalytics.getConsent().analytics, analytics);
        assert.ok(b.nodes.has('dm-privacy-access'));
        assert.equal(b.stored.has('dm_consent_credential'), false);
        assert.equal(b.stored.has('dm_consent_pending'), false);
    }
});

test('client cannot turn Google advertising off but analytics stays independent', async () => {
    const b = browser();
    b.window.designModeAnalytics.setConsent({ analytics: false, advertising: false });
    const consent = b.window.designModeAnalytics.getConsent();
    assert.equal(consent.analytics, false); assert.equal(consent.advertising, true);
    const context = await b.window.designModeAnalytics.checkoutContext();
    assert.equal(context.analyticsConsent, false);
    assert.equal(context.advertisingConsent, true);
    assert.equal(context.googleAdvertisingConsent, true);
});

test('shared static and Blazor UI has no advertising toggle or consent API workflow', () => {
    assert.match(read('wwwroot/social-landing.html'), /designModePrivacy\?\.initialize\(API_BASE_URL\)/);
    assert.match(read('App.razor'), /<PrivacyPreferences/);
    assert.match(read('wwwroot/index.html'), /js\/designmode-privacy.js/);
    const source = read('wwwroot/js/designmode-privacy.js');
    assert.doesNotMatch(source, /chooseAdvertising|AdvertisingConsent|data-enable|data-disable|\/api\/AdvertisingConsent|gtag\(/);
    assert.match(source, /Google advertising measurement are enabled/);
    assert.match(source, /Enable analytics/);
    assert.match(source, /Disable analytics/);
});
