import type { Page, Route } from '@playwright/test';

// Fixed, fictional data for the visual suite. Nothing here comes from a real workplace.
export const FROZEN_NOW = new Date('2026-03-02T09:30:00Z');

const b64 = (value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');
const permissions = [
  'reports.read', 'employees.read', 'employees.manage', 'roles.manage', 'departments.read', 'tasks.read', 'tasks.manage', 'audit.read',
  'operations.read', 'attendance.read', 'security.alerts.read', 'presence.read', 'surveys.read', 'projects.read',
  'shifts.read', 'access.assignments.read'
];
// The Admin Web only decodes the token payload; the API that would check the signature is mocked.
const expiresAt = Math.floor(FROZEN_NOW.getTime() / 1000) + 365 * 24 * 3600;
const token = `${b64({ alg: 'HS256', typ: 'JWT' })}.${b64({
  sub: '00000000-0000-0000-0000-000000000001', email: 'visual-admin@example.invalid',
  permission: permissions, role: ['SuperAdmin'], exp: expiresAt
})}.visual`;
export const adminSession = JSON.stringify({ accessToken: token, accessTokenExpiresAtUtc: new Date(expiresAt * 1000).toISOString() });

const departments = [
  { id: 'd1', code: 'OPS', name: 'Operations' },
  { id: 'd2', code: 'SUP', name: 'Customer Support' }
];
const people = [
  ['Ayesha Rahman', 'EMP-001', 'Team Lead', 'd1', null, 'TeamLead'],
  ['Rafi Karim', 'EMP-002', 'Support Agent', 'd2', 'Ayesha Rahman', 'Employee'],
  ['Nadia Sultana', 'EMP-003', 'Content Editor', 'd1', 'Ayesha Rahman', 'Employee'],
  ['Tanvir Hasan', 'EMP-004', 'QA Analyst', 'd2', 'Ayesha Rahman', 'Employee']
] as const;
const employees = people.map(([fullName, employeeCode, jobTitle, departmentId, supervisorName, role], index) => ({
  id: `e${index + 1}`, userId: `u${index + 1}`, employeeCode, fullName,
  email: `${employeeCode.toLowerCase()}@example.invalid`, jobTitle, phone: null, employmentType: 'FullTime',
  joinedOn: '2025-01-15', isActive: index !== 3, departmentId,
  departmentName: departments.find(d => d.id === departmentId)?.name ?? null,
  supervisorEmployeeId: supervisorName ? 'e1' : null, supervisorName,
  roles: [{ id: role === 'TeamLead' ? 'r2' : 'r1', name: role, isActive: true }]
}));

const dashboard = {
  generatedAtUtc: FROZEN_NOW.toISOString(), from: '2026-02-24', to: '2026-03-02',
  workforce: { activeEmployees: 3, activeDepartments: 2 },
  attendance: { sessions: 18, distinctEmployees: 3, completedSessions: 15, openSessions: 3, lateSessions: 2, earlyLeaveSessions: 1, totalBreakMinutes: 540 },
  projects: { planning: 1, active: 2, onHold: 0, completed: 1, archived: 0, overdue: 1 },
  tasks: { open: 9, toDo: 4, inProgress: 3, blocked: 2, done: 12, cancelled: 1, overdue: 2, unassignedOpen: 1, completedInPeriod: 5 },
  surveys: { publishedForms: 2, activeAssignments: 4, pendingReview: 1, approvedAssignments: 3, rejectedAssignments: 0, overdueAssignments: 1, submittedInPeriod: 2, reviewedInPeriod: 2 }
};
const dailyAttendance = ['2026-02-24', '2026-02-25', '2026-02-26', '2026-02-27', '2026-03-01', '2026-03-02'].map((workDate, i) => ({
  workDate, sessions: 3, distinctEmployees: 3, completedSessions: i === 5 ? 0 : 3, openSessions: i === 5 ? 3 : 0,
  lateSessions: i % 3 === 0 ? 1 : 0, earlyLeaveSessions: i === 2 ? 1 : 0, totalBreakMinutes: 90
}));
const workload = employees.slice(0, 3).map((e, i) => ({
  employeeId: e.id, employeeCode: e.employeeCode, fullName: e.fullName, departmentName: e.departmentName,
  openTasks: 4 - i, urgentOpenTasks: i === 0 ? 1 : 0, overdueTasks: i === 1 ? 1 : 0,
  activeSurveyAssignments: 1, overdueSurveyAssignments: 0, totalOpenItems: 5 - i
}));

const paged = (items: unknown[]) => ({ items, page: 1, pageSize: 100, totalCount: items.length });

function bodyFor(path: string): unknown {
  if (path.endsWith('/reports/dashboard')) return dashboard;
  if (path.endsWith('/reports/attendance/daily')) return dailyAttendance;
  if (path.endsWith('/reports/workload')) return workload;
  if (path === '/api/employees') return paged(employees);
  if (path.endsWith('/admin-lookups/employees/departments')) return departments;
  if (path.endsWith('/admin-lookups/employees/roles')) return [{ id: 'r1', name: 'Employee', isActive: true }, { id: 'r2', name: 'TeamLead', isActive: true }];
  if (path.endsWith('/admin-lookups/employees/supervisors')) return employees.slice(0, 1).map(e => ({ id: e.id, employeeCode: e.employeeCode, fullName: e.fullName }));
  if (path.endsWith('/reports/website-work/attention')) return {
    generatedAtUtc: FROZEN_NOW.toISOString(), localDate: '2026-03-02', utcOffsetMinutes: 0,
    thresholds: { longWorkingMinutes: 240, pendingReviewMinutes: 120, repeatedCorrectionCount: 3 },
    total: 0, activeTotal: 0, managedTotal: 0, pendingFollowUpTotal: 0, overdueFollowUpTotal: 0, critical: 0, high: 0, medium: 0, items: []
  };
  if (path.endsWith('/website-work/attention/follow-up-owners')) return [];
  if (path === '/api/presence') return paged(employees.slice(0, 3).map((e, i) => ({
    employeeId: e.id, employeeCode: e.employeeCode, fullName: e.fullName, departmentName: e.departmentName,
    isOnline: true, workState: i === 0 ? 'Working' : i === 1 ? 'OnBreak' : 'Idle',
    lastSeenAtUtc: '2026-03-02T09:29:00Z', clientKind: 'Desktop', clientVersion: '1.0.0',
    workSessionStartedAtUtc: i < 2 ? '2026-03-02T03:00:00Z' : null
  })));
  if (path.endsWith('/operations/incidents/summary')) return { open: 0, openCritical: 0, openWarning: 0, acknowledged: 0, assigned: 0, resolvedToday: 0 };
  if (path.endsWith('/security-alerts/summary')) return { open: 0, acknowledged: 0, escalated: 0, criticalActive: 0, assigned: 0, resolvedToday: 0 };
  if (path.endsWith('/admin-notifications/summary')) return { unread: 0, total: 0 };
  if (path.endsWith('/follow-ups/mine')) return { generatedAtUtc: FROZEN_NOW.toISOString(), pending: 0, overdue: 0, resolved: 0, totalCount: 0, items: [] };
  if (path.endsWith('/departments')) return departments.map(d => ({ ...d, description: null, isActive: true, employeeCount: 2 }));
  return paged([]);
}

/** Answers every API call with the fixed data above, keeps realtime offline and freezes the clock. */
export async function mockAdminWeb(page: Page, options: { signedIn: boolean }) {
  await page.clock.setFixedTime(FROZEN_NOW);
  if (options.signedIn) {
    await page.addInitScript(value => sessionStorage.setItem('task-monitoring.admin.session.v2', value), adminSession);
  }
  await page.route('**/hubs/**', route => route.abort());
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    return route.fulfill({ json: bodyFor(path) });
  });
}
