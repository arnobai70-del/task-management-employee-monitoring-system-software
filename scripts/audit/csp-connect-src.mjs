// Usage: PW=$(npm root -g)/playwright node scripts/audit/csp-connect-src.mjs <label> [csp]
// Serves src/AdminWeb/dist on 127.0.0.1:5181 with the Content-Security-Policy from deploy/production/Caddyfile (or the
// optional csp argument) and proxies /api and /hubs, WebSocket upgrades included, to a local API on 127.0.0.1:5080,
// the same single-origin layout Caddy uses. Signs in with RT_ADMIN_EMAIL/RT_ADMIN_PASSWORD (a local scratch admin),
// opens the dashboard and reports CSP violations and whether the realtime hub connected over a WebSocket.
import { createRequire } from 'module';
import { readFileSync, existsSync } from 'fs';
import { createServer, request } from 'http';
import { connect } from 'net';
import { join, extname, resolve } from 'path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PW);
const [,, label = 'check', cspArg] = process.argv;
const ROOT = resolve(new URL('../..', import.meta.url).pathname);
const DIST = join(ROOT, 'src/AdminWeb/dist');
const API = { host: '127.0.0.1', port: 5080 };
const PORT = 5181;
const ADMIN = { email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD };
if (!ADMIN.email || !ADMIN.password) throw new Error('Set RT_ADMIN_EMAIL and RT_ADMIN_PASSWORD to the local bootstrap admin.');
const csp = cspArg ?? readFileSync(join(ROOT, 'deploy/production/Caddyfile'), 'utf8').match(/Content-Security-Policy "([^"]+)"/)[1];
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json', '.woff2': 'font/woff2' };

const server = createServer((req, res) => {
  if (req.url.startsWith('/api/') || req.url.startsWith('/hubs/')) {
    const upstream = request({ ...API, path: req.url, method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    req.pipe(upstream);
    upstream.on('error', () => { res.writeHead(502); res.end(); });
    return;
  }
  const path = join(DIST, decodeURIComponent(req.url.split('?')[0]));
  const file = path.startsWith(DIST) && existsSync(path) && extname(path) ? path : join(DIST, 'index.html');
  res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream', 'content-security-policy': csp });
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
const page = await browser.newPage({ viewport: { width: 1366, height: 860 } });
const sockets = []; const violations = []; const consoleErrors = [];
page.on('websocket', ws => { if (ws.url().includes('/hubs/realtime')) { const s = { url: ws.url().replace(/access_token=[^&]+/, 'access_token=<redacted>'), frames: 0 }; sockets.push(s); ws.on('framereceived', () => { s.frames++; }); } });
page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text().replace(/access_token=[^&\s]+/g, 'access_token=<redacted>').slice(0, 200)); });
await page.exposeFunction('reportViolation', v => violations.push(v));
await page.addInitScript(() => document.addEventListener('securitypolicyviolation', e => window.reportViolation(`${e.violatedDirective} blocked ${e.blockedURI}`)));
await page.goto(`http://127.0.0.1:${PORT}/login`);
await page.getByLabel(/email/i).fill(ADMIN.email);
await page.getByLabel(/password/i).fill(ADMIN.password);
await page.getByRole('button', { name: /sign in|log in/i }).click();
await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 15000 }).catch(() => {});
await page.waitForTimeout(4000);
const signedIn = !new URL(page.url()).pathname.startsWith('/login');
await browser.close();
server.close();

const wsOk = sockets.some(s => s.frames > 0);
console.log(`[${label}] csp connect-src: ${csp.match(/connect-src [^;]+/)?.[0]}`);
console.log(`[${label}] signed in: ${signedIn}`);
console.log(`[${label}] realtime websockets: ${sockets.length} (with frames: ${sockets.filter(s => s.frames > 0).length}) ${sockets.map(s => s.url).join(' ')}`);
console.log(`[${label}] csp violations: ${violations.length}${violations.length ? '\n  ' + [...new Set(violations)].join('\n  ') : ''}`);
console.log(`[${label}] console errors: ${consoleErrors.length}${consoleErrors.length ? '\n  ' + [...new Set(consoleErrors)].slice(0, 5).join('\n  ') : ''}`);
console.log(`[${label}] RESULT: ${signedIn && wsOk && violations.length === 0 ? 'PASS' : 'FAIL'}`);
