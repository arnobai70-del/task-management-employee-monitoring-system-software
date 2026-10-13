import { createRequire } from 'module';
const require = createRequire(import.meta.url);
// Usage: PW=$(npm root -g)/playwright node scripts/audit/status-rail-overlap.mjs http://localhost:5174 <label> <outDir>
// Mocks every /api call with synthetic counts (no backend, no real data) and measures pill overlap.
const { chromium } = require(process.env.PW);
const [,, base, label, outDir] = process.argv;
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
const perms = ['reports.read','employees.read','departments.read','tasks.read','tasks.manage','audit.read','operations.read','operations.manage','attendance.read','attendance.manage','security.alerts.read','presence.read','surveys.read','projects.read','shifts.read','access.assignments.read'];
const exp = Math.floor(Date.now()/1000)+3600;
const token = `${b64({alg:'HS256',typ:'JWT'})}.${b64({sub:'00000000-0000-0000-0000-000000000001',email:'admin@example.test',permission:perms,role:['SuperAdmin'],exp})}.sig`;
const session = { accessToken: token, accessTokenExpiresAtUtc: new Date(exp*1000).toISOString() };
const results = [];
const browser = await chromium.launch();
for (const width of [320, 375, 768, 1024, 1440]) {
  const page = await browser.newPage({ viewport: { width, height: 800 } });
  await page.addInitScript(s => sessionStorage.setItem('task-monitoring.admin.session.v2', s), JSON.stringify(session));
  await page.route('**/hubs/**', r => r.abort());
  await page.route('**/api/**', r => {
    const u = new URL(r.request().url()).pathname;
    const body =
      u.endsWith('/operations/incidents/summary') ? { open: 2, openCritical: 1, openWarning: 1, acknowledged: 0, assigned: 0, resolvedToday: 0 } :
      u.endsWith('/security-alerts/summary') ? { open: 3, acknowledged: 0, escalated: 0, criticalActive: 1, assigned: 0, resolvedToday: 0 } :
      u.endsWith('/admin-notifications/summary') ? { unread: 4, total: 4 } :
      u.endsWith('/departments') ? [] :
      u.endsWith('/follow-ups/mine') ? { generatedAtUtc: new Date().toISOString(), pending: 2, overdue: 1, resolved: 0, totalCount: 0, items: [] } :
      { items: [], page: 1, pageSize: 20, totalCount: 0 };
    return r.fulfill({ json: body });
  });
  await page.goto(`${base}/departments`);
  await page.waitForSelector('h1');
  await page.waitForTimeout(1500);
  const boxes = await page.evaluate(() => {
    const rect = el => { const b = el.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height, text: el.textContent.trim().slice(0, 40) }; };
    const pills = [...document.querySelectorAll('a.status-badge')].map(rect);
    const header = [...document.querySelectorAll('.page-header .eyebrow, .page-header h1, .page-header button, .topbar button, .topbar-title')].map(rect);
    return { pills, header, overflowX: document.documentElement.scrollWidth > window.innerWidth };
  });
  const hit = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  const overlaps = [];
  boxes.pills.forEach((p, i) => {
    boxes.pills.slice(i + 1).forEach(q => hit(p, q) && overlaps.push(`${p.text} x ${q.text}`));
    boxes.header.forEach(h => hit(p, h) && overlaps.push(`${p.text} x ${h.text}`));
  });
  results.push(`${label} ${width}px: pills=${boxes.pills.length} overlaps=${overlaps.length}${overlaps.length ? ' [' + overlaps.join('; ') + ']' : ''} overflowX=${boxes.overflowX}`);
  await page.screenshot({ path: `${outDir}/${label}-${width}.png` });
  await page.close();
}
await browser.close();
console.log(results.join('\n'));
