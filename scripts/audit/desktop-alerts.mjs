// Usage: PW=$(npm root -g)/playwright RT_ADMIN_EMAIL=... RT_ADMIN_PASSWORD=... node scripts/audit/desktop-alerts.mjs
// Serves src/AdminWeb/dist on 127.0.0.1:5183 and proxies /api and /hubs to a local SCRATCH API on 127.0.0.1:5080.
// Checks the opt-in desktop alerts: the top-bar toggle, that a new security alert (a failed sign-in burst for a
// fictional address) becomes an operating-system notification while the console is in the background, that none
// is shown while it has focus, and that a browser that blocks notifications shows the blocked state.
import { createRequire } from 'module';
import { readFileSync, existsSync } from 'fs';
import { createServer, request } from 'http';
import { connect } from 'net';
import { join, extname, resolve } from 'path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PW);
const ROOT = resolve(new URL('../..', import.meta.url).pathname);
const DIST = resolve(process.env.DIST ?? join(ROOT, 'src/AdminWeb/dist'));
const API = { host: '127.0.0.1', port: 5080 };
const PORT = 5183;
const WEB = `http://127.0.0.1:${PORT}`;
const ADMIN = { email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD };
if (!ADMIN.email || !ADMIN.password) throw new Error('Set RT_ADMIN_EMAIL and RT_ADMIN_PASSWORD to the local bootstrap admin.');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json' };

const server = createServer((req, res) => {
  if (req.url.startsWith('/api/') || req.url.startsWith('/hubs/')) {
    const upstream = request({ ...API, path: req.url, method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    req.pipe(upstream);
    upstream.on('error', () => { res.writeHead(502); res.end(); });
    return;
  }
  const path = join(DIST, decodeURIComponent(req.url.split('?')[0]));
  const file = path.startsWith(DIST) && existsSync(path) && extname(path) ? path : join(DIST, 'index.html');
  res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
  res.end(readFileSync(file));
});
server.on('upgrade', (req, socket, head) => {
  const upstream = connect(API.port, API.host, () => {
    upstream.write(`${req.method} ${req.url} HTTP/1.1\r\n${Object.entries(req.headers).map(([k, v]) => `${k}: ${v}`).join('\r\n')}\r\n\r\n`);
    upstream.write(head);
    upstream.pipe(socket); socket.pipe(upstream);
  });
  upstream.on('error', () => socket.destroy());
  socket.on('error', () => upstream.destroy());
});
await new Promise(r => server.listen(PORT, '127.0.0.1', r));

// Headless Chromium cannot show OS notifications and reports the permission as denied, so window.Notification
// is replaced by a recorder with the permission the check needs. This checks what the page asks the browser to
// show, not the operating system's own display. hasFocus is controlled so "in the background" can be simulated.
const recorder = permission => {
  window.__shown = [];
  window.__focused = true;
  // A real browser remembers a granted permission across reloads; sessionStorage stands in for that here.
  let current = sessionStorage.getItem('audit-notification-permission') ?? permission;
  class Recorded {
    constructor(title, options) { window.__shown.push({ title, body: options?.body, tag: options?.tag }); }
    close() {}
    static get permission() { return current; }
    static requestPermission() {
      if (current === 'default') current = 'granted';
      sessionStorage.setItem('audit-notification-permission', current);
      return Promise.resolve(current);
    }
  }
  window.Notification = Recorded;
  document.hasFocus = () => window.__focused;
};

const browser = await chromium.launch();
const log = line => console.log(line);
const toggle = page => page.locator('.desktop-alerts-topbar');

async function signIn(context, permission) {
  const page = await context.newPage();
  await page.addInitScript(recorder, permission);
  await page.goto(`${WEB}/login`);
  await page.getByLabel(/email/i).fill(ADMIN.email);
  await page.getByLabel(/password/i).fill(ADMIN.password);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 30000 });
  await page.goto(`${WEB}/dashboard`);
  await toggle(page).waitFor({ timeout: 15000 });
  await page.waitForTimeout(3000);
  return page;
}

async function failedSignIns(email, count) {
  for (let i = 0; i < count; i++) {
    const r = await fetch(`http://${API.host}:${API.port}/api/auth/web/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email, password: 'wrong-password-for-a-fictional-user' })
    });
    await r.arrayBuffer();
  }
}

async function waitForAlert(page, sinceCount, timeoutMs) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    const toast = await page.locator('.realtime-toast').filter({ hasText: /security alert/i }).count();
    if (toast > 0) return { toast: true, after: Math.round((Date.now() - t0) / 1000), shown: await page.evaluate(() => window.__shown) };
    await page.waitForTimeout(1000);
  }
  return { toast: false, after: null, shown: await page.evaluate(() => window.__shown) };
}

// 1. Not yet asked: clicking the toggle asks for permission (granted here) and turns alerts on; then, then a security alert arrives while the window is in the background.
{
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const page = await signIn(context, 'default');
  log(`allowed: toggle before click "${await toggle(page).innerText()}" (aria-pressed=${await toggle(page).getAttribute('aria-pressed')})`);
  await toggle(page).click();
  await page.waitForTimeout(500);
  log(`allowed: toggle after click "${await toggle(page).innerText()}" (aria-pressed=${await toggle(page).getAttribute('aria-pressed')}); stored=${await page.evaluate(() => localStorage.getItem('taskmonitoring-admin-desktop-alerts'))}`);

  await page.evaluate(() => { window.__focused = false; });
  await failedSignIns(`burst-a-${Date.now()}@example.invalid`, 5);
  const background = await waitForAlert(page, 0, 150000);
  log(`background window: in-page toast ${background.toast ? `after ${background.after} s` : 'not seen in 150 s'}; desktop notifications ${background.shown.length}: ${JSON.stringify(background.shown)}`);

  await page.reload(); await toggle(page).waitFor(); await page.waitForTimeout(3000);
  log(`after reload: toggle "${await toggle(page).innerText()}"`);
  await page.evaluate(() => { window.__focused = true; });
  await failedSignIns(`burst-b-${Date.now()}@example.invalid`, 5);
  const focused = await waitForAlert(page, 0, 150000);
  log(`focused window (control): in-page toast ${focused.toast ? `after ${focused.after} s` : 'not seen in 150 s'}; desktop notifications ${focused.shown.length}`);

  await toggle(page).click(); await page.waitForTimeout(500);
  log(`turned off: toggle "${await toggle(page).innerText()}"; stored=${await page.evaluate(() => localStorage.getItem('taskmonitoring-admin-desktop-alerts'))}`);
  await context.close();
}

// 2. Blocked: the browser denies notifications for the site.
{
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const page = await signIn(context, 'denied');
  log(`blocked: toggle "${await toggle(page).innerText()}", disabled=${await toggle(page).isDisabled()}, hint="${await toggle(page).getAttribute('title')}"`);
  await context.close();
}

await browser.close();
server.close();
