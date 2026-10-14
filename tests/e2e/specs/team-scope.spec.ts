import { test, expect, signIn, state } from '../fixtures';

// G-27: a lead whose role has employees.read but not employees.scope.all sees only their reporting line.
test('a team lead sees their own report and not an employee outside the team', async ({ page }) => {
  const { lead, report, outsider } = state();
  await signIn(page, lead.email, lead.password);
  await expect(page).toHaveURL(/\/employees$/);
  const directory = page.locator('table');
  await expect(directory).toContainText(report.fullName);
  await expect(directory).toContainText(lead.fullName);
  await expect(directory).not.toContainText(outsider.fullName);

  const token = await page.evaluate(() => JSON.parse(sessionStorage.getItem('task-monitoring.admin.session.v2') ?? '{}').accessToken as string);
  const detail = await page.request.get(`/api/employees/${outsider.id}`, { headers: { Authorization: `Bearer ${token}` } });
  expect(detail.status()).toBe(404);
});

test('the administrator sees everyone, including the employee outside the team', async ({ adminPage: page }) => {
  const { lead, report, outsider, run } = state();
  await page.goto('/employees');
  await page.getByPlaceholder('Search employees').fill(run);
  await page.getByRole('button', { name: 'Search' }).click();
  const directory = page.locator('table');
  for (const person of [lead, report, outsider]) await expect(directory).toContainText(person.fullName);
});
