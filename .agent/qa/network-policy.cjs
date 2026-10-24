'use strict';

const optionalExternalHosts = new Set([
  'accounts.google.com',
  'fonts.googleapis.com',
  'fonts.gstatic.com',
  'www.googletagmanager.com',
  'www.google-analytics.com',
]);

// These assets are referenced by the application shell but are not part of the
// behavior under test. Their local substitutes are deterministic and never
// fetch the original URL.
const requiredAssetHosts = new Set([
  'blazor.cdn.telerik.com',
  'demos.creative-tim.com',
  'cdnjs.cloudflare.com',
  'raw.githubusercontent.com',
  'unpkg.com',
  'lottie.host',
]);

const localSubstituteTypes = new Map([
  ['stylesheet', 'text/css'],
  ['script', 'application/javascript'],
  ['font', 'font/woff2'],
]);

function isLocalApiPath(pathname) {
  return /^\/(?:api|swagger)(?:\/|$)/i.test(pathname);
}

function classifyRequest(rawUrl, resourceType, allowedOrigin) {
  const url = new URL(rawUrl);
  const appOrigin = new URL(allowedOrigin).origin;

  if (['data:', 'blob:', 'about:'].includes(url.protocol)) {
    return { action: 'continue-local' };
  }

  if (url.origin === appOrigin) {
    if (isLocalApiPath(url.pathname)) {
      return { action: 'block-unmocked-api', reason: 'local API responses must be explicitly mocked' };
    }
    return { action: 'continue-local' };
  }

  if (optionalExternalHosts.has(url.hostname)) {
    return { action: 'block-optional', reason: 'optional third-party identity, telemetry, or font service' };
  }

  const contentType = localSubstituteTypes.get(resourceType);
  if (requiredAssetHosts.has(url.hostname) && contentType) {
    const body = resourceType === 'stylesheet'
      ? '/* deterministic local substitute for an external stylesheet */'
      : resourceType === 'script'
        ? '/* deterministic local substitute for an external script */'
        : '';
    return { action: 'fulfill-local-substitute', contentType, body };
  }

  return { action: 'block-required', reason: 'unmocked non-local application request' };
}

module.exports = { classifyRequest, isLocalApiPath };
