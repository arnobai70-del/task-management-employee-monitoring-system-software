import { test, expect, state } from '../fixtures';

test('the administrator creates an employee through the form and finds them in the directory', async ({ adminPage: page }) => {
  const { run, lead } = state();
  const code = `E2E-NEW-${run}`;
  const fullName = `E2E New Hire ${run}`;
  await page.goto('/employees');
  await page.getByRole('button', { name: 'New employee' }).click();
  await page.getByLabel('Employee code').fill(code);
  await page.getByLabel('Full name').fill(fullName);
  await page.getByLabel('Email', { exact: true }).fill(`e2e-new-${run.toLowerCase()}@example.invalid`);
  await page.getByLabel('Initial password').fill(`E2e-New-Hire-${run}-pass`);
  await page.getByLabel('Job title').fill('Fictional tester');
  // The pickers fill in once the page's lookups return; wait for the lead to be offered.
  const supervisor = page.getByRole('combobox', { name: 'Supervisor', exact: true });
  await expect(supervisor).toContainText(lead.fullName);
  await supervisor.selectOption({ label: `${lead.fullName} (${lead.code})` });
  await page.getByRole('checkbox', { name: 'Employee', exact: true }).check();
  await page.getByRole('button', { name: 'Save employee' }).click();

  await page.getByPlaceholder('Search employees').fill(code);
  await page.getByRole('button', { name: 'Search' }).click();
  const row = page.locator('tbody tr', { hasText: code });
  await expect(row).toContainText(fullName);
  await expect(row).toContainText(lead.fullName);
  await expect(row).toContainText('Employee');
});
