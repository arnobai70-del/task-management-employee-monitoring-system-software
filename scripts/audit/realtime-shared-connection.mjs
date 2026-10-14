// Usage: PW=$(npm root -g)/playwright node scripts/audit/realtime-shared-connection.mjs http://localhost:5173 <label> <outDir> [waitExpiry]
// Needs a local API on 127.0.0.1:5080 with a scratch database, RT_ADMIN_EMAIL/RT_ADMIN_PASSWORD for its bootstrap admin, and
// Jwt__AccessTokenMinutes=1. Creates one fictional employee, project and two website-work targets.
import { createRequire } from 'module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PW);
const [,, web, label, outDir, waitExpiry] = process.argv;
const API = 'http://127.0.0.1:5080';
const ADMIN = { email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD };
if (!ADMIN.email || !ADMIN.password) throw new Error('Set RT_ADMIN_EMAIL and RT_ADMIN_PASSWORD to the local bootstrap admin.');
const EMP = { email: `rt-employee-${label}@example.test`, password: `Rt-${crypto.randomUUID()}` };
async function call(method, path, token, body) {
  const r = await fetch(API + path, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${text}`);
  return text ? JSON.parse(text) : null;
}
const login = async u => (await call('POST', '/api/auth/login', null, u)).accessToken;
let admin = await login(ADMIN);
const roles = await call('GET', '/api/roles', admin);
const employeeRole = (roles.items ?? roles).find(r => r.name === 'Employee');
const emp = await call('POST', '/api/employees', admin, { employeeCode: `RT-${label}`, fullName: `Realtime Check ${label}`, email: EMP.email, password: EMP.password, jobTitle: 'Website operator', roleIds: [employeeRole.id] });
const project = await call('POST', '/api/projects', admin, { code: `RT${label}`.slice(0, 20), name: `Realtime check ${label}`, status: 'Active' });
await call('PUT', `/api/projects/${project.id}/members`, admin, { employeeId: emp.id, role: 'Member' });
const work = async title => (await call('POST', '/api/website-work', admin, { projectId: project.id, employeeId: emp.id, title, url: 'https://example.com/rt', priority: 'Normal' })).id;
const w1 = await work('First realtime target'); const w2 = await work('Second realtime target');
const empToken = await login(EMP);

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1366, height: 860 } });
const sockets = []; const consoleErrors = [];
page.on('websocket', ws => { if (ws.url().includes('/hubs/realtime')) { const s = { url: ws.url(), open: true }; sockets.push(s); ws.on('close', () => { s.open = false; }); } });
page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text().slice(0, 160)); });
await page.goto(`${web}/login`);
await page.getByLabel(/email/i).fill(ADMIN.email);
await page.getByLabel(/password/i).fill(ADMIN.password);
await page.getByRole('button', { name: /sign in|log in/i }).click();
await page.waitForURL(u => !u.pathname.startsWith('/login'));
await page.waitForTimeout(3000);
const log = [];
const openCount = () => sockets.filter(s => s.open).length;
log.push(`after sign-in (${new URL(page.url()).pathname}): hub sockets created=${sockets.length} open=${openCount()}`);
for (const name of ['Attendance', 'Website Work', 'Departments', 'Dashboard']) {
  await page.locator('nav').getByRole('link', { name: new RegExp(name + '$') }).click();
  await page.waitForTimeout(1500);
  log.push(`after navigating to ${name}: created=${sockets.length} open=${openCount()}`);
}
await call('POST', `/api/me/website-work/${w1}/start`, empToken);
await call('POST', `/api/me/website-work/${w1}/submit`, empToken);
const toast1 = await page.getByText('Work submitted for review').first().waitFor({ timeout: 10000 }).then(() => true, () => false);
log.push(`websiteWorkSubmitted toast shown: ${toast1}`);
await page.screenshot({ path: `${outDir}/${label}-toast.png` });
if (waitExpiry) {
  await page.waitForTimeout(80000); // access tokens live 1 minute here: the server closes the hub, the client reconnects
  const emp2 = await login(EMP);
  await call('POST', `/api/me/website-work/${w2}/start`, emp2);
  await call('POST', `/api/me/website-work/${w2}/submit`, emp2);
  const toast2 = await page.getByText('Second realtime target').first().waitFor({ timeout: 15000 }).then(() => true, () => false);
  log.push(`after 80 s (token expiry): created=${sockets.length} open=${openCount()}; second submission reached the page: ${toast2}`);
}
await page.getByRole('button', { name: 'Sign out' }).click();
await page.waitForURL(u => u.pathname.startsWith('/login'));
await page.waitForTimeout(1500);
log.push(`after sign-out: open=${openCount()}`);
log.push(`console errors: ${consoleErrors.length ? consoleErrors.join(' | ') : 'none'}`);
await browser.close();
console.log(log.map(l => `${label}: ${l}`).join('\n'));
