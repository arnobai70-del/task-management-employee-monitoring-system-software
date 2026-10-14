// Usage: RT_ADMIN_EMAIL=... RT_ADMIN_PASSWORD=... node scripts/audit/long-range.mjs http://127.0.0.1:5080 [runs]
// Calls every date-ranged or paged admin endpoint at its largest allowed range and just past it, against a SCRATCH API
// seeded with scripts/audit/perf-seed.sql and scripts/audit/perf-extend-history.sql. Reports status, time and size,
// and whether over-limit requests are refused. Credentials come from the environment only.
const [,, base = 'http://127.0.0.1:5080', runsArg = '5'] = process.argv;
const runs = Number(runsArg);
const day = offset => new Date(Date.now() + offset * 86400000).toISOString().slice(0, 10);
const iso = offset => new Date(Date.now() + offset * 86400000).toISOString();

const login = await fetch(`${base}/api/auth/login`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD })
});
if (!login.ok) throw new Error(`login failed: ${login.status}`);
const { accessToken } = await login.json();
const headers = { authorization: `Bearer ${accessToken}` };
const employees = await (await fetch(`${base}/api/employees?page=1&pageSize=1`, { headers })).json();
const employeeId = employees.items[0].id;

// [name, path, expected status]
const checks = [
  ['dashboard', '/api/reports/dashboard', 200],
  ['attendance daily 366d', `/api/reports/attendance/daily?from=${day(-366)}&to=${day(-1)}`, 200],
  ['attendance daily 367d (over limit)', `/api/reports/attendance/daily?from=${day(-368)}&to=${day(-1)}`, 400],
  ['productivity 366d', `/api/reports/website-work/productivity?from=${day(-366)}&to=${day(-1)}`, 200],
  ['productivity 367d (over limit)', `/api/reports/website-work/productivity?from=${day(-368)}&to=${day(-1)}`, 400],
  ['timeline 366d', `/api/reports/website-work/productivity/${employeeId}/timeline?from=${day(-366)}&to=${day(-1)}`, 200],
  ['follow-up SLA 366d', `/api/reports/website-work/follow-up-sla?from=${day(-366)}&to=${day(-1)}`, 200],
  ['follow-up SLA 367d (over limit)', `/api/reports/website-work/follow-up-sla?from=${day(-368)}&to=${day(-1)}`, 400],
  ['attendance list 366d p1', `/api/attendance?from=${day(-366)}&to=${day(-1)}&page=1&pageSize=100`, 200],
  ['attendance list 366d last page', `/api/attendance?from=${day(-366)}&to=${day(-1)}&page=10000&pageSize=100`, 200],
  ['attendance list 367d (over limit)', `/api/attendance?from=${day(-368)}&to=${day(-1)}&page=1&pageSize=100`, 400],
  ['attendance list pageSize 100000 (clamped)', `/api/attendance?from=${day(-30)}&to=${day(-1)}&page=1&pageSize=100000`, 200],
  ['employees p1', '/api/employees?page=1&pageSize=100', 200],
  ['employees search', '/api/employees?search=EMPLOYEE%20100999&page=1&pageSize=50', 200],
  ['presence', '/api/presence?page=1&pageSize=100', 200],
  ['audit logs 366d p1', `/api/audit-logs?fromUtc=${iso(-366)}&toUtc=${iso(0)}&page=1&pageSize=100`, 200],
  ['audit logs page 1000000', '/api/audit-logs?page=1000000&pageSize=100', 200],
  ['audit export 366d', `/api/security-observability/audit-export.csv?fromUtc=${iso(-366)}&toUtc=${iso(0)}`, 200],
  ['audit export 400d (over limit)', `/api/security-observability/audit-export.csv?fromUtc=${iso(-400)}&toUtc=${iso(0)}`, 400],
  ['monitoring activity 31d', `/api/monitoring/activity?fromUtc=${iso(-31)}&toUtc=${iso(0)}&page=1&pageSize=100`, 200],
  ['monitoring activity 60d (over limit)', `/api/monitoring/activity?fromUtc=${iso(-60)}&toUtc=${iso(0)}&page=1&pageSize=100`, 400],
  ['security dashboard max window', '/api/security-observability/dashboard?windowHours=100000', 200],
  ['operations overview', '/api/operations/overview', 200],
];

const rows = [];
for (const [name, path, expected] of checks) {
  const times = [];
  let status = 0, bytes = 0, extra = '';
  for (let i = 0; i < runs; i++) {
    const started = performance.now();
    const response = await fetch(base + path, { headers });
    const body = await response.arrayBuffer();
    times.push(performance.now() - started);
    status = response.status; bytes = body.byteLength;
    if (response.headers.get('x-audit-record-count')) extra = `records=${response.headers.get('x-audit-record-count')} truncated=${response.headers.get('x-audit-truncated')}`;
    if (status === 200 && /json/.test(response.headers.get('content-type') ?? '')) {
      const json = JSON.parse(new TextDecoder().decode(body));
      if (Array.isArray(json.items)) extra = `items=${json.items.length} total=${json.totalCount ?? json.total ?? '?'}`;
      else if (Array.isArray(json)) extra = `rows=${json.length}`;
    }
  }
  times.sort((a, b) => a - b);
  rows.push({ name, expected, status, ok: status === expected ? 'yes' : 'NO', kb: (bytes / 1024).toFixed(1), median_ms: times[Math.floor(times.length / 2)].toFixed(0), max_ms: times.at(-1).toFixed(0), extra });
}
console.log(`runs=${runs} base=${base}`);
console.table(rows);
const failures = rows.filter(r => r.ok !== 'yes');
console.log(failures.length ? `UNEXPECTED STATUS: ${failures.map(r => r.name).join(', ')}` : 'All statuses as expected.');
