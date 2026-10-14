import { test, expect, signIn, state } from '../fixtures';

// An account whose roles grant no admin permission (the built-in Employee role).
test('an employee without admin permissions gets the permission page, in the browser and from the API', async ({ page }) => {
  const { outsider } = state();
  await signIn(page, outsider.email, outsider.password);
  await expect(page).toHaveURL(/\/forbidden$/);
  await expect(page.getByRole('heading', { name: 'Permission required' })).toBeVisible();

  for (const path of ['/employees', '/attendance', '/audit-logs', '/access/rdp']) {
    await page.goto(path);
    await expect(page, `${path} should be refused`).toHaveURL(/\/forbidden$/);
  }

  // The server enforces it too, not only the menu.
  const token = await page.evaluate(() => JSON.parse(sessionStorage.getItem('task-monitoring.admin.session.v2') ?? '{}').accessToken as string);
  for (const path of ['/api/employees', '/api/attendance', '/api/audit-logs', '/api/access-assignments/rdp']) {
    const response = await page.request.get(path, { headers: { Authorization: `Bearer ${token}` } });
    expect(response.status(), path).toBe(403);
  }
});
