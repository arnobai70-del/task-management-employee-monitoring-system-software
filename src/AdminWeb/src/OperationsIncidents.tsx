import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import { operationsIncidentChangedEvent } from './OperationsIncidentRealtimeNotice';
import './management.css';

type IncidentSeverity = 'Info' | 'Warning' | 'Critical';
type IncidentStatus = 'Open' | 'Acknowledged' | 'Resolved';
type IncidentKind = 'AgentOffline' | 'ServiceStopped' | 'OutdatedRuntime' | 'RollbackDetected' | 'BackupStale' | 'DatabaseDegraded';

interface IncidentEvent {
  action: string;
  atUtc: string;
  actorUserId: string | null;
  actorEmail: string | null;
  note: string | null;
}

interface Incident {
  id: string;
  kind: IncidentKind;
  severity: IncidentSeverity;
  status: IncidentStatus;
  sourceKey: string;
  title: string;
  message: string;
  employeeId: string | null;
  employeeCode: string | null;
  employeeName: string | null;
  departmentName: string | null;
  firstDetectedAtUtc: string;
  lastDetectedAtUtc: string;
  acknowledgedAtUtc: string | null;
  resolvedAtUtc: string | null;
  ownerUserId: string | null;
  ownerEmail: string | null;
  ownerName: string | null;
  occurrenceCount: number;
  resolutionKind: string | null;
  history: IncidentEvent[];
}

interface Summary {
  open: number;
  openCritical: number;
  openWarning: number;
  acknowledged: number;
  assigned: number;
  resolvedToday: number;
}

interface Assignee {
  userId: string;
  email: string;
  name: string | null;
}

interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function statusClass(value: string): string {
  if (value === 'Resolved') return 'status-active';
  if (value === 'Critical') return 'status-urgent';
  if (value === 'Acknowledged') return 'status-warning';
  return 'status-warning';
}

function kindLabel(value: IncidentKind): string {
  const labels: Record<IncidentKind, string> = {
    AgentOffline: 'Agent offline',
    ServiceStopped: 'Service stopped',
    OutdatedRuntime: 'Outdated runtime',
    RollbackDetected: 'Rollback detected',
    BackupStale: 'Backup stale/missing',
    DatabaseDegraded: 'Database degraded'
  };
  return labels[value];
}

export default function OperationsIncidentsPage() {
  const { can } = useAuth();
  const mayManage = can('operations.manage');
  const [summary, setSummary] = useState<Summary | null>(null);
  const [items, setItems] = useState<Incident[]>([]);
  const [assignees, setAssignees] = useState<Assignee[]>([]);
  const [selected, setSelected] = useState<Incident | null>(null);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [severity, setSeverity] = useState('');
  const [ownerUserId, setOwnerUserId] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const listPath = useMemo(() => {
    const params = new URLSearchParams({ page: '1', pageSize: '100' });
    if (search) params.set('search', search);
    if (status) params.set('status', status);
    if (severity) params.set('severity', severity);
    return `/api/operations/incidents?${params.toString()}`;
  }, [search, severity, status]);

  const load = useCallback(async (initial = false) => {
    if (initial) setLoading(true);
    try {
      const [nextSummary, page] = await Promise.all([
        apiFetch<Summary>('/api/operations/incidents/summary'),
        apiFetch<PagedResponse<Incident>>(listPath)
      ]);
      setSummary(nextSummary);
      setItems(page.items);
      setError('');
      if (selected) {
        const stillPresent = page.items.find(item => item.id === selected.id);
        if (stillPresent) {
          const detail = await apiFetch<Incident>(`/api/operations/incidents/${selected.id}`);
          setSelected(detail);
          setOwnerUserId(detail.ownerUserId || '');
        }
      }
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load incidents.');
    } finally {
      if (initial) setLoading(false);
    }
  }, [listPath, selected]);

  useEffect(() => {
    void load(true);
    const timer = window.setInterval(() => void load(false), 15_000);
    const realtime = () => void load(false);
    window.addEventListener(operationsIncidentChangedEvent, realtime);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener(operationsIncidentChangedEvent, realtime);
    };
  }, [load]);

  useEffect(() => {
    if (!mayManage) return;
    void apiFetch<Assignee[]>('/api/operations/incidents/assignees')
      .then(setAssignees)
      .catch(() => setAssignees([]));
  }, [mayManage]);

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSearch(searchDraft.trim());
  }

  async function openDetail(id: string) {
    try {
      const detail = await apiFetch<Incident>(`/api/operations/incidents/${id}`);
      setSelected(detail);
      setOwnerUserId(detail.ownerUserId || '');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load incident history.');
    }
  }

  async function postAction(path: string, body: unknown) {
    setBusy(true);
    setError('');
    try {
      const detail = await apiFetch<Incident>(path, { method: 'POST', body: JSON.stringify(body) });
      setSelected(detail);
      setOwnerUserId(detail.ownerUserId || '');
      await load(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to update incident.');
    } finally {
      setBusy(false);
    }
  }

  async function acknowledge() {
    if (!selected || !mayManage) return;
    const note = window.prompt('Optional acknowledgement note:');
    if (note === null) return;
    await postAction(`/api/operations/incidents/${selected.id}/acknowledge`, { note: note.trim() || null });
  }

  async function assign() {
    if (!selected || !mayManage || !ownerUserId) return;
    const note = window.prompt('Optional assignment note:');
    if (note === null) return;
    await postAction(`/api/operations/incidents/${selected.id}/assign`, { ownerUserId, note: note.trim() || null });
  }

  async function resolve() {
    if (!selected || !mayManage) return;
    const note = window.prompt('Resolution note (recommended):');
    if (note === null) return;
    if (!window.confirm(`Resolve "${selected.title}"? If the same unhealthy signal continues, it can reopen after the configured cooldown.`)) return;
    await postAction(`/api/operations/incidents/${selected.id}/resolve`, { note: note.trim() || null });
  }

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Production operations</p>
          <h1>Incident Center</h1>
          <p className="muted">Automatic operational alerts with deduplication, ownership, acknowledgement, resolution and durable history.</p>
        </div>
        <button className="ghost-button" type="button" onClick={() => void load(false)}>Refresh now</button>
      </div>

      {error && <div className="error-banner">{error}</div>}
      <div className="panel" style={{ marginBottom: 16 }}>
        <strong>Signal scope</strong>
        <p className="muted" style={{ marginBottom: 0 }}>
          Incidents use TaskMonitoring operational health only: long-offline agents, stopped service, outdated runtime, rollback state, stale/missing database backup and degraded database latency. They do not inspect employee screen content, keystrokes, passwords, cookies, external website fields, balances, earnings or browsing history.
        </p>
      </div>

      {summary && (
        <section className="metric-grid compact-metrics">
          <article className="metric-card"><span>Open</span><strong>{summary.open}</strong><small>{summary.openCritical} critical · {summary.openWarning} warning</small></article>
          <article className="metric-card"><span>Acknowledged</span><strong>{summary.acknowledged}</strong><small>Seen but not resolved</small></article>
          <article className="metric-card"><span>Assigned</span><strong>{summary.assigned}</strong><small>Active incidents with owner</small></article>
          <article className="metric-card"><span>Resolved today</span><strong>{summary.resolvedToday}</strong><small>Manual or health-recovered</small></article>
        </section>
      )}

      <article className="panel table-panel">
        <div className="panel-heading">
          <div><h2>Incidents</h2><p>Active incidents appear first; recurring signals reuse the same incident identity.</p></div>
          <div className="header-actions">
            <form className="inline-search" onSubmit={submitSearch}>
              <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Employee, owner, incident" />
              <button className="ghost-button" type="submit">Search</button>
            </form>
            <select value={status} onChange={event => setStatus(event.target.value)}>
              <option value="">All status</option><option value="Open">Open</option><option value="Acknowledged">Acknowledged</option><option value="Resolved">Resolved</option>
            </select>
            <select value={severity} onChange={event => setSeverity(event.target.value)}>
              <option value="">All severity</option><option value="Critical">Critical</option><option value="Warning">Warning</option><option value="Info">Info</option>
            </select>
          </div>
        </div>
        {loading ? <div className="loading-block">Loading incidents…</div> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Incident</th><th>Severity</th><th>Status</th><th>Employee/source</th><th>Owner</th><th>Detected</th><th>Occurrences</th><th></th></tr></thead>
            <tbody>
              {items.map(item => (
                <tr key={item.id}>
                  <td><strong>{item.title}</strong><small>{kindLabel(item.kind)} · {item.message}</small></td>
                  <td><span className={`status-badge ${statusClass(item.severity)}`}>{item.severity}</span></td>
                  <td><span className={`status-badge ${statusClass(item.status)}`}>{item.status}</span></td>
                  <td>{item.employeeName || item.sourceKey}<small>{item.employeeCode || item.departmentName || 'System infrastructure'}</small></td>
                  <td>{item.ownerName || item.ownerEmail || 'Unassigned'}</td>
                  <td>{formatDateTime(item.firstDetectedAtUtc)}<small>Latest {formatDateTime(item.lastDetectedAtUtc)}</small></td>
                  <td>{item.occurrenceCount}</td>
                  <td><button className="ghost-button" type="button" onClick={() => void openDetail(item.id)}>History / Actions</button></td>
                </tr>
              ))}
              {!items.length && <tr><td colSpan={8} className="empty-cell">No incidents match this filter.</td></tr>}
            </tbody>
          </table></div>
        )}
      </article>

      {selected && (
        <article className="panel" style={{ marginTop: 16 }}>
          <div className="panel-heading">
            <div><h2>{selected.title}</h2><p>{selected.message}</p></div>
            <button className="ghost-button" type="button" onClick={() => setSelected(null)}>Close</button>
          </div>
          <div className="progress-list" style={{ marginBottom: 16 }}>
            <div className="progress-row"><span>Status / severity</span><strong>{selected.status} · {selected.severity}</strong></div>
            <div className="progress-row"><span>Current owner</span><strong>{selected.ownerName || selected.ownerEmail || 'Unassigned'}</strong></div>
            <div className="progress-row"><span>First detected</span><strong>{formatDateTime(selected.firstDetectedAtUtc)}</strong></div>
            <div className="progress-row"><span>Resolved</span><strong>{formatDateTime(selected.resolvedAtUtc)}{selected.resolutionKind ? ` · ${selected.resolutionKind}` : ''}</strong></div>
          </div>

          {mayManage && selected.status !== 'Resolved' && (
            <div className="header-actions" style={{ marginBottom: 18, flexWrap: 'wrap' }}>
              <button className="ghost-button" disabled={busy || selected.status === 'Acknowledged'} onClick={() => void acknowledge()}>Acknowledge</button>
              <select value={ownerUserId} onChange={event => setOwnerUserId(event.target.value)} disabled={busy}>
                <option value="">Choose incident owner</option>
                {assignees.map(owner => <option key={owner.userId} value={owner.userId}>{owner.name ? `${owner.name} · ${owner.email}` : owner.email}</option>)}
              </select>
              <button className="ghost-button" disabled={busy || !ownerUserId} onClick={() => void assign()}>Assign owner</button>
              <button className="primary-button" disabled={busy} onClick={() => void resolve()}>Resolve</button>
            </div>
          )}

          {!mayManage && <p className="muted">Your account can view incident history but does not have operations.manage permission for incident actions.</p>}
          <h3>Incident history</h3>
          <div className="progress-list">
            {selected.history.map((event, index) => (
              <div className="progress-row" key={`${event.atUtc}-${index}`}>
                <span><strong>{event.action}</strong><small style={{ display: 'block' }}>{event.actorEmail || 'Automatic health monitor'}{event.note ? ` · ${event.note}` : ''}</small></span>
                <strong>{formatDateTime(event.atUtc)}</strong>
              </div>
            ))}
          </div>
        </article>
      )}
    </>
  );
}
