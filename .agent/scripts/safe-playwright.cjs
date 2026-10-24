'use strict';

<<<<<<< HEAD
// The browser is allowed to contact only the local application origin. Test
// specs may register explicit route.fulfill() handlers for local API responses;
// unmatched API traffic is denied by the guard below.
=======
// The browser may contact only the local application origin. Known shell assets
// receive deterministic local substitutes; optional identity and telemetry
// requests are blocked, and every other external request is denied.
>>>>>>> codex/4-marketing-dashboard-work-04-20261008-181415
const { createRequire } = require('node:module');
const path = require('node:path');
const localRequire = createRequire(path.join(process.cwd(), 'package.json'));
const { test: base, expect } = localRequire('@playwright/test');
<<<<<<< HEAD
const { classifyRequest } = require('./network-policy.cjs');
=======
const { classifyRequest } = require('../qa/network-policy.cjs');
>>>>>>> codex/4-marketing-dashboard-work-04-20261008-181415

const test = base.extend({
  page: async ({ page }, use, testInfo) => {
    const deniedApplicationRequests = [];
    const blockedOptionalRequests = [];
    const substitutedExternalAssets = [];
    const localResourceFailures = [];
    const applicationErrors = [];
    const allowedOrigin = process.env.DESIGNMODE_QA_ALLOWED_ORIGIN;
    if (!allowedOrigin) throw new Error('DESIGNMODE_QA_ALLOWED_ORIGIN is required for isolated browser acceptance.');

    const appOrigin = new URL(allowedOrigin).origin;
    page.on('requestfailed', request => {
      const url = new URL(request.url());
      if (url.origin === appOrigin && !isExpectedBlockedApi(url)) {
        localResourceFailures.push(`${url.href}: ${request.failure()?.errorText || 'request failed'}`);
      }
    });
    page.on('response', response => {
      const url = new URL(response.url());
      if (url.origin === appOrigin && response.status() >= 400) {
        localResourceFailures.push(`${url.href}: HTTP ${response.status()}`);
      }
    });
    page.on('pageerror', error => applicationErrors.push(error.stack || error.message));

    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      const decision = classifyRequest(url.href, route.request().resourceType(), allowedOrigin);
      if (decision.action === 'continue-local') {
        await route.continue();
        return;
      }

      if (decision.action === 'block-optional') {
        blockedOptionalRequests.push(url.href);
        await route.abort('blockedbyclient');
        return;
      }

      if (decision.action === 'fulfill-local-substitute') {
        substitutedExternalAssets.push(url.href);
        await route.fulfill({ status: 200, contentType: decision.contentType, body: decision.body });
        return;
      }

      deniedApplicationRequests.push(url.href);
      await route.abort('blockedbyclient');
    });

    await use(page);

    await testInfo.attach('network-isolation.json', {
      body: Buffer.from(JSON.stringify({
        allowedOrigin,
        blockedOptionalRequests,
        substitutedExternalAssets,
        deniedApplicationRequests,
        localResourceFailures,
        applicationErrors,
      }, null, 2)),
      contentType: 'application/json',
    });

    if (testInfo.status !== testInfo.expectedStatus) {
      await page.screenshot({ path: testInfo.outputPath('failure.png'), fullPage: true }).catch(() => {});
    }
    const failures = getAcceptanceFailures({ deniedApplicationRequests, localResourceFailures, applicationErrors });
    if (failures.length) {
      throw new Error(failures.join(' '));
    }
  }
});

function isExpectedBlockedApi(url) {
  return /^\/(?:api|swagger)(?:\/|$)/i.test(url.pathname);
}

function getAcceptanceFailures({ deniedApplicationRequests, localResourceFailures, applicationErrors }) {
  const failures = [];
  if (deniedApplicationRequests.length) {
    failures.push(`Acceptance attempted an unmocked application request; requests were blocked: ${deniedApplicationRequests.join(', ')}`);
  }
  if (localResourceFailures.length) {
    failures.push(`Acceptance had failed local application resources: ${localResourceFailures.join(', ')}`);
  }
  if (applicationErrors.length) {
    failures.push(`Acceptance had uncaught application errors: ${applicationErrors.join(' | ')}`);
  }
  return failures;
}

module.exports = { test, expect, getAcceptanceFailures };
