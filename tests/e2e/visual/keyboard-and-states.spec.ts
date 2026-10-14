import { test, expect, type Page } from '@playwright/test';
import { mockAdminWeb } from './mock-api';

// Keyboard use and empty / error / loading states of the Admin Web (G-13), on the same fixed fictional
// data as the screenshot suite. No screenshots here: these check behaviour.

const focused = (page: Page) => page.evaluate(() => {
  const el = document.activeElement as HTMLElement | null;
  return { tag: el?.tagName.toLowerCase() ?? '', text: (el?.getAttribute('aria-label') || el?.getAttribute('placeholder') || el?.textContent || '').trim(), inDialog: Boolean(el?.closest('[role=dialog]')), inNav: Boolean(el?.closest('nav')) };
});

test.describe('desktop keyboard', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('the first Tab offers a skip link that moves focus past the navigation', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    await page.goto('/employees');
    await expect(page.getByText('Tanvir Hasan')).toBeVisible();
    await page.keyboard.press('Tab');
    expect((await focused(page)).text).toBe('Skip to main content');
    await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeInViewport();
    await page.keyboard.press('Enter');
    expect((await focused(page)).tag).toBe('main');
    await page.keyboard.press('Tab');
    const next = await focused(page);
    expect(next.inNav).toBe(false);
    expect(next.text).toBe('Search employees');
  });

  test('quick jump keeps focus inside the dialog and gives it back on Escape', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    await page.goto('/employees');
    await expect(page.getByText('Tanvir Hasan')).toBeVisible();
    await page.getByRole('button', { name: 'Refresh' }).focus();
    await page.keyboard.press('Control+k');
    await expect(page.getByRole('dialog', { name: 'Quick navigation' })).toBeVisible();
    await expect.poll(async () => (await focused(page)).text).toBe('Search admin workspaces');
    for (let i = 0; i < 30; i++) {
      await page.keyboard.press('Tab');
      expect((await focused(page)).inDialog, `Tab ${i + 1} left the dialog`).toBe(true);
    }
    await page.getByLabel('Search admin workspaces').focus();
    await page.keyboard.press('Shift+Tab');
    expect((await focused(page)).inDialog).toBe(true);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect((await focused(page)).text).toBe('Refresh');
  });
});

test.describe('phone keyboard', () => {
  test.use({ viewport: { width: 375, height: 812 } });

  test('the closed menu is out of the Tab order and the menu button opens and closes it', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    await page.goto('/employees');
    await expect(page.getByText('Tanvir Hasan')).toBeVisible();
    const menuButton = page.getByRole('button', { name: 'Toggle navigation' });
    await expect(menuButton).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Tab');
    expect((await focused(page)).text).toBe('Skip to main content');
    await page.keyboard.press('Tab');
    expect((await focused(page)).text).toBe('Toggle navigation');

    await page.keyboard.press('Enter');
    await expect(menuButton).toHaveAttribute('aria-expanded', 'true');
    await expect.poll(async () => (await focused(page)).inNav).toBe(true);
    await expect(page.getByRole('link', { name: /Dashboard/ })).toBeInViewport();

    await page.keyboard.press('Escape');
    await expect(menuButton).toHaveAttribute('aria-expanded', 'false');
    expect((await focused(page)).text).toBe('Toggle navigation');
    await expect(page.getByRole('link', { name: /Dashboard/ })).toBeHidden();
  });
});

test.describe('sign-in by keyboard', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('Tab reaches email, password and the button, and Enter submits', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: false });
    await page.route('**/api/auth/web/login', route => route.fulfill({ status: 401, json: { title: 'Invalid email or password.', status: 401 } }));
    await page.goto('/login');
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
    await page.getByLabel('Email', { exact: true }).focus();
    await page.keyboard.type('visual-admin@example.invalid');
    await page.keyboard.press('Tab');
    expect((await focused(page)).tag).toBe('input');
    await page.keyboard.type('not-the-password');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page).toHaveURL(/\/login$/);
  });
});

test.describe('empty, error and loading states', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('a failed list load is announced and the page keeps working', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    await page.route(/\/api\/employees(\?|$)/, route => route.fulfill({ status: 500, json: { title: 'Server error', status: 500 } }));
    await page.goto('/employees');
    await expect(page.getByRole('alert').filter({ hasText: 'Server error' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Employees & roles' })).toBeVisible();
  });

  test('a failed dashboard load is announced and Retry recovers', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    let fail = true;
    await page.route('**/api/reports/dashboard', route => fail
      ? route.fulfill({ status: 500, json: { title: 'Server error', status: 500 } })
      : route.fallback());
    await page.goto('/dashboard');
    const alert = page.getByRole('alert').filter({ hasText: 'Could not load this view.' });
    await expect(alert).toBeVisible();
    fail = false;
    await alert.getByRole('button', { name: 'Retry' }).click();
    await expect(page.getByText('Active employees')).toBeVisible();
    await expect(alert).toHaveCount(0);
  });

  test('a slow load shows a loading message, then the data', async ({ page }) => {
    await mockAdminWeb(page, { signedIn: true });
    let release: () => void = () => {};
    const held = new Promise<void>(resolve => { release = resolve; });
    await page.route(/\/api\/employees(\?|$)/, async route => { await held; await route.fallback(); });
    await page.goto('/employees');
    await expect(page.getByText('Loading…')).toBeVisible();
    release();
    await expect(page.getByText('Tanvir Hasan')).toBeVisible();
    await expect(page.getByText('Loading…')).toHaveCount(0);
  });

  test('an empty list shows zero results without crashing', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mockAdminWeb(page, { signedIn: true });
    await page.route(/\/api\/employees(\?|$)/, route => route.fulfill({ json: { items: [], page: 1, pageSize: 100, totalCount: 0 } }));
    await page.goto('/employees');
    await expect(page.getByText('0 loaded')).toBeVisible();
    await expect(page.locator('tbody tr')).toHaveCount(0);
    expect(errors).toEqual([]);
  });
});
