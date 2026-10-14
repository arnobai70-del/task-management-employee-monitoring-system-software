// Usage: RT_ADMIN_EMAIL=... RT_ADMIN_PASSWORD=... node scripts/audit/api-latency.mjs http://127.0.0.1:5080 [requests] [concurrency]
// Measures p50/p95/p99 latency of read-heavy admin endpoints against a SCRATCH API seeded with
// scripts/audit/perf-seed.sql. Credentials come from the environment only.
const [,, base = 'http://127.0.0.1:5080', requestsArg = '60', concurrencyArg = '4'] = process.argv;
const requests = Number(requestsArg);
const concurrency = Number(concurrencyArg);
const day = offset => new Date(Date.now() + offset * 86400000).toISOString().slice(0, 10);
const iso = offset => new Date(Date.now() + offset * 86400000).toISOString();

const login = await fetch(`${base}/api/auth/login`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD })
});
if (!login.ok) throw new Error(`login failed: ${login.status}`);
const { accessToken } = await login.json();
const headers = { authorization: `Bearer ${accessToken}` };

const endpoints = [
  ['dashboard', '/api/reports/dashboard'],
  ['attendance daily 30d', `/api/reports/attendance/daily?from=${day(-30)}&to=${day(-1)}`],
  ['attendance daily 60d', `/api/reports/attendance/daily?from=${day(-60)}&to=${day(-1)}`],
  ['workload', '/api/reports/workload'],
  ['projects report', '/api/reports/projects'],
  ['website-work productivity 30d', `/api/reports/website-work/productivity?from=${day(-30)}&to=${day(-1)}`],
  ['follow-up SLA 30d', `/api/reports/website-work/follow-up-sla?from=${day(-30)}&to=${day(-1)}`],
  ['attendance list p1', `/api/attendance?from=${day(-60)}&to=${day(-1)}&page=1&pageSize=50`],
  ['attendance list p500', `/api/attendance?from=${day(-60)}&to=${day(-1)}&page=500&pageSize=50`],
  ['employees p1', '/api/employees?page=1&pageSize=50'],
  ['employees search', '/api/employees?search=EMPLOYEE%2099&page=1&pageSize=50'],
  ['tasks p1', '/api/tasks?page=1&pageSize=50'],
  ['presence', '/api/presence?page=1&pageSize=100'],
  ['audit logs p1', '/api/audit-logs?page=1&pageSize=50'],
  ['audit logs search', '/api/audit-logs?search=checked_in&page=1&pageSize=50'],
  ['audit logs p2000', '/api/audit-logs?page=2000&pageSize=50'],
  ['monitoring activity 7d', `/api/monitoring/activity?fromUtc=${iso(-7)}&page=1&pageSize=50`],
  ['security dashboard 24h', '/api/security-observability/dashboard?windowHours=24'],
  ['security dashboard 168h', '/api/security-observability/dashboard?windowHours=168'],
  ['operations overview', '/api/operations/overview'],
];

const pct = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.ceil(p / 100 * sorted.length) - 1)];
const rows = [];
for (const [name, path] of endpoints) {
  const times = [];
  let status = 0, bytes = 0;
  const one = async () => {
    const started = performance.now();
    const response = await fetch(base + path, { headers });
    const body = await response.arrayBuffer();
    times.push(performance.now() - started);
    status = response.status; bytes = body.byteLength;
  };
  for (let i = 0; i < 3; i++) await one();
  times.length = 0;
  let next = 0;
  await Promise.all(Array.from({ length: concurrency }, async () => { while (next++ < requests) await one(); }));
  times.sort((a, b) => a - b);
  rows.push({ name, status, kb: (bytes / 1024).toFixed(1), p50: pct(times, 50).toFixed(0), p95: pct(times, 95).toFixed(0), p99: pct(times, 99).toFixed(0), max: times.at(-1).toFixed(0) });
}
console.log(`requests=${requests} concurrency=${concurrency} base=${base}`);
console.log('endpoint'.padEnd(32), 'status', 'KB'.padStart(8), 'p50ms'.padStart(7), 'p95ms'.padStart(7), 'p99ms'.padStart(7), 'maxms'.padStart(7));
for (const r of rows) console.log(r.name.padEnd(32), String(r.status).padEnd(6), r.kb.padStart(8), r.p50.padStart(7), r.p95.padStart(7), r.p99.padStart(7), r.max.padStart(7));
