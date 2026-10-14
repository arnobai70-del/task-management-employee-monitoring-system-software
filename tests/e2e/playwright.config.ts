import { defineConfig, devices } from '@playwright/test';

// End-to-end and RBAC checks against a real API and the built Admin Web (G-17).
// Needs: the API on E2E_API_URL with a bootstrap admin (E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD),
// seed-roles.sql applied to its database, and `vite preview` of the Admin Web on E2E_BASE_URL
// proxying /api to that API. CI wires this up in the "Admin Web end-to-end" job; all data is fictional.
// The sign-in endpoint allows 10 attempts per minute per client, so the suite signs in through the
// UI only where sign-in is what is being tested and reuses the admin session elsewhere.
export default defineConfig({
  testDir: './specs',
  globalSetup: './global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:4173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
    launchOptions: process.env.E2E_CHROMIUM ? { executablePath: process.env.E2E_CHROMIUM } : {}
  }
});
