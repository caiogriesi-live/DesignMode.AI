module.exports = {
    testDir: '.',
    testMatch: [
        'planner-acceptance.spec.cjs',
        'generated-*.spec.cjs'
    ],
    reporter: 'line',
    outputDir: '../results/playwright-artifacts',
    use: {
        trace: 'retain-on-failure'
    }
};