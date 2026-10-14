import { mkdirSync, writeFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';

// Creates fictional people through the real API as the bootstrap admin:
//   lead     (TeamLead, which seed-roles.sql gives employees.read but not employees.scope.all)
//   report   (Employee, reports to lead)
//   outsider (Employee, no supervisor; Employee has no admin permissions, so also the RBAC subject)
// Codes are unique per run so the suite can run again on the same database.
export const STATE_FILE = '.auth/state.json';

export interface Person { id: string; code: string; fullName: string; email: string; password: string }
export interface E2EState {
  run: string;
  admin: { email: string; session: { accessToken: string; accessTokenExpiresAtUtc: string } };
  lead: Person; report: Person; outsider: Person;
}

const api = (process.env.E2E_API_URL ?? 'http://127.0.0.1:5080').replace(/\/$/, '');

async function call<T>(path: string, init: RequestInit & { token?: string } = {}): Promise<T> {
  const response = await fetch(`${api}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init.token ? { Authorization: `Bearer ${init.token}` } : {}) }
  });
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${path} -> ${response.status} ${await response.text()}`);
  return response.status === 204 ? (undefined as T) : (await response.json()) as T;
}

export default async function globalSetup() {
  const email = process.env.E2E_ADMIN_EMAIL;
  const password = process.env.E2E_ADMIN_PASSWORD;
  if (!email || !password) throw new Error('E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD are required.');

  const session = await call<{ accessToken: string; accessTokenExpiresAtUtc: string }>('/api/auth/web/login', {
    method: 'POST', body: JSON.stringify({ email, password })
  });
  const token = session.accessToken;
  const roles = await call<Array<{ id: string; name: string }>>('/api/admin-lookups/employees/roles', { token });
  const roleId = (name: string) => {
    const role = roles.find(item => item.name === name);
    if (!role) throw new Error(`Role ${name} is missing`);
    return role.id;
  };

  const run = Date.now().toString(36).toUpperCase();
  const create = async (key: string, role: string, supervisorEmployeeId?: string): Promise<Person> => {
    const person = {
      code: `E2E-${key}-${run}`,
      fullName: `E2E ${key[0]}${key.slice(1).toLowerCase()} ${run}`,
      email: `e2e-${key.toLowerCase()}-${run.toLowerCase()}@example.invalid`,
      password: `E2e-${randomBytes(12).toString('hex')}`
    };
    const created = await call<{ id: string }>('/api/employees', {
      method: 'POST', token,
      body: JSON.stringify({ employeeCode: person.code, fullName: person.fullName, email: person.email, password: person.password,
        jobTitle: 'Fictional tester', roleIds: [roleId(role)], supervisorEmployeeId })
    });
    return { id: created.id, ...person };
  };

  const lead = await create('LEAD', 'TeamLead');
  const report = await create('REPORT', 'Employee', lead.id);
  const outsider = await create('OUTSIDER', 'Employee');

  mkdirSync('.auth', { recursive: true });
  const state: E2EState = { run, admin: { email, session }, lead, report, outsider };
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2), { mode: 0o600 });
}
