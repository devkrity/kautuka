import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  timeout: 300_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'test-results/html' }]],
  use: {
    // Raw traces/network captures can contain Authorization and cookie values.
    trace: 'off',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 60_000,
  },
  outputDir: 'test-results/artifacts',
});
