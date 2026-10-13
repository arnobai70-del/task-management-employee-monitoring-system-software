// Evidence script: admin-web-like client (token factory that refreshes + automatic reconnect).
// Verifies that after the server closes an expired socket the client reconnects with a fresh token.
import { createRequire } from 'node:module';
// Run from the repository root after `npm ci` in src/AdminWeb. Uses the Admin Web's own @microsoft/signalr.
const require = createRequire(new URL('../../src/AdminWeb/package.json', import.meta.url));
const signalR = require('@microsoft/signalr');
const base = process.env.API_BASE ?? 'http://127.0.0.1:5080';
const email = process.env.AUDIT_EMAIL, password = process.env.AUDIT_PASSWORD;
if (!email || !password) throw new Error('Set AUDIT_EMAIL and AUDIT_PASSWORD for a NON-production test account.');
const post = (path, body) => fetch(`${base}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
let auth = await (await post('/api/auth/login', { email, password })).json();
const tokenFactory = async () => {
  if (Date.parse(auth.accessTokenExpiresAtUtc) - Date.now() < 20000) {
    auth = await (await post('/api/auth/refresh', { refreshToken: auth.refreshToken })).json();
    console.log(new Date().toISOString(), 'refreshed access token, new expiry', auth.accessTokenExpiresAtUtc);
  }
  return auth.accessToken;
};
const conn = new signalR.HubConnectionBuilder()
  .withUrl(`${base}/hubs/realtime`, { accessTokenFactory: tokenFactory, transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true })
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(signalR.LogLevel.None).build();
let reconnects = 0;
conn.onreconnecting(err => console.log(new Date().toISOString(), 'reconnecting:', err?.message ?? 'n/a'));
conn.onreconnected(() => { reconnects++; console.log(new Date().toISOString(), 'reconnected'); });
await conn.start();
console.log(new Date().toISOString(), 'connected');
await new Promise(r => setTimeout(r, Number(process.env.WAIT_MS ?? 150000)));
console.log(new Date().toISOString(), `final state=${conn.state} reconnects=${reconnects}`);
console.log(conn.state === 'Connected' && reconnects > 0 ? 'RESULT: client recovered with a fresh token after server-side expiry close' : 'RESULT: client did not recover');
await conn.stop(); process.exit(0);
