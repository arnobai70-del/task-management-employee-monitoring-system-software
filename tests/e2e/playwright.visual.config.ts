import { defineConfig, devices } from '@playwright/test';

// Visual regression for the Admin Web (G-13). No API is needed: every /api call is answered with fixed,
// fictional data and the clock is frozen, so a page renders the same on every run. Baselines are
// rendered inside the mcr.microsoft.com/playwright:v1.56.1-noble image (the CI job uses the same image)
// because fonts and anti-aliasing differ between machines. Update baselines after an intended UI change
// with `npx playwright test -c playwright.visual.config.ts --update-snapshots` in that image.
export default defineConfig({
  testDir: './visual',
  snapshotPathTemplate: '{testDir}/__screenshots__/{arg}{ext}',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report-visual' }]] : 'list',
  expect: { toHaveScreenshot: { maxDiffPixels: 50, animations: 'disabled', caret: 'hide' } },
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:4173',
    ...devices['Desktop Chrome'],
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
    launchOptions: process.env.E2E_CHROMIUM ? { executablePath: process.env.E2E_CHROMIUM } : {}
  }
});
