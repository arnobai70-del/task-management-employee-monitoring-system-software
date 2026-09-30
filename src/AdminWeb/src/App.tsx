import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { apiFetch } from './api';
import { useAuth } from './auth';
import AccessAssignmentsPage from './AccessAssignments';
import AuditLogsPage from './AuditLogs';
import { DepartmentManagementPage, EmployeeManagementPage } from './EmployeeDepartmentManagement';
import PresencePanel from './PresencePanel';
import ProductivityReportsPage from './ProductivityReports';
import SurveyManagementPage from './SurveyManagement';
import WebsiteWorkFollowUpsPage from './WebsiteWorkFollowUps';
import WebsiteWorkManagementPage from './WebsiteWorkManagement';
import WebsiteWorkRealtimeNotice from './WebsiteWorkRealtimeNotice';
import { ProjectManagementPage, ShiftManagementPage, TaskManagementPage } from './WorkManagement';
import type {
  AttendanceDailyMetric,
  DashboardOverview,
  Employee,
  EmployeeWorkload,
  PagedResponse,
  Project,
  ProjectTask,
  SurveyForm,
  WorkSession
} from './types';

interface NavItem {
  path: string;
  label: string;
  short: string;
  permission: string;
}

const navItems: NavItem[] = [
  { path: '/dashboard', label: 'Dashboard', short: 'DB', permission: 'reports.read' },
  { path: '/productivity', label: 'Productivity', short: 'PD', permission: 'reports.read' },
  { path: '/employees', label: 'Employees', short: 'EM', permission: 'employees.read' },
  { path: '/departments', label: 'Departments', short: 'DP', permission: 'departments.read' },
  { path: '/shifts', label: 'Shifts', short: 'SH', permission: 'shifts.read' },
  { path: '/attendance', label: 'Attendance', short: 'AT', permission: 'attendance.read' },
  { path: '/projects', label: 'Projects', short: 'PR', permission: 'projects.read' },
  { path: '/tasks', label: 'Tasks', short: 'TK', permission: 'tasks.read' },
  { path: '/website-work', label: 'Website Work', short: 'WW', permission: 'tasks.read' },
  { path: '/follow-ups', label: 'My Follow-ups', short: 'FU', permission: 'tasks.manage' },
  { path: '/surveys', label: 'Surveys', short: 'SV', permission: 'surveys.read' },
  { path: '/access/rdp', label: 'RDP Assign', short: 'RD', permission: 'access.assignments.read' },
  { path: '/access/ip', label: 'IP Assign', short: 'IP', permission: 'access.assignments.read' },
  { path: '/access/websites', label: 'Website Assign', short: 'WB', permission: 'access.assignments.read' },
  { path: '/audit-logs', label: 'Audit Logs', short: 'AU', permission: 'audit.read' }
];

function firstAllowedPath(can: (permission: string) => boolean): string {
  return navItems.find(item => can(item.permission))?.path ?? '/forbidden';
}

function RequirePermission({ permission, children }: { permission: string; children: ReactNode }) {
  const { isAuthenticated, can } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  if (!can(permission)) return <Navigate to="/forbidden" replace />;
  return <>{children}</>;
}

function HomeRedirect() {
  const { isAuthenticated, can } = useAuth();
  return <Navigate to={isAuthenticated ? firstAllowedPath(can) : '/login'} replace />;
}

function LoginPage() {
  const { isAuthenticated, can, signIn } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  if (isAuthenticated) return <Navigate to={firstAllowedPath(can)} replace />;

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError('');
    try {
      await signIn(email.trim(), password);
      navigate('/', { replace: true });
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to sign in.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login-screen">
      <section className="login-card" aria-labelledby="login-title">
        <div className="brand-mark large">TM</div>
        <p className="eyebrow">Task Management & Employee Monitoring</p>
        <h1 id="login-title">Admin sign in</h1>
        <p className="muted">Use your authorized organization account. Access is limited by server-issued permissions.</p>
        <form onSubmit={submit} className="stack-lg">
          <label>
            <span>Email</span>
            <input type="email" autoComplete="username" value={email} onChange={event => setEmail(event.target.value)} required maxLength={320} />
          </label>
          <label>
            <span>Password</span>
            <input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} required minLength={8} maxLength={200} />
          </label>
          {error && <div className="error-banner" role="alert">{error}</div>}
          <button className="primary-button" type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
        </form>
      </section>
    </main>
  );
}

function AppShell({ children }: { children: ReactNode }) {
  const { claims, can, signOut } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const location = useLocation();
  const visibleItems = navItems.filter(item => can(item.permission));

  useEffect(() => setMenuOpen(false), [location.pathname]);

  return (
    <div className="app-shell">
      <aside className={`sidebar ${menuOpen ? 'open' : ''}`}>
        <div className="sidebar-brand">
          <div className="brand-mark">TM</div>
          <div><strong>Task Monitor</strong><small>Admin Console</small></div>
        </div>
        <nav className="sidebar-nav" aria-label="Primary navigation">
          {visibleItems.map(item => (
            <NavLink key={item.path} to={item.path} className={({ isActive }) => isActive ? 'nav-link active' : 'nav-link'}>
              <span className="nav-icon">{item.short}</span><span>{item.label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer"><span>Transparent, business-scoped operations</span></div>
      </aside>
      {menuOpen && <button className="sidebar-backdrop" aria-label="Close menu" onClick={() => setMenuOpen(false)} />}
      <div className="workspace">
        <header className="topbar">
          <button className="menu-button" type="button" onClick={() => setMenuOpen(value => !value)} aria-label="Toggle navigation">☰</button>
          <div className="topbar-title"><strong>Operations Console</strong><span>Live server data</span></div>
          <div className="account-area">
            <div className="account-copy"><strong>{claims.email || 'Signed in'}</strong><span>{claims.roles.join(', ') || 'Authorized user'}</span></div>
            <button className="ghost-button" type="button" onClick={() => void signOut()}>Sign out</button>
          </div>
        </header>
        <main className="content">{children}</main>
      </div>
      <WebsiteWorkRealtimeNotice />
    </div>
  );
}

function ProtectedPage({ permission, children }: { permission: string; children: ReactNode }) {
  return <RequirePermission permission={permission}><AppShell>{children}</AppShell></RequirePermission>;
}

function useApiResource<T>(path: string) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    void apiFetch<T>(path)
      .then(value => { if (!cancelled) setData(value); })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load data.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [path, version]);

  return { data, error, loading, reload: () => setVersion(value => value + 1) };
}

function PageHeader({ title, description, action }: { title: string; description: string; action?: ReactNode }) {
  return <div className="page-header"><div><p className="eyebrow">Administration</p><h1>{title}</h1><p className="muted">{description}</p></div>{action}</div>;
}

function LoadingBlock() { return <div className="panel loading-block">Loading current server data…</div>; }
function ErrorBlock({ message, retry }: { message: string; retry?: () => void }) {
  return <div className="panel error-panel"><strong>Could not load this view.</strong><span>{message}</span>{retry && <button className="ghost-button" onClick={retry}>Retry</button>}</div>;
}
function EmptyRow({ colSpan, text = 'No records found.' }: { colSpan: number; text?: string }) { return <tr><td colSpan={colSpan} className="empty-cell">{text}</td></tr>; }

function MetricCard({ label, value, note }: { label: string; value: number | string; note?: string }) {
  return <article className="metric-card"><span>{label}</span><strong>{value}</strong>{note && <small>{note}</small>}</article>;
}

function StatusBadge({ value }: { value: string }) {
  const normalized = value.toLowerCase().replace(/\s+/g, '-');
  return <span className={`status-badge status-${normalized}`}>{value}</span>;
}

function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value.length === 10 ? `${value}T00:00:00` : value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(parsed);
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function DashboardPage() {
  const overview = useApiResource<DashboardOverview>('/api/reports/dashboard');
  const attendance = useApiResource<AttendanceDailyMetric[]>('/api/reports/attendance/daily');
  const workload = useApiResource<EmployeeWorkload[]>('/api/reports/workload?limit=6');
  const loading = overview.loading || attendance.loading || workload.loading;
  const error = overview.error || attendance.error || workload.error;
  const maxSessions = Math.max(1, ...(attendance.data || []).map(day => day.sessions));
  const recentAttendance = (attendance.data || []).slice(-10);
  const reloadAll = () => { overview.reload(); attendance.reload(); workload.reload(); };

  return (
    <>
      <PageHeader title="Dashboard" description="Organization health, workload, attendance and survey signals from the central API." action={<button className="ghost-button" onClick={reloadAll}>Refresh</button>} />
      {loading && !overview.data ? <LoadingBlock /> : error ? <ErrorBlock message={error} retry={reloadAll} /> : overview.data && (
        <>
          <section className="metric-grid">
            <MetricCard label="Active employees" value={overview.data.workforce.activeEmployees} note={`${overview.data.workforce.activeDepartments} active departments`} />
            <MetricCard label="Open tasks" value={overview.data.tasks.open} note={`${overview.data.tasks.overdue} overdue`} />
            <MetricCard label="Active projects" value={overview.data.projects.active} note={`${overview.data.projects.overdue} overdue`} />
            <MetricCard label="Attendance sessions" value={overview.data.attendance.sessions} note={`${overview.data.attendance.lateSessions} late`} />
            <MetricCard label="Pending survey review" value={overview.data.surveys.pendingReview} note={`${overview.data.surveys.overdueAssignments} overdue assignments`} />
          </section>
          <section className="dashboard-grid">
            <article className="panel">
              <div className="panel-heading"><div><h2>Attendance trend</h2><p>{formatDate(overview.data.from)} – {formatDate(overview.data.to)}</p></div></div>
              <div className="bar-chart" aria-label="Attendance sessions by day">
                {recentAttendance.map(day => (
                  <div className="bar-column" key={day.workDate} title={`${day.workDate}: ${day.sessions} sessions`}>
                    <div className="bar-value">{day.sessions}</div>
                    <div className="bar-track"><div className="bar-fill" style={{ height: `${Math.max(4, (day.sessions / maxSessions) * 100)}%` }} /></div>
                    <span>{day.workDate.slice(5)}</span>
                  </div>
                ))}
              </div>
            </article>
            <article className="panel">
              <div className="panel-heading"><div><h2>Task health</h2><p>Current workload state</p></div></div>
              <div className="progress-list">
                {[['To do', overview.data.tasks.toDo], ['In progress', overview.data.tasks.inProgress], ['Blocked', overview.data.tasks.blocked], ['Overdue', overview.data.tasks.overdue]].map(([label, value]) => (
                  <div className="progress-row" key={String(label)}><span>{label}</span><strong>{value}</strong></div>
                ))}
              </div>
            </article>
          </section>
          <PresencePanel />
          <article className="panel table-panel">
            <div className="panel-heading"><div><h2>Highest open workload</h2><p>Tasks and active survey assignments</p></div><NavLink className="text-link" to="/employees">Employees →</NavLink></div>
            <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Department</th><th>Open tasks</th><th>Urgent</th><th>Overdue</th><th>Survey work</th><th>Total</th></tr></thead><tbody>
              {(workload.data || []).map(item => <tr key={item.employeeId}><td><strong>{item.fullName}</strong><small>{item.employeeCode}</small></td><td>{item.departmentName || '—'}</td><td>{item.openTasks}</td><td>{item.urgentOpenTasks}</td><td>{item.overdueTasks}</td><td>{item.activeSurveyAssignments}</td><td><strong>{item.totalOpenItems}</strong></td></tr>)}
              {!workload.data?.length && <EmptyRow colSpan={7} />}
            </tbody></table></div>
          </article>
        </>
      )}
    </>
  );
}

function EmployeesPage() {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const path = `/api/employees?page=1&pageSize=50&isActive=true${search ? `&search=${encodeURIComponent(search)}` : ''}`;
  const resource = useApiResource<PagedResponse<Employee>>(path);
  return <>
    <PageHeader title="Employees" description="Active employee directory with department, role and reporting-line context." action={<form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(draft.trim()); }}><input value={draft} onChange={event => setDraft(event.target.value)} placeholder="Search employee" /><button className="ghost-button">Search</button></form>} />
    {resource.loading ? <LoadingBlock /> : resource.error ? <ErrorBlock message={resource.error} retry={resource.reload} /> : <article className="panel table-panel"><div className="panel-heading"><h2>Active employees</h2><span>{resource.data?.totalCount ?? 0} records</span></div><div className="table-wrap"><table><thead><tr><th>Employee</th><th>Job title</th><th>Department</th><th>Supervisor</th><th>Roles</th><th>Status</th></tr></thead><tbody>
      {(resource.data?.items || []).map(employee => <tr key={employee.id}><td><strong>{employee.fullName}</strong><small>{employee.employeeCode} · {employee.email}</small></td><td>{employee.jobTitle}</td><td>{employee.departmentName || '—'}</td><td>{employee.supervisorName || '—'}</td><td>{employee.roles.map(role => role.name).join(', ') || '—'}</td><td><StatusBadge value={employee.isActive ? 'Active' : 'Inactive'} /></td></tr>)}
      {!resource.data?.items.length && <EmptyRow colSpan={6} />}
    </tbody></table></div></article>}
  </>;
}

function AttendancePage() {
  const resource = useApiResource<PagedResponse<WorkSession>>('/api/attendance?page=1&pageSize=50');
  return <>
    <PageHeader title="Attendance" description="Recent server-validated work sessions, lateness and break totals." action={<button className="ghost-button" onClick={resource.reload}>Refresh</button>} />
    {resource.loading ? <LoadingBlock /> : resource.error ? <ErrorBlock message={resource.error} retry={resource.reload} /> : <article className="panel table-panel"><div className="panel-heading"><h2>Recent work sessions</h2><span>{resource.data?.totalCount ?? 0} records</span></div><div className="table-wrap"><table><thead><tr><th>Employee</th><th>Date</th><th>Shift</th><th>Check in</th><th>Check out</th><th>Late</th><th>Break</th></tr></thead><tbody>
      {(resource.data?.items || []).map(session => <tr key={session.id}><td><strong>{session.employeeName}</strong></td><td>{formatDate(session.workDate)}</td><td>{session.shiftName}</td><td>{formatDateTime(session.startedAtUtc)}</td><td>{formatDateTime(session.endedAtUtc)}</td><td>{session.lateMinutes} min</td><td>{session.totalBreakMinutes} min</td></tr>)}
      {!resource.data?.items.length && <EmptyRow colSpan={7} />}
    </tbody></table></div></article>}
  </>;
}

function ProjectsPage() {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const resource = useApiResource<PagedResponse<Project>>(`/api/projects?page=1&pageSize=50${search ? `&search=${encodeURIComponent(search)}` : ''}`);
  return <>
    <PageHeader title="Projects" description="Project lifecycle, due dates, active members and remaining task load." action={<form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(draft.trim()); }}><input value={draft} onChange={event => setDraft(event.target.value)} placeholder="Search project" /><button className="ghost-button">Search</button></form>} />
    {resource.loading ? <LoadingBlock /> : resource.error ? <ErrorBlock message={resource.error} retry={resource.reload} /> : <article className="panel table-panel"><div className="panel-heading"><h2>Projects</h2><span>{resource.data?.totalCount ?? 0} records</span></div><div className="table-wrap"><table><thead><tr><th>Project</th><th>Status</th><th>Start</th><th>Due</th><th>Members</th><th>Open tasks</th></tr></thead><tbody>
      {(resource.data?.items || []).map(project => <tr key={project.id}><td><strong>{project.name}</strong><small>{project.code}</small></td><td><StatusBadge value={project.status} /></td><td>{formatDate(project.startDate)}</td><td>{formatDate(project.dueDate)}</td><td>{project.activeMemberCount}</td><td>{project.openTaskCount}</td></tr>)}
      {!resource.data?.items.length && <EmptyRow colSpan={6} />}
    </tbody></table></div></article>}
  </>;
}

function TasksPage() {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const resource = useApiResource<PagedResponse<ProjectTask>>(`/api/tasks?page=1&pageSize=50${search ? `&search=${encodeURIComponent(search)}` : ''}`);
  return <>
    <PageHeader title="Tasks" description="Cross-project task queue with status, priority, assignee and due-date visibility." action={<form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(draft.trim()); }}><input value={draft} onChange={event => setDraft(event.target.value)} placeholder="Search task" /><button className="ghost-button">Search</button></form>} />
    {resource.loading ? <LoadingBlock /> : resource.error ? <ErrorBlock message={resource.error} retry={resource.reload} /> : <article className="panel table-panel"><div className="panel-heading"><h2>Tasks</h2><span>{resource.data?.totalCount ?? 0} records</span></div><div className="table-wrap"><table><thead><tr><th>Task</th><th>Project</th><th>Status</th><th>Priority</th><th>Assignee</th><th>Due</th></tr></thead><tbody>
      {(resource.data?.items || []).map(task => <tr key={task.id}><td><strong>{task.title}</strong><small>{task.commentCount} comments</small></td><td>{task.projectName}<small>{task.projectCode}</small></td><td><StatusBadge value={task.status} /></td><td><StatusBadge value={task.priority} /></td><td>{task.assigneeName || 'Unassigned'}</td><td>{formatDate(task.dueDate)}</td></tr>)}
      {!resource.data?.items.length && <EmptyRow colSpan={6} />}
    </tbody></table></div></article>}
  </>;
}

function SurveysPage() {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const resource = useApiResource<PagedResponse<SurveyForm>>(`/api/surveys?page=1&pageSize=50${search ? `&search=${encodeURIComponent(search)}` : ''}`);
  return <>
    <PageHeader title="Surveys" description="Project-scoped forms, questionnaire size, assignment volume and lifecycle status." action={<form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(draft.trim()); }}><input value={draft} onChange={event => setDraft(event.target.value)} placeholder="Search survey" /><button className="ghost-button">Search</button></form>} />
    {resource.loading ? <LoadingBlock /> : resource.error ? <ErrorBlock message={resource.error} retry={resource.reload} /> : <article className="panel table-panel"><div className="panel-heading"><h2>Survey forms</h2><span>{resource.data?.totalCount ?? 0} records</span></div><div className="table-wrap"><table><thead><tr><th>Survey</th><th>Project</th><th>Status</th><th>Questions</th><th>Assignments</th></tr></thead><tbody>
      {(resource.data?.items || []).map(survey => <tr key={survey.id}><td><strong>{survey.name}</strong><small>{survey.code}</small></td><td>{survey.projectName}<small>{survey.projectCode}</small></td><td><StatusBadge value={survey.status} /></td><td>{survey.questionCount}</td><td>{survey.assignmentCount}</td></tr>)}
      {!resource.data?.items.length && <EmptyRow colSpan={5} />}
    </tbody></table></div></article>}
  </>;
}

function ForbiddenPage() {
  const { isAuthenticated, can } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  const fallback = firstAllowedPath(can);
  return <AppShell><div className="panel centered-panel"><span className="forbidden-code">403</span><h1>Permission required</h1><p className="muted">Your account does not have permission for this admin view.</p>{fallback !== '/forbidden' && <NavLink className="primary-button link-button" to={fallback}>Go to an available section</NavLink>}</div></AppShell>;
}

function NotFoundPage() {
  const { isAuthenticated } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return <AppShell><div className="panel centered-panel"><span className="forbidden-code">404</span><h1>Page not found</h1><NavLink className="primary-button link-button" to="/">Return home</NavLink></div></AppShell>;
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/" element={<HomeRedirect />} />
      <Route path="/dashboard" element={<ProtectedPage permission="reports.read"><DashboardPage /></ProtectedPage>} />
      <Route path="/productivity" element={<ProtectedPage permission="reports.read"><ProductivityReportsPage /></ProtectedPage>} />
      <Route path="/employees" element={<ProtectedPage permission="employees.read"><EmployeeManagementPage /></ProtectedPage>} />
      <Route path="/departments" element={<ProtectedPage permission="departments.read"><DepartmentManagementPage /></ProtectedPage>} />
      <Route path="/shifts" element={<ProtectedPage permission="shifts.read"><ShiftManagementPage /></ProtectedPage>} />
      <Route path="/attendance" element={<ProtectedPage permission="attendance.read"><AttendancePage /></ProtectedPage>} />
      <Route path="/projects" element={<ProtectedPage permission="projects.read"><ProjectManagementPage /></ProtectedPage>} />
      <Route path="/tasks" element={<ProtectedPage permission="tasks.read"><TaskManagementPage /></ProtectedPage>} />
      <Route path="/website-work" element={<ProtectedPage permission="tasks.read"><WebsiteWorkManagementPage /></ProtectedPage>} />
      <Route path="/follow-ups" element={<ProtectedPage permission="tasks.manage"><WebsiteWorkFollowUpsPage /></ProtectedPage>} />
      <Route path="/surveys" element={<ProtectedPage permission="surveys.read"><SurveyManagementPage /></ProtectedPage>} />
      <Route path="/access/rdp" element={<ProtectedPage permission="access.assignments.read"><AccessAssignmentsPage kind="rdp" /></ProtectedPage>} />
      <Route path="/access/ip" element={<ProtectedPage permission="access.assignments.read"><AccessAssignmentsPage kind="ip" /></ProtectedPage>} />
      <Route path="/access/websites" element={<ProtectedPage permission="access.assignments.read"><AccessAssignmentsPage kind="websites" /></ProtectedPage>} />
      <Route path="/audit-logs" element={<ProtectedPage permission="audit.read"><AuditLogsPage /></ProtectedPage>} />
      <Route path="/forbidden" element={<ForbiddenPage />} />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}
