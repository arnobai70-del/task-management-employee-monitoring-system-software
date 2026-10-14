// Usage: PW=$(npm root -g)/playwright node scripts/audit/degradation.mjs [shotDir]   (ONLY=baseline,errors,offline,slow,hidden to pick;
//        DIST=<built Admin Web dir> to test another build, for example an older commit as a control)
// Serves src/AdminWeb/dist on 127.0.0.1:5182 and proxies /api and /hubs (WebSocket upgrades too) to a local API on
// 127.0.0.1:5080, like Caddy. Signs in with RT_ADMIN_EMAIL/RT_ADMIN_PASSWORD (local scratch admin, fictional data) and
// checks how the Admin Web behaves under HTTP 429, HTTP 500, a lost connection and a slow link: what the page shows,
// how many API requests it sends while the server is failing, and whether it recovers without a manual reload.
import { createRequire } from 'module';
import { readFileSync, existsSync, mkdirSync } from 'fs';
import { createServer, request } from 'http';
import { connect } from 'net';
import { join, extname, resolve } from 'path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PW);
const [,, shotDir] = process.argv;
const ROOT = resolve(new URL('../..', import.meta.url).pathname);
const DIST = resolve(process.env.DIST ?? join(ROOT, 'src/AdminWeb/dist'));
const API = { host: '127.0.0.1', port: 5080 };
const PORT = 5182;
const WEB = `http://127.0.0.1:${PORT}`;
const ADMIN = { email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD };
if (!ADMIN.email || !ADMIN.password) throw new Error('Set RT_ADMIN_EMAIL and RT_ADMIN_PASSWORD to the local bootstrap admin.');
if (shotDir) mkdirSync(shotDir, { recursive: true });
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

const browser = await chromium.launch();
const results = [];
const log = line => { results.push(line); console.log(line); };
const isApi = url => new URL(url).pathname.startsWith('/api/');
const apiPath = url => new URL(url).pathname.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, ':id');

async function signedInPage(context) {
  const page = await context.newPage();
  const state = { apiRequests: [], failed: 0, pageErrors: [], sockets: [] };
  page.on('request', r => { if (isApi(r.url())) state.apiRequests.push({ at: Date.now(), path: apiPath(r.url()) }); });
  page.on('requestfailed', r => { if (isApi(r.url())) state.failed++; });
  page.on('pageerror', e => state.pageErrors.push(e.message.slice(0, 160)));
  page.on('websocket', ws => { if (ws.url().includes('/hubs/realtime')) { const s = { at: Date.now(), frames: 0, closed: false }; state.sockets.push(s); ws.on('framereceived', () => { s.frames++; }); ws.on('close', () => { s.closed = true; }); } });
  await page.goto(`${WEB}/login`);
  await page.getByLabel(/email/i).fill(ADMIN.email);
  await page.getByLabel(/password/i).fill(ADMIN.password);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 30000 });
  await page.waitForTimeout(2500);
  return { page, state };
}
const countSince = (state, t) => state.apiRequests.filter(r => r.at >= t).length;
const topPaths = (state, t) => Object.entries(state.apiRequests.filter(r => r.at >= t).reduce((m, r) => ({ ...m, [r.path]: (m[r.path] ?? 0) + 1 }), {})).sort((a, b) => b[1] - a[1]).slice(0, 4).map(([p, n]) => `${p}×${n}`).join(', ');
const visibleAlerts = page => page.evaluate(() => [...document.querySelectorAll('.error-panel, [role="alert"], .notice-error')]
  .map(e => e.textContent.trim().replace(/\s+/g, ' ')).filter(Boolean).slice(0, 3).map(t => t.slice(0, 120)));
const mainText = page => page.evaluate(() => (document.querySelector('main')?.innerText ?? document.body.innerText).trim().length);
const shot = async (page, name) => { if (shotDir) await page.screenshot({ path: join(shotDir, `${name}.png`) }); };

const only = (process.env.ONLY ?? 'baseline,errors,offline,slow').split(',');
const headerStatus = page => page.locator('.topbar-title span').innerText().catch(() => '');
const ROUTES = ['/dashboard', '/employees', '/attendance', '/audit-logs'];
const WINDOW_MS = 30000;

// 1. Idle baseline: how many API calls a healthy page makes in 30 s.
if (only.includes('baseline')) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const { page, state } = await signedInPage(context);
  for (const route of ROUTES) {
    await page.goto(WEB + route); await page.waitForTimeout(2000);
    const t = Date.now(); await page.waitForTimeout(WINDOW_MS);
    log(`baseline ${route}: ${countSince(state, t)} API requests in 30 s idle (${topPaths(state, t) || 'none'}); page errors ${state.pageErrors.length}`);
  }
  await context.close();
}

// 2/3. Server answers 429 or 500 to every API call except token refresh, for 30 s, then recovers.
if (only.includes('errors')) for (const [status, body, headers] of [
  [429, { code: 'rate_limited', message: 'Too many requests.' }, { 'retry-after': '30' }],
  [500, { code: 'server_error', message: 'An unexpected error occurred.' }, {}]]) {
  // One sign-in per status: the auth endpoints allow 10 requests per minute per client.
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const { page, state } = await signedInPage(context);
  for (const route of ROUTES) {
    await page.goto(WEB + route); await page.waitForTimeout(2000);
    const handler = r => isApi(r.request().url()) && !r.request().url().includes('/api/auth/')
      ? r.fulfill({ status, headers, json: body }) : r.continue();
    await page.route('**/api/**', handler);
    const t = Date.now();
    await page.reload(); await page.waitForTimeout(WINDOW_MS);
    const during = countSince(state, t);
    const alerts = await visibleAlerts(page);
    const stillSignedIn = !new URL(page.url()).pathname.startsWith('/login');
    await shot(page, `${status}${route.replace(/\//g, '_')}`);
    await page.unroute('**/api/**', handler);
    const r0 = Date.now();
    // Recovery without reload: wait for the next automatic request to succeed, up to 60 s.
    const auto = await page.waitForResponse(r => isApi(r.url()) && r.status() < 400 && !r.url().includes('/api/auth/'), { timeout: 60000 }).then(() => Date.now() - r0).catch(() => null);
    const textAfter = await mainText(page);
    log(`${status} ${route}: ${during} API requests in 30 s while failing (${topPaths(state, t)}); signed in: ${stillSignedIn}; message: ${JSON.stringify(alerts)}; page errors: ${state.pageErrors.length}; auto recovery: ${auto === null ? 'none in 60 s' : `${Math.round(auto / 1000)} s`}; main text after: ${textAfter} chars`);
  }
  await context.close();
}

// 4. Connection lost for 30 s, then back.
if (only.includes('offline')) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const { page, state } = await signedInPage(context);
  await page.goto(WEB + '/dashboard'); await page.waitForTimeout(3000);
  const headerBefore = await headerStatus(page);
  const socketsBefore = state.sockets.length;
  const failedBefore = state.failed;
  await context.setOffline(true);
  const t = Date.now(); await page.waitForTimeout(WINDOW_MS);
  const alerts = await visibleAlerts(page);
  const headerOffline = await headerStatus(page);
  await shot(page, 'offline_dashboard');
  log(`offline /dashboard: ${countSince(state, t)} API requests attempted in 30 s (${state.failed - failedBefore} failed); message: ${JSON.stringify(alerts)}; page errors: ${state.pageErrors.length}; signed in: ${!new URL(page.url()).pathname.startsWith('/login')}; header before: "${headerBefore}", while offline: "${headerOffline}"`);
  await context.setOffline(false);
  const r0 = Date.now();
  let reconnected = null;
  for (let i = 0; i < 90; i++) {
    await page.waitForTimeout(1000);
    if (state.sockets.length > socketsBefore && state.sockets.at(-1).frames > 0) { reconnected = Date.now() - r0; break; }
  }
  const okAfter = await page.waitForResponse(r => isApi(r.url()) && r.status() < 400, { timeout: 60000 }).then(() => true).catch(() => false);
  log(`online again: realtime reconnected ${reconnected === null ? 'not within 90 s' : `after ${Math.round(reconnected / 1000)} s`}; API data request succeeded: ${okAfter}; still signed in: ${!new URL(page.url()).pathname.startsWith('/login')}; page errors: ${state.pageErrors.length}; header now: "${await headerStatus(page)}"`);
  await context.close();
}

// 6. Hidden tab: the page is signed in and visible, then hidden for 60 s (as if the admin switched tabs), then shown.
if (only.includes('hidden')) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const { page, state } = await signedInPage(context);
  const setHidden = hidden => page.evaluate(h => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => h });
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => (h ? 'hidden' : 'visible') });
    document.dispatchEvent(new Event('visibilitychange'));
  }, hidden);
  for (const route of ['/dashboard', '/website-work', '/operations']) {
    await page.goto(WEB + route); await page.waitForTimeout(3000);
    // Each window is summarised as soon as it ends, so later requests are not counted in it.
    const windowSummary = t => `${countSince(state, t)} API requests (${topPaths(state, t) || 'none'})`;
    const v = Date.now(); await page.waitForTimeout(WINDOW_MS);
    const visible = windowSummary(v);
    await setHidden(true);
    const h = Date.now(); await page.waitForTimeout(2 * WINDOW_MS);
    const hidden = windowSummary(h);
    await setHidden(false);
    const s0 = Date.now(); await page.waitForTimeout(3000);
    log(`hidden ${route}: visible 30 s ${visible}; hidden 60 s ${hidden}; first 3 s after shown ${windowSummary(s0)}; page errors ${state.pageErrors.length}`);
  }
  await context.close();
}

// 5. Slow link (about 400 kbit/s down, 400 ms latency): time from sign-in click to a rendered dashboard.
if (only.includes('slow')) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 860 } });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 400, downloadThroughput: 50 * 1024, uploadThroughput: 25 * 1024 });
  const t0 = Date.now();
  await page.goto(`${WEB}/login`);
  await page.getByLabel(/email/i).waitFor({ timeout: 120000 });
  const loginReady = Date.now() - t0;
  await page.getByLabel(/email/i).fill(ADMIN.email);
  await page.getByLabel(/password/i).fill(ADMIN.password);
  const t1 = Date.now();
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 120000 });
  await page.waitForFunction(() => (document.querySelector('main')?.innerText ?? '').trim().length > 200, null, { timeout: 120000 }).catch(() => {});
  const dashReady = Date.now() - t1;
  await shot(page, 'slow_dashboard');
  log(`slow link (400 ms, 50 KB/s): sign-in page usable after ${(loginReady / 1000).toFixed(1)} s; dashboard content after sign-in ${(dashReady / 1000).toFixed(1)} s`);
  await context.close();
}

await browser.close();
server.close();
