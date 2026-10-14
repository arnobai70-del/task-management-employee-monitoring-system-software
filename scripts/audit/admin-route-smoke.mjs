import { createRequire } from 'module';
const require = createRequire(import.meta.url);
// Usage: PW=$(npm root -g)/playwright node scripts/audit/admin-route-smoke.mjs http://localhost:5175 <label>
// Serves no real data: every /api call returns 503, the hub is aborted, the user is fictional.
const { chromium } = require(process.env.PW);
const [,, base, label] = process.argv;
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
const perms = ['reports.read','employees.read','departments.read','tasks.read','tasks.manage','audit.read','attendance.read','attendance.manage','presence.read','surveys.read','projects.read','shifts.read','access.assignments.read'];
const exp = Math.floor(Date.now()/1000)+3600;
const token = `${b64({alg:'HS256',typ:'JWT'})}.${b64({sub:'00000000-0000-0000-0000-000000000001',email:'admin@example.test',permission:perms,role:['SuperAdmin'],exp})}.sig`;
const session = JSON.stringify({ accessToken: token, accessTokenExpiresAtUtc: new Date(exp*1000).toISOString() });
const routes = ['/dashboard','/productivity','/sla-analytics','/operations','/security-alerts','/employees','/departments','/shifts','/attendance','/projects','/tasks','/website-work','/follow-ups','/surveys','/access/rdp','/access/ip','/access/websites','/audit-logs','/forbidden','/nope'];
const browser = await chromium.launch();
const out = [];
// initial payload on the sign-in page (no session)
{
  const page = await browser.newPage();
  let js = 0; page.on('response', async r => { if (r.url().endsWith('.js')) js += (await r.body()).length; });
  await page.goto(`${base}/login`); await page.waitForSelector('form'); await page.waitForTimeout(500);
  out.push(`${label} /login JS bytes (uncompressed) = ${js}`);
  await page.close();
}
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const errors = [];
page.on('pageerror', e => errors.push(e.message));
await page.addInitScript(s => sessionStorage.setItem('task-monitoring.admin.session.v2', s), session);
await page.route('**/hubs/**', r => r.abort());
await page.route('**/api/**', r => r.fulfill({ status: 503, json: { code: 'mock_unavailable', message: 'mock' } }));
for (const route of routes) {
  errors.length = 0;
  await page.goto(`${base}${route}`);
  let h1 = '';
  try { await page.waitForSelector('h1', { timeout: 8000 }); h1 = (await page.textContent('h1')).trim(); } catch { h1 = '(no h1)'; }
  out.push(`${label} ${route} -> url=${new URL(page.url()).pathname} h1="${h1}" pageErrors=${errors.length}`);
}
await browser.close();
console.log(out.join('\n'));
