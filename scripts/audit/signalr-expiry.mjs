// Evidence script: connects to /hubs/realtime with ONE access token (no refresh) and
// reports whether the server keeps the socket open after the token's expiry.
import { createRequire } from 'node:module';
// Run from the repository root after `npm ci` in src/AdminWeb. Uses the Admin Web's own @microsoft/signalr.
const require = createRequire(new URL('../../src/AdminWeb/package.json', import.meta.url));
const signalR = require('@microsoft/signalr');
const base = process.env.API_BASE ?? 'http://127.0.0.1:5080';
const email = process.env.AUDIT_EMAIL, password = process.env.AUDIT_PASSWORD;
if (!email || !password) throw new Error('Set AUDIT_EMAIL and AUDIT_PASSWORD for a NON-production test account.');
const res = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }) });
const auth = await res.json();
console.log(new Date().toISOString(), 'login', res.status, 'accessTokenExpiresAtUtc', auth.accessTokenExpiresAtUtc);
const conn = new signalR.HubConnectionBuilder()
  .withUrl(`${base}/hubs/realtime`, { accessTokenFactory: () => auth.accessToken, transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true })
  .configureLogging(signalR.LogLevel.None).build();
let closedAt = null;
conn.onclose(err => { closedAt = new Date(); console.log(closedAt.toISOString(), 'server closed connection:', err?.message ?? 'no error'); });
await conn.start();
console.log(new Date().toISOString(), 'connected, state =', conn.state);
const waitMs = Number(process.env.WAIT_MS ?? 150000);
await new Promise(r => setTimeout(r, waitMs));
const expired = Date.now() > Date.parse(auth.accessTokenExpiresAtUtc);
console.log(new Date().toISOString(), `after ${waitMs / 1000}s: tokenExpired=${expired} state=${conn.state} closedAt=${closedAt?.toISOString() ?? 'never'}`);
console.log(expired && conn.state === 'Connected' ? 'RESULT: socket still open after access-token expiry (finding reproduced)' : 'RESULT: socket closed after access-token expiry');
await conn.stop().catch(() => {});
process.exit(0);
