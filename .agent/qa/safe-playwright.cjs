'use strict';

// Keep direct Playwright runs on the same network policy as the QA pipeline.
module.exports = require('../scripts/safe-playwright.cjs');
