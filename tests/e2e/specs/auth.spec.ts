import { test, expect, signIn, state } from '../fixtures';

test('a wrong password is refused and keeps the user on the sign-in page', async ({ page }) => {
  await signIn(page, state().admin.email, 'Not-the-right-password-1');
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);
});

test('the administrator signs in, lands on the dashboard and signs out', async ({ page }) => {
  await signIn(page, state().admin.email, process.env.E2E_ADMIN_PASSWORD!);
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login$/);
  // The session is gone: a protected page sends the browser back to sign-in.
  await page.goto('/employees');
  await expect(page).toHaveURL(/\/login$/);
});
