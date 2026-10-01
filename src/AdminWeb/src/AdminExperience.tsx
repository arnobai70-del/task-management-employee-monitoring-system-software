import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from './auth';

interface QuickDestination {
  path: string;
  label: string;
  detail: string;
  code: string;
  permission: string;
}

const destinations: QuickDestination[] = [
  { path: '/dashboard', label: 'Dashboard', detail: 'Organization command center', code: 'DB', permission: 'reports.read' },
  { path: '/productivity', label: 'Productivity', detail: 'Workload and productivity signals', code: 'PD', permission: 'reports.read' },
  { path: '/sla-analytics', label: 'SLA Analytics', detail: 'Escalation and SLA performance', code: 'SL', permission: 'reports.read' },
  { path: '/operations', label: 'Operations', detail: 'System and service health', code: 'OP', permission: 'reports.read' },
  { path: '/security-alerts', label: 'Security Alerts', detail: 'Automated security signals', code: 'SA', permission: 'audit.read' },
  { path: '/employees', label: 'Employees', detail: 'People and reporting lines', code: 'EM', permission: 'employees.read' },
  { path: '/departments', label: 'Departments', detail: 'Organization structure', code: 'DP', permission: 'departments.read' },
  { path: '/shifts', label: 'Shifts', detail: 'Shift schedules and rules', code: 'SH', permission: 'shifts.read' },
  { path: '/attendance', label: 'Attendance', detail: 'Work sessions and lateness', code: 'AT', permission: 'attendance.read' },
  { path: '/projects', label: 'Projects', detail: 'Project delivery overview', code: 'PR', permission: 'projects.read' },
  { path: '/tasks', label: 'Tasks', detail: 'Cross-project task queue', code: 'TK', permission: 'tasks.read' },
  { path: '/website-work', label: 'Website Work', detail: 'Website work assignments', code: 'WW', permission: 'tasks.read' },
  { path: '/follow-ups', label: 'My Follow-ups', detail: 'Items needing follow-up', code: 'FU', permission: 'tasks.manage' },
  { path: '/surveys', label: 'Surveys', detail: 'Survey operations and review', code: 'SV', permission: 'surveys.read' },
  { path: '/access/rdp', label: 'RDP Assign', detail: 'Remote desktop assignments', code: 'RD', permission: 'access.assignments.read' },
  { path: '/access/ip', label: 'IP Assign', detail: 'IP assignments', code: 'IP', permission: 'access.assignments.read' },
  { path: '/access/websites', label: 'Website Assign', detail: 'Website access assignments', code: 'WB', permission: 'access.assignments.read' },
  { path: '/audit-logs', label: 'Audit Logs', detail: 'Security and operator audit trail', code: 'AU', permission: 'audit.read' }
];

export default function AdminExperience() {
  const { isAuthenticated, can } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [compact, setCompact] = useState(() => window.localStorage.getItem('taskmonitoring-admin-density') === 'compact');
  const inputRef = useRef<HTMLInputElement>(null);

  const allowed = useMemo(() => destinations.filter(item => can(item.permission)), [can]);
  const filtered = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    if (!normalized) return allowed;
    return allowed.filter(item => `${item.label} ${item.detail} ${item.code}`.toLowerCase().includes(normalized));
  }, [allowed, query]);

  useEffect(() => {
    document.body.classList.toggle('compact-density', compact);
    window.localStorage.setItem('taskmonitoring-admin-density', compact ? 'compact' : 'comfortable');
    return () => document.body.classList.remove('compact-density');
  }, [compact]);

  useEffect(() => {
    if (!isAuthenticated) return;
    const onKeyDown = (event: globalThis.KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setOpen(value => !value);
      }
      if (event.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isAuthenticated]);

  useEffect(() => {
    if (!open) return;
    setQuery('');
    window.setTimeout(() => inputRef.current?.focus(), 0);
  }, [open]);

  useEffect(() => setOpen(false), [location.pathname]);

  if (!isAuthenticated || location.pathname === '/login') return null;

  function go(path: string) {
    setOpen(false);
    navigate(path);
  }

  function handleInputKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' && filtered.length > 0) {
      event.preventDefault();
      go(filtered[0].path);
    }
  }

  return (
    <>
      <button className="command-trigger" type="button" onClick={() => setOpen(true)} aria-label="Open quick navigation">
        <span className="command-trigger-dot" />
        <span>Quick jump</span>
        <kbd>Ctrl K</kbd>
      </button>

      {open && (
        <div className="command-layer" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setOpen(false); }}>
          <section className="command-palette" role="dialog" aria-modal="true" aria-label="Quick navigation">
            <div className="command-search-row">
              <span className="command-search-icon" aria-hidden="true">⌕</span>
              <input
                ref={inputRef}
                value={query}
                onChange={event => setQuery(event.target.value)}
                onKeyDown={handleInputKeyDown}
                placeholder="Jump to a workspace…"
                aria-label="Search admin workspaces"
              />
              <kbd>Esc</kbd>
            </div>
            <div className="command-meta-row">
              <span>Authorized workspaces</span>
              <button type="button" className="density-toggle" onClick={() => setCompact(value => !value)}>
                Density: <strong>{compact ? 'Compact' : 'Comfortable'}</strong>
              </button>
            </div>
            <div className="command-results" role="listbox">
              {filtered.map(item => (
                <button key={item.path} type="button" className={location.pathname === item.path ? 'command-result current' : 'command-result'} onClick={() => go(item.path)}>
                  <span className="command-result-code">{item.code}</span>
                  <span className="command-result-copy"><strong>{item.label}</strong><small>{item.detail}</small></span>
                  <span className="command-result-arrow" aria-hidden="true">→</span>
                </button>
              ))}
              {filtered.length === 0 && <div className="command-empty">No authorized workspace matches “{query}”.</div>}
            </div>
            <footer className="command-footer"><span>Enter opens first result</span><span>Ctrl K toggles quick jump</span></footer>
          </section>
        </div>
      )}
    </>
  );
}
