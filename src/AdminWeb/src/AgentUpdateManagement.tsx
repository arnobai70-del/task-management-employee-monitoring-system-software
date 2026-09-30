import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

type RolloutStage = 'Pilot' | 'General';
type RolloutStatus = 'Active' | 'Paused' | 'Completed' | 'Cancelled';
type AssignmentStatus = 'WaitingForDevice' | 'Pending' | 'Deferred' | 'Downloading' | 'Installed' | 'Failed' | 'RolledBack';

interface Department { id: string; code: string; name: string; isActive: boolean; }
interface EmployeeOption { id: string; employeeCode: string; fullName: string; departmentId: string | null; departmentName: string | null; }
interface Device {
  deviceId: string; machineName: string; employeeId: string | null; employeeCode: string | null; employeeName: string | null;
  departmentName: string | null; installedVersion: string | null; updaterVersion: string | null; registeredAtUtc: string;
  lastObservedAtUtc: string | null; isActive: boolean;
}
interface Assignment {
  employeeId: string; employeeCode: string; fullName: string; departmentName: string | null; deviceId: string | null;
  machineName: string | null; currentVersion: string | null; status: AssignmentStatus; statusAtUtc: string | null; message: string | null;
}
interface Rollout {
  id: string; name: string; targetVersion: string; stage: RolloutStage; status: RolloutStatus; createdAtUtc: string; createdByEmail: string | null;
  maintenanceStartUtc: string | null; maintenanceEndUtc: string | null; targetEmployees: number; boundDevices: number; waitingForDevice: number;
  pending: number; deferred: number; downloading: number; installed: number; failed: number; rolledBack: number; canPromote: boolean;
  assignments: Assignment[];
}
interface Overview {
  generatedAtUtc: string; stableVersion: string | null; stablePublishedAtUtc: string | null; enrollmentEnabled: boolean;
  enrolledDevices: number; boundDevices: number; devices: Device[]; rollouts: Rollout[];
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function badge(value: string): string {
  if (value === 'Installed' || value === 'Completed' || value === 'Active') return 'status-active';
  if (value === 'Failed' || value === 'RolledBack') return 'status-urgent';
  if (value === 'Cancelled') return 'status-inactive';
  return 'status-warning';
}

function toUtc(value: string): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

export default function AgentUpdateManagementPage() {
  const { can } = useAuth();
  const mayManage = can('agent-updates.manage');
  const [overview, setOverview] = useState<Overview | null>(null);
  const [departments, setDepartments] = useState<Department[]>([]);
  const [employees, setEmployees] = useState<EmployeeOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [name, setName] = useState('');
  const [stage, setStage] = useState<RolloutStage>('Pilot');
  const [scope, setScope] = useState<'employees' | 'department' | 'all'>('employees');
  const [departmentId, setDepartmentId] = useState('');
  const [employeeIds, setEmployeeIds] = useState<string[]>([]);
  const [maintenanceStart, setMaintenanceStart] = useState('');
  const [maintenanceEnd, setMaintenanceEnd] = useState('');
  const [note, setNote] = useState('');

  const load = useCallback(async (initial = false) => {
    if (initial) setLoading(true);
    try {
      const data = await apiFetch<Overview>('/api/agent-updates/overview');
      setOverview(data);
      setError('');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load centralized update state.');
    } finally {
      if (initial) setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(true);
    const timer = window.setInterval(() => void load(false), 15_000);
    return () => window.clearInterval(timer);
  }, [load]);

  useEffect(() => {
    if (!mayManage) return;
    void Promise.all([
      apiFetch<Department[]>('/api/departments'),
      apiFetch<PagedResponse<EmployeeOption>>('/api/employees?page=1&pageSize=200&isActive=true')
    ]).then(([departmentData, employeeData]) => {
      setDepartments(departmentData.filter(item => item.isActive));
      setEmployees(employeeData.items);
    }).catch(() => {
      setDepartments([]);
      setEmployees([]);
    });
  }, [mayManage]);

  const activeRollouts = useMemo(() => (overview?.rollouts || []).filter(item => item.status === 'Active' || item.status === 'Paused'), [overview]);
  const failedTargets = useMemo(() => (overview?.rollouts || []).reduce((sum, item) => sum + item.failed + item.rolledBack, 0), [overview]);

  async function createRollout(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage || !overview?.stableVersion) return;
    if (scope === 'all' && !window.confirm(`Target every active employee with stable version ${overview.stableVersion}?`)) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const created = await apiFetch<Rollout>('/api/agent-updates/rollouts', {
        method: 'POST',
        body: JSON.stringify({
          name: name.trim(),
          targetVersion: overview.stableVersion,
          stage,
          includeAll: scope === 'all',
          departmentIds: scope === 'department' && departmentId ? [departmentId] : [],
          employeeIds: scope === 'employees' ? employeeIds : [],
          maintenanceStartUtc: toUtc(maintenanceStart),
          maintenanceEndUtc: toUtc(maintenanceEnd),
          note: note.trim() || null
        })
      });
      setNotice(`Rollout ${created.name} created for ${created.targetEmployees} employee(s).`);
      setShowCreate(false); setName(''); setEmployeeIds([]); setDepartmentId(''); setMaintenanceStart(''); setMaintenanceEnd(''); setNote('');
      await load(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to create rollout.');
    } finally { setBusy(false); }
  }

  async function action(rollout: Rollout, verb: 'pause' | 'resume' | 'cancel') {
    if (!mayManage) return;
    if (verb === 'cancel' && !window.confirm(`Cancel rollout "${rollout.name}"? Devices will stop receiving approval for it.`)) return;
    const actionNote = window.prompt(`Optional ${verb} note:`);
    if (actionNote === null) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/agent-updates/rollouts/${rollout.id}/${verb}`, { method: 'POST', body: JSON.stringify({ note: actionNote.trim() || null }) });
      setNotice(`Rollout ${verb} action completed.`);
      await load(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : `Unable to ${verb} rollout.`);
    } finally { setBusy(false); }
  }

  async function promote(rollout: Rollout) {
    if (!mayManage || !rollout.canPromote) return;
    const confirmation = window.prompt(`Pilot is healthy. Type PROMOTE to expand "${rollout.name}" to every active employee.`);
    if (confirmation !== 'PROMOTE') return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/agent-updates/rollouts/${rollout.id}/promote`, {
        method: 'POST',
        body: JSON.stringify({ includeAll: true, departmentIds: [], employeeIds: [], note: 'Promoted to all active employees after pilot success.' })
      });
      setNotice('Pilot promoted to a general rollout for all remaining active employees.');
      await load(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to promote pilot rollout.');
    } finally { setBusy(false); }
  }

  return (
    <>
      <div className="page-header">
        <div><p className="eyebrow">Production operations</p><h1>Agent Updates</h1><p className="muted">Stage and approve Employee Desktop/Service updates while the installed updater keeps package hash, version and publisher-signature enforcement.</p></div>
        <div className="header-actions">
          <button className="ghost-button" type="button" onClick={() => void load(false)}>Refresh</button>
          {mayManage && <button className="primary-button" type="button" onClick={() => setShowCreate(value => !value)}>New rollout</button>}
        </div>
      </div>

      {error && <div className="error-banner" role="alert">{error}</div>}
      {notice && <div className="success-banner">{notice}</div>}
      {loading && !overview && <div className="panel loading-block">Loading centralized update state…</div>}

      {overview && (
        <>
          {!overview.enrollmentEnabled && <div className="error-banner">Central enrollment is disabled. Configure the server AgentUpdates enrollment secret before installing managed agents.</div>}
          <section className="metric-grid compact-metrics">
            <article className="metric-card"><span>Stable release</span><strong>{overview.stableVersion || 'Unknown'}</strong><small>Published {formatDateTime(overview.stablePublishedAtUtc)}</small></article>
            <article className="metric-card"><span>Enrolled devices</span><strong>{overview.enrolledDevices}</strong><small>{overview.boundDevices} linked to employees</small></article>
            <article className="metric-card"><span>Active rollouts</span><strong>{activeRollouts.length}</strong><small>Active or paused</small></article>
            <article className="metric-card"><span>Failed / rollback</span><strong>{failedTargets}</strong><small>Creates production incident alerts</small></article>
          </section>

          {showCreate && mayManage && (
            <form className="panel stack-lg" onSubmit={createRollout} style={{ marginBottom: 16 }}>
              <div className="panel-heading"><div><h2>Create rollout</h2><p>Pilot first is recommended. A pilot is limited to 20 active employees.</p></div></div>
              <label><span>Rollout name</span><input value={name} onChange={event => setName(event.target.value)} required maxLength={150} placeholder="October pilot" /></label>
              <div className="form-grid">
                <label><span>Stage</span><select value={stage} onChange={event => setStage(event.target.value as RolloutStage)}><option value="Pilot">Pilot</option><option value="General">General</option></select></label>
                <label><span>Target scope</span><select value={scope} onChange={event => setScope(event.target.value as typeof scope)}><option value="employees">Selected employees</option><option value="department">Department</option><option value="all">All active employees</option></select></label>
              </div>
              {scope === 'department' && <label><span>Department</span><select value={departmentId} onChange={event => setDepartmentId(event.target.value)} required><option value="">Choose department</option>{departments.map(item => <option key={item.id} value={item.id}>{item.name} · {item.code}</option>)}</select></label>}
              {scope === 'employees' && <label><span>Employees</span><select multiple size={Math.min(10, Math.max(5, employees.length))} value={employeeIds} onChange={event => setEmployeeIds(Array.from(event.target.selectedOptions, option => option.value))} required>{employees.map(item => <option key={item.id} value={item.id}>{item.fullName} · {item.employeeCode}{item.departmentName ? ` · ${item.departmentName}` : ''}</option>)}</select><small>Ctrl/Cmd-click to choose multiple employees.</small></label>}
              <div className="form-grid">
                <label><span>Maintenance start (optional)</span><input type="datetime-local" value={maintenanceStart} onChange={event => setMaintenanceStart(event.target.value)} /></label>
                <label><span>Maintenance end (optional)</span><input type="datetime-local" value={maintenanceEnd} onChange={event => setMaintenanceEnd(event.target.value)} /></label>
              </div>
              <label><span>Audit note (optional)</span><textarea value={note} onChange={event => setNote(event.target.value)} maxLength={1000} rows={3} /></label>
              <div className="header-actions"><button className="primary-button" disabled={busy || !overview.stableVersion}>Create rollout</button><button className="ghost-button" type="button" onClick={() => setShowCreate(false)}>Cancel</button></div>
            </form>
          )}

          {!mayManage && <div className="panel" style={{ marginBottom: 16 }}><p className="muted" style={{ margin: 0 }}>You can view update status, but agent-updates.manage permission is required to create or control rollouts.</p></div>}

          <article className="panel table-panel" style={{ marginBottom: 16 }}>
            <div className="panel-heading"><div><h2>Rollouts</h2><p>Only one active/paused rollout may target an employee at a time.</p></div><span>{overview.rollouts.length} total</span></div>
            <div className="table-wrap"><table><thead><tr><th>Rollout</th><th>Stage</th><th>Status</th><th>Window</th><th>Progress</th><th>Issues</th><th>Actions</th></tr></thead><tbody>
              {overview.rollouts.map(item => <tr key={item.id}>
                <td><strong>{item.name}</strong><small>v{item.targetVersion} · {item.targetEmployees} employees · by {item.createdByEmail || 'system'}</small></td>
                <td>{item.stage}</td><td><span className={`status-badge ${badge(item.status)}`}>{item.status}</span></td>
                <td>{item.maintenanceStartUtc ? formatDateTime(item.maintenanceStartUtc) : 'Any approved check'}<small>{item.maintenanceEndUtc ? `to ${formatDateTime(item.maintenanceEndUtc)}` : 'No end window'}</small></td>
                <td><strong>{item.installed}/{item.targetEmployees}</strong><small>{item.downloading} downloading · {item.deferred} deferred · {item.waitingForDevice} waiting device</small></td>
                <td>{item.failed + item.rolledBack}<small>{item.failed} failed · {item.rolledBack} rolled back</small></td>
                <td><div className="header-actions" style={{ flexWrap: 'wrap' }}>{mayManage && item.status === 'Active' && <button className="ghost-button" disabled={busy} onClick={() => void action(item, 'pause')}>Pause</button>}{mayManage && item.status === 'Paused' && <button className="ghost-button" disabled={busy} onClick={() => void action(item, 'resume')}>Resume</button>}{mayManage && (item.status === 'Active' || item.status === 'Paused') && <button className="ghost-button" disabled={busy} onClick={() => void action(item, 'cancel')}>Cancel</button>}{mayManage && item.canPromote && <button className="primary-button" disabled={busy} onClick={() => void promote(item)}>Promote all</button>}</div></td>
              </tr>)}
              {!overview.rollouts.length && <tr><td colSpan={7} className="empty-cell">No centralized rollouts yet.</td></tr>}
            </tbody></table></div>
          </article>

          {overview.rollouts.map(item => <details className="panel" key={`assignments-${item.id}`} style={{ marginBottom: 12 }}>
            <summary><strong>{item.name}</strong> · target status by employee</summary>
            <div className="table-wrap" style={{ marginTop: 12 }}><table><thead><tr><th>Employee</th><th>Device</th><th>Current</th><th>Status</th><th>Updated</th><th>Message</th></tr></thead><tbody>
              {item.assignments.map(target => <tr key={target.employeeId}><td><strong>{target.fullName}</strong><small>{target.employeeCode} · {target.departmentName || 'No department'}</small></td><td>{target.machineName || 'Not enrolled'}</td><td>{target.currentVersion || '—'}</td><td><span className={`status-badge ${badge(target.status)}`}>{target.status}</span></td><td>{formatDateTime(target.statusAtUtc)}</td><td>{target.message || '—'}</td></tr>)}
            </tbody></table></div>
          </details>)}

          <article className="panel table-panel">
            <div className="panel-heading"><div><h2>Enrolled devices</h2><p>Per-device credentials are not displayed; this table contains operational identity/version metadata only.</p></div></div>
            <div className="table-wrap"><table><thead><tr><th>Machine</th><th>Employee</th><th>Department</th><th>Installed</th><th>Updater</th><th>Last observed</th><th>State</th></tr></thead><tbody>
              {overview.devices.map(device => <tr key={device.deviceId}><td><strong>{device.machineName}</strong><small>{device.deviceId}</small></td><td>{device.employeeName || 'Waiting for employee sign-in'}<small>{device.employeeCode || '—'}</small></td><td>{device.departmentName || '—'}</td><td>{device.installedVersion || '—'}</td><td>{device.updaterVersion || '—'}</td><td>{formatDateTime(device.lastObservedAtUtc)}</td><td><span className={`status-badge ${device.isActive ? 'status-active' : 'status-inactive'}`}>{device.isActive ? 'Active' : 'Replaced'}</span></td></tr>)}
              {!overview.devices.length && <tr><td colSpan={7} className="empty-cell">No centrally enrolled agent devices.</td></tr>}
            </tbody></table></div>
          </article>

          <div className="panel" style={{ marginTop: 16 }}><strong>Safety boundary</strong><p className="muted" style={{ marginBottom: 0 }}>Central rollout approval does not bypass updater verification. Production packages still require the configured Authenticode publisher certificate; package SHA-256 and executable versions are validated before activation. If Employee Desktop is open, the updater safely defers unless an administrator explicitly uses a forced maintenance run.</p></div>
        </>
      )}
    </>
  );
}
