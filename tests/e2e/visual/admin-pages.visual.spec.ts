import { test, expect } from '@playwright/test';
import { mockAdminWeb } from './mock-api';

// Each page is checked in light and dark, on a phone and on a desktop.
const viewports = [
  { name: 'phone', width: 375, height: 812 },
  { name: 'desktop', width: 1440, height: 900 }
];
const schemes = ['light', 'dark'] as const;
const pages = [
  { name: 'login', path: '/login', signedIn: false, ready: 'Sign in' },
  { name: 'dashboard', path: '/dashboard', signedIn: true, ready: 'Ayesha Rahman' },
  { name: 'employees', path: '/employees', signedIn: true, ready: 'Tanvir Hasan' },
  { name: 'forbidden', path: '/forbidden', signedIn: true, ready: null },
  { name: 'not-found', path: '/no-such-page', signedIn: true, ready: null }
];

for (const viewport of viewports) {
  for (const colorScheme of schemes) {
    test.describe(`${viewport.name} ${colorScheme}`, () => {
      test.use({ viewport: { width: viewport.width, height: viewport.height }, colorScheme });

      for (const target of pages) {
        test(target.name, async ({ page }) => {
          const errors: string[] = [];
          page.on('pageerror', error => errors.push(error.message));
          await mockAdminWeb(page, { signedIn: target.signedIn });
          await page.goto(target.path);
          await expect(page.locator('h1').first()).toBeVisible();
          if (target.ready) await expect(page.getByText(target.ready).first()).toBeVisible();
          await page.waitForLoadState('networkidle');
          await page.evaluate(() => document.fonts.ready);
          // A render crash after the first paint would otherwise be captured as a blank baseline.
          expect(errors).toEqual([]);
          await expect(page).toHaveScreenshot(`${target.name}-${viewport.name}-${colorScheme}.png`, { fullPage: true });
        });
      }
    });
  }
}
