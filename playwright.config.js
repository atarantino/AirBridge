const path = require('node:path');
const { defineConfig } = require('@playwright/test');

const output = process.env.AIRBRIDGE_BROWSER_OUTPUT || path.join(__dirname, 'artifacts', 'browser-qa');
module.exports = defineConfig({
  testDir: './tests/browser-e2e',
  timeout: 30000,
  expect: { timeout: 7000 },
  workers: 1,
  retries: 0,
  outputDir: path.join(output, 'results'),
  reporter: [['list'], ['json', { outputFile: path.join(output, 'report.json') }]],
});
