import { readFileSync } from 'node:fs';
import { test as base, expect, type Page } from '@playwright/test';
import { STATE_FILE, type E2EState } from './global-setup';

export const state = (): E2EState => JSON.parse(readFileSync(STATE_FILE, 'utf8')) as E2EState;

/** Signs in through the real Admin sign-in form. */
export async function signIn(page: Page, email: string, password: string) {
  await page.goto('/login');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

export const test = base.extend<{ adminPage: Page }>({
  // The admin session from global setup, placed where the Admin Web keeps it, so tests that are not
  // about sign-in do not spend the sign-in rate limit.
  adminPage: async ({ page }, use) => {
    const session = JSON.stringify(state().admin.session);
    await page.addInitScript(value => sessionStorage.setItem('task-monitoring.admin.session.v2', value), session);
    await use(page);
  }
});

export { expect };
