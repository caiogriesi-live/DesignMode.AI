'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { classifyRequest } = require('./network-policy.cjs');
// Exercise the source that run-qa.ps1 copies over the QA-directory adapter.
const { getAcceptanceFailures } = require('../scripts/safe-playwright.cjs');

const app = 'http://127.0.0.1:5276';
const workItems = ['WORK-01', 'WORK-04', 'WORK-09'];

for (const workItem of workItems) {
  test(`${workItem}: keeps optional identity, analytics, and financial services blocked`, () => {
    for (const url of [
      'https://accounts.google.com/gsi/client',
      'https://www.googletagmanager.com/gtag/js',
      'https://www.google-analytics.com/g/collect',
      'https://api.stripe.com/v1/charges',
      'https://payments.example.test/expenses',
    ]) {
      assert.equal(classifyRequest(url, 'script', app).action,
        url.includes('accounts.google.com') || url.includes('googletagmanager') || url.includes('google-analytics')
          ? 'block-optional'
          : 'block-required');
    }
  });

  test(`${workItem}: substitutes approved external assets deterministically`, () => {
    const cases = [
      ['https://blazor.cdn.telerik.com/blazor/7.1.0/kendo-theme-bootstrap/all.css', 'stylesheet', 'text/css'],
      ['https://blazor.cdn.telerik.com/blazor/7.1.0/telerik-blazor.min.js', 'script', 'application/javascript'],
      ['https://demos.creative-tim.com/argon-design-system/assets/css/theme.css', 'stylesheet', 'text/css'],
      ['https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.0.0-beta3/css/all.min.css', 'stylesheet', 'text/css'],
      ['https://unpkg.com/@dotlottie/player-component@latest/dist/dotlottie-player.mjs', 'script', 'application/javascript'],
      ['https://raw.githubusercontent.com/vendor/font.ttf', 'font', 'font/woff2'],
    ];
    for (const [url, type, contentType] of cases) {
      const first = classifyRequest(url, type, app);
      const second = classifyRequest(url, type, app);
      assert.equal(first.action, 'fulfill-local-substitute');
      assert.equal(first.contentType, contentType);
      assert.deepEqual(second, first);
    }
  });

  test(`${workItem}: allows local pages but requires explicitly mocked local APIs`, () => {
    assert.equal(classifyRequest(`${app}/admin/expenses`, 'document', app).action, 'continue-local');
    assert.equal(classifyRequest(`${app}/api/business-expenses?date=2026-09-17`, 'fetch', app).action,
      'block-unmocked-api');
  });

  test(`${workItem}: blocks unknown external requests, including static resources`, () => {
    for (const type of ['document', 'stylesheet', 'script', 'font', 'fetch', 'image']) {
      assert.equal(classifyRequest('https://unapproved.example.test/resource', type, app).action, 'block-required');
    }
  });
}

test('optional blocked assets do not fail acceptance', () => {
  assert.deepEqual(getAcceptanceFailures({
    deniedApplicationRequests: [],
    localResourceFailures: [],
    applicationErrors: [],
    blockedOptionalRequests: [
      'https://fonts.googleapis.com/css?family=Inter',
      'https://www.google-analytics.com/g/collect',
    ],
  }), []);
});

test('unmocked local APIs, failed local resources, and application errors fail acceptance', () => {
  assert.match(getAcceptanceFailures({
    deniedApplicationRequests: ['http://127.0.0.1:5276/api/business-expenses'],
    localResourceFailures: ['http://127.0.0.1:5276/app.css: HTTP 404'],
    applicationErrors: ['TypeError: failed to render'],
  }).join(' '), /unmocked application request.*failed local application resources.*uncaught application errors/);
});

test('known asset matching is exact-host and unknown external resources remain failures', () => {
  assert.equal(classifyRequest('https://blazor.cdn.telerik.com.evil.test/theme.css', 'stylesheet', app).action,
    'block-required');
  assert.equal(classifyRequest('https://blazor.cdn.telerik.com/theme.css', 'fetch', app).action,
    'block-required');
  assert.deepEqual(getAcceptanceFailures({
    deniedApplicationRequests: ['https://unapproved.example.test/expense.json'],
    localResourceFailures: [],
    applicationErrors: [],
  }), ['Acceptance attempted an unmocked application request; requests were blocked: https://unapproved.example.test/expense.json']);
});
