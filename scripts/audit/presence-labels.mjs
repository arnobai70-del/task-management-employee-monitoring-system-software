import { createRequire } from 'module';
const require = createRequire(import.meta.url);
// Usage: PW=$(npm root -g)/playwright node scripts/audit/presence-labels.mjs http://localhost:5176 <out.png>
// Every /api call is mocked with fictional presence rows; no backend or real data.
const { chromium } = require(process.env.PW);
const [,, base, out] = process.argv;
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
const exp = Math.floor(Date.now()/1000)+3600;
const token = `${b64({alg:'HS256'})}.${b64({sub:'00000000-0000-0000-0000-000000000001',email:'admin@example.test',permission:['reports.read','presence.read'],role:['SuperAdmin'],exp})}.sig`;
const session = JSON.stringify({ accessToken: token, accessTokenExpiresAtUtc: new Date(exp*1000).toISOString() });
const person = (code, name, isOnline, workState) => ({ employeeId: crypto.randomUUID(), employeeCode: code, fullName: name, departmentName: 'Operations', isOnline, workState, lastSeenAtUtc: new Date().toISOString(), clientKind: 'Desktop', clientVersion: '1.0.0', workSessionStartedAtUtc: null });
const presence = { items: [person('E-1','Fictional One',true,'Working'), person('E-2','Fictional Two',true,'OnBreak'), person('E-3','Fictional Three',true,'Idle'), person('E-4','Fictional Four',false,'Offline')], page: 1, pageSize: 100, totalCount: 4 };
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1366, height: 1400 } });
await page.addInitScript(s => sessionStorage.setItem('task-monitoring.admin.session.v2', s), session);
await page.route('**/hubs/**', r => r.abort());
await page.route('**/api/**', r => {
  const u = new URL(r.request().url()).pathname;
  if (u === '/api/presence') return r.fulfill({ json: presence });
  if (u === '/api/reports/dashboard') return r.fulfill({ json: { generatedAtUtc: new Date().toISOString(), from: '2026-10-14', to: '2026-10-14', workforce: { activeEmployees: 4, activeDepartments: 1 }, attendance: { sessions: 2, distinctEmployees: 2, completedSessions: 0, openSessions: 2, lateSessions: 0, earlyLeaveSessions: 0, totalBreakMinutes: 0 }, projects: { planning: 0, active: 0, onHold: 0, completed: 0, archived: 0, overdue: 0 }, tasks: { open: 0, toDo: 0, inProgress: 0, blocked: 0, done: 0, cancelled: 0, overdue: 0, unassignedOpen: 0, completedInPeriod: 0 }, surveys: { publishedForms: 0, activeAssignments: 0, pendingReview: 0, approvedAssignments: 0, rejectedAssignments: 0, overdueAssignments: 0, submittedInPeriod: 0, reviewedInPeriod: 0 } } });
  if (u === '/api/reports/attendance/daily' || u === '/api/reports/workload') return r.fulfill({ json: [] });
  return r.fulfill({ status: 503, json: { code: 'mock', message: 'mock' } });
});
page.on('pageerror', e => console.log('pageerror', e.message));
await page.goto(`${base}/dashboard`);
await page.getByText('Fictional Four').waitFor({ timeout: 15000 });
const table = page.locator('table', { hasText: 'Fictional One' });
const rows = await table.locator('tbody tr').allInnerTexts();
const summary = await page.getByText(/connected · .* on shift|online · .* working/).first().innerText();
console.log('summary:', summary);
rows.forEach(r => console.log('row:', r.replace(/\s+/g, ' ').trim()));
await table.screenshot({ path: out });
await browser.close();
