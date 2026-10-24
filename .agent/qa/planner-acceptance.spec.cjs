const { test, expect } = require('@playwright/test');

const viewports = [
    {
        name: 'desktop',
        width: 1440,
        height: 900,
        browser: 'chromium'
    },
    {
        name: 'mobile-webkit',
        width: 390,
        height: 844,
        browser: 'webkit'
    }
];

for (const viewport of viewports) {
    test(`${viewport.name} planner modal acceptance`, async ({ playwright }) => {
        const browser = await playwright[viewport.browser].launch();

        const context = await browser.newContext({
            viewport: {
                width: viewport.width,
                height: viewport.height
            }
        });

        /*
         * Synthetic local QA authentication fixture.
         *
         * This matches the fixture proven during Experiment 3.
         * It is only intended to allow browser QA to reach the
         * existing protected client-side test route.
         *
         * It does NOT verify backend authentication.
         */
        const token =
            Buffer.from(
                JSON.stringify({
                    alg: 'HS256',
                    typ: 'JWT'
                })
            ).toString('base64url') +
            '.' +
            Buffer.from(
                JSON.stringify({
                    sub: 'local-qa-fixture',
                    name: 'Local QA',
                    exp: Math.floor(Date.now() / 1000) + 3600
                })
            ).toString('base64url') +
            '.cWE';

        await context.addInitScript(
            ({ token }) => {
                localStorage.setItem(
                    'authToken',
                    JSON.stringify(token)
                );

                localStorage.setItem(
                    'authTokenExpiration',
                    JSON.stringify(
                        new Date(Date.now() + 3600000).toISOString()
                    )
                );
            },
            { token }
        );

        const page = await context.newPage();

        const consoleErrors = [];
        const pageErrors = [];
        const networkErrors = [];

        page.on('console', message => {
            if (message.type() === 'error') {
                consoleErrors.push(message.text());
            }
        });

        page.on('pageerror', error => {
            pageErrors.push(error.message);
        });

        page.on('requestfailed', request => {
            networkErrors.push(
                `${request.method()} ${request.url()}: ` +
                `${request.failure()?.errorText}`
            );
        });

        try {
            const response = await page.goto(
                'http://127.0.0.1:5276/app/testlogic',
                {
                    waitUntil: 'networkidle',
                    timeout: 90000
                }
            );

            /*
             * Verify that the page itself loaded successfully.
             */
            expect(
                response?.ok(),
                `page response ${response?.status()}`
            ).toBeTruthy();

            /*
             * Explicit authentication/route-guard check.
             *
             * If this fails and the URL is /login, we know QA
             * authentication failed rather than the feature.
             */
            expect(
                page.url(),
                `Expected protected test route but reached ${page.url()}`
            ).toContain('/app/testlogic');

            /*
             * AC-01 / button verification
             */
            const button = page.getByTestId(
                'planner-test-button'
            );

            await expect(button).toBeVisible();

            await expect(button).toHaveText(
                'PLANNER TEST'
            );

            /*
             * Verify the page does not overflow before opening
             * the modal.
             */
            expect(
                await page.evaluate(
                    () =>
                        document.documentElement.scrollWidth <=
                        window.innerWidth
                )
            ).toBeTruthy();

            /*
             * AC-02 / open modal
             */
            await button.click();

            const modal = page.getByTestId(
                'planner-test-modal'
            );

            const message = page.getByTestId(
                'planner-test-message'
            );

            const close = page.getByTestId(
                'planner-test-close'
            );

            await expect(modal).toBeVisible();

            await expect(modal).toHaveAttribute(
                'role',
                'dialog'
            );

            await expect(modal).toHaveAttribute(
                'aria-modal',
                'true'
            );

            /*
             * Exact required message.
             */
            await expect(message).toBeVisible();

            await expect(message).toHaveText(
                'PLANNER AGENT WORKS'
            );

            /*
             * Close control.
             */
            await expect(close).toBeVisible();

            /*
             * Verify no horizontal overflow while modal is open.
             */
            expect(
                await page.evaluate(
                    () =>
                        document.documentElement.scrollWidth <=
                        window.innerWidth
                )
            ).toBeTruthy();

            /*
             * Verify modal remains inside the viewport.
             */
            const modalContent = modal.locator(
                '.modal-content'
            );

            const bounds = await modalContent.boundingBox();

            expect(bounds).not.toBeNull();

            expect(bounds.x).toBeGreaterThanOrEqual(0);
            expect(bounds.y).toBeGreaterThanOrEqual(0);

            expect(
                bounds.x + bounds.width
            ).toBeLessThanOrEqual(viewport.width);

            expect(
                bounds.y + bounds.height
            ).toBeLessThanOrEqual(viewport.height);

            /*
             * AC-03 / close modal.
             */
            await close.click();

            await expect(modal).toHaveCount(0);

            await expect(button).toBeVisible();

            /* Existing page controls must remain interactive. */
            const existingControls = [
                ['auto-agent-test-button', 'auto-agent-test-modal', 'auto-agent-test-close'],
                ['auto-repair-test-button', 'auto-repair-test-modal', 'auto-repair-test-close']
            ];

            for (const [buttonId, modalId, closeId] of existingControls) {
                await page.getByTestId(buttonId).click();
                await expect(page.getByTestId(modalId)).toBeVisible();
                await page.getByTestId(closeId).click();
                await expect(page.getByTestId(modalId)).toHaveCount(0);
            }

            /*
             * Browser/runtime health.
             */
            expect(pageErrors).toEqual([]);

            /*
             * Ignore failed third-party analytics requests here.
             * Previous experiments showed Google Analytics can
             * be cancelled by the browser without representing
             * an application failure.
             */
            const applicationNetworkErrors =
                networkErrors.filter(error =>
                    !error.includes('google-analytics.com') &&
                    !error.includes('googletagmanager.com')
                );

            expect(applicationNetworkErrors).toEqual([]);

            /*
             * Console errors remain strict.
             */
            expect(consoleErrors).toEqual([]);

            console.log(
                JSON.stringify({
                    viewport: viewport.name,
                    status: 'PASS',
                    finalUrl: page.url(),
                    consoleErrors,
                    pageErrors,
                    networkErrors,
                    modalBounds: bounds
                })
            );
        } catch (error) {
            /*
             * Preserve failure evidence.
             */
            await page
                .screenshot({
                    path:
                        `../results/planner-test/` +
                        `${viewport.name}-failure.png`,
                    fullPage: true
                })
                .catch(() => { });

            console.log(
                JSON.stringify({
                    viewport: viewport.name,
                    status: 'FAIL',
                    finalUrl: page.url(),
                    consoleErrors,
                    pageErrors,
                    networkErrors,
                    error: error.message
                })
            );

            throw error;
        } finally {
            await browser.close();
        }
    });
}
