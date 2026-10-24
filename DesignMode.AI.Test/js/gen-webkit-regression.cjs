// Opt-in browser regression; see docs/gen-ios-blank-page.md for prerequisites.
// No production API or AI provider requests are allowed by this fixture.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium, webkit } = require(process.env.PLAYWRIGHT_MODULE ||
    path.resolve(__dirname, '../../DesignMode.AI.Server/publish-test/.playwright/package'));
const base = 'http://localhost:5187';
const claim = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name';
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
const token = `${encode({ alg: 'none', typ: 'JWT' })}.${encode({
    sub: 'local-fixture', [claim]: 'fixture@example.invalid', exp: 4102444800
})}.`;
const cases = [
    [webkit, 390, 844], [webkit, 430, 932], [webkit, 375, 667],
    [webkit, 1440, 900], [chromium, 1920, 1080],
    [chromium, 1440, 900], [chromium, 1366, 768]
];

async function check(engine, width, height) {
    const browser = await engine.launch({ headless: true });
    try {
        const context = await browser.newContext({ viewport: { width, height } });
        await context.addInitScript(value => {
            localStorage.setItem('authToken', JSON.stringify(value));
            localStorage.setItem('authTokenExpiration', JSON.stringify('2100-01-01T00:00:00Z'));
        }, token);
        await context.route('**/*', async route => {
            const url = new URL(route.request().url());
            if (url.origin === 'http://localhost:7032') {
                const fixtures = {
                    '/api/dashboard/projects': [{ name: 'Fixture project', companyIdeaId: 'fixture' }],
                    '/api/GetImageIdeasByCompanyId': [{
                        id: 'fixture-image', imageUrl: `${base}/favicon.png`,
                        deliverableType: 'Website UI/UX', subCategory: 'Generic', countId: '1'
                    }],
                    '/api/ai-usage-summary': { generationLimit: 100, generationsUsed: 0, generationsRemaining: 100 },
                    '/api/GetPromptIdForCompanyIdea': '',
                    '/api/GetImageIdeaCountsByCompanyId': { totalCount: 1 },
                    '/api/measurement/paid-workspace': {},
                    '/api/UpdateUserCurrentPage': {}
                };
                const known = Object.hasOwn(fixtures, url.pathname);
                return route.fulfill({
                    status: known ? 200 : 404, contentType: 'application/json',
                    headers: { 'access-control-allow-origin': '*' },
                    body: JSON.stringify(known ? fixtures[url.pathname] : {})
                });
            }
            if (url.origin === base) return route.continue();
            // Only public UI dependencies may leave localhost.
            if (['blazor.cdn.telerik.com', 'cdnjs.cloudflare.com', 'fonts.googleapis.com',
                'fonts.gstatic.com', 'demos.creative-tim.com', 'unpkg.com', 'lottie.host'].includes(url.hostname)) {
                return route.continue();
            }
            return route.fulfill({ status: 200, body: '' });
        });
        const page = await context.newPage();
        const errors = [], pageErrors = [], network = [];
        page.on('console', message => {
            if (message.type() === 'error') errors.push(message.text());
        });
        page.on('pageerror', error => pageErrors.push(error.message));
        page.on('requestfailed', request => network.push({ path: new URL(request.url()).pathname, failed: true }));
        page.on('response', response => {
            if (response.status() >= 400) network.push({ path: new URL(response.url()).pathname, status: response.status() });
        });
        await page.goto(`${base}/init/dashboard`);
        await page.locator('.project-card').waitFor({ timeout: 45000 });
        await page.locator('.project-card').click();
        await page.locator('#pageHeader').waitFor({ timeout: 15000 });
        await page.waitForFunction(() => {
            const root = document.querySelector('#pageHeader');
            return root && getComputedStyle(root).opacity === '1' &&
                root.textContent.includes('Main Concept:');
        });
        assert.equal(new URL(page.url()).searchParams.get('cid'), 'fixture');
        assert.equal(await page.locator('.ai-usage-meter').isVisible(), true);
        assert.equal(await page.locator('.client-nav').isVisible(), true);
        assert.equal(await page.locator('#ai-recommendations-container').isVisible(), true);
        assert.equal(await page.locator('#generation-exit-confirmation').isVisible(), false);
        await page.waitForFunction(() => Array.from(document.querySelectorAll('#pageHeader img'))
            .some(image => image.src.endsWith('/favicon.png') && image.complete && image.naturalWidth > 0));
        const invalidAttributes = await page.evaluate(() => Array.from(document.querySelectorAll('#app *'))
            .flatMap(element => Array.from(element.attributes).map(attribute => attribute.name))
            .filter(name => name.startsWith('@')));
        assert.deepEqual(invalidAttributes, []);
        assert.deepEqual(errors.filter(error => /Unhandled exception rendering component|InvalidCharacterError/.test(error)), []);
        // Existing inline-script insertion syntax errors also occur on the unmodified
        // working Chromium baseline. Report these separately from the blank-page gate.
        assert.deepEqual(pageErrors.filter(error => !/Unexpected end of (script|input)/.test(error)), []);
        console.log(JSON.stringify({ engine: engine.name(), width, height, blankPageGate: 'PASS', pageErrors, network }));
    } finally {
        await browser.close();
    }
}

(async () => {
    for (const args of cases) await check(...args);
})().catch(error => { console.error(error); process.exitCode = 1; });
