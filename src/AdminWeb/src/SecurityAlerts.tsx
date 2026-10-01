import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import { securityAlertChangedEvent } from './SecurityAlertRealtimeNotice';

interface SecurityAlert {
  id: string;
  kind: 'FailedLoginBurst' | 'RateLimitBurst' | 'RefreshTokenReuse';
  severity: 'Warning' | 'Critical';
  status: 'Open' | 'Acknowledged' | 'Escalated' | 'Resolved';
  sourceKey: string;
  title: string;
  message: string;
  eventCount: number;
  firstDetectedAtUtc: string;
  lastDetectedAtUtc: string;
  acknowledgedAtUtc: string | null;
  escalatedAtUtc: string | null;
  resolvedAtUtc: string | null;
  ownerUserId: string | null;
  ownerEmail: string | null;
  ownerName: string | null;
  occurrenceCount: number;
  resolutionKind: string | null;
}

interface SecurityAlertSummary {
  open: number;
  acknowledged: number;
  escalated: number;
  criticalActive: number;
  assigned: number;
  resolvedToday: number;
}

interface SecurityAlertAssignee {
  userId: string;
  email: string;
  name: string | null;
}

interface Filters {
  search: string;
  status: string;
  severity: string;
  kind: string;
}

const emptyFilters: Filters = { search: '', status: '', severity: '', kind: '' };

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function badge(value: string): string {
  return `status-badge status-${value.toLowerCase().replace(/\s+/g, '-')}`;
}

export default function SecurityAlertsPage() {
  const { can } = useAuth();
  const canManage = can('security.alerts.manage');
  const [draft, setDraft] = useState<Filters>(emptyFilters);
  const [filters, setFilters] = useState<Filters>(emptyFilters);
  const [page, setPage] = useState(1);
  const [alerts, setAlerts] = useState<PagedResponse<SecurityAlert> | null>(null);
  const [summary, setSummary] = useState<SecurityAlertSummary | null>(null);
  const [assignees, setAssignees] = useState<SecurityAlertAssignee[]>([]);
  const [selectedOwner, setSelectedOwner] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busyId, setBusyId] = useState('');

  const path = useMemo(() => {
    const params = new URLSearchParams({ page: String(page), pageSize: '50' });
    if (filters.search) params.set('search', filters.search);
    if (filters.status) params.set('status', filters.status);
    if (filters.severity) params.set('severity', filters.severity);
    if (filters.kind) params.set('kind', filters.kind);
    return `/api/security-alerts?${params.toString()}`;
  }, [filters, page]);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const [items, currentSummary] = await Promise.all([
        apiFetch<PagedResponse<SecurityAlert>>(path),
        apiFetch<SecurityAlertSummary>('/api/security-alerts/summary')
      ]);
      setAlerts(items);
      setSummary(currentSummary);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load security alerts.');
    } finally {
      setLoading(false);
    }
  }, [path]);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (!canManage) return;
    void apiFetch<SecurityAlertAssignee[]>('/api/security-alerts/assignees')
      .then(items => {
        setAssignees(items);
        if (items.length) setSelectedOwner(value => value || items[0].userId);
      })
      .catch(() => setAssignees([]));
  }, [canManage]);

  useEffect(() => {
    const handler = () => void load();
    window.addEventListener(securityAlertChangedEvent, handler);
    const timer = window.setInterval(() => void load(), 30_000);
    return () => {
      window.removeEventListener(securityAlertChangedEvent, handler);
      window.clearInterval(timer);
    };
  }, [load]);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPage(1);
    setFilters({
      search: draft.search.trim(),
      status: draft.status,
      severity: draft.severity,
      kind: draft.kind
    });
  }

  async function mutate(id: string, action: 'acknowledge' | 'assign' | 'resolve') {
    setBusyId(id);
    setError('');
    try {
      let body: Record<string, unknown> = {};
      if (action === 'assign') {
        if (!selectedOwner) throw new Error('Choose an alert owner first.');
        body = { ownerUserId: selectedOwner, note: 'Assigned from the security alert queue.' };
      } else {
        const note = window.prompt(action === 'acknowledge' ? 'Acknowledgement note (optional)' : 'Resolution note (optional)') || null;
        body = { note };
      }
      await apiFetch<SecurityAlert>(`/api/security-alerts/${id}/${action}`, {
        method: 'POST',
        body: JSON.stringify(body)
      });
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to update security alert.');
    } finally {
      setBusyId('');
    }
  }

  const totalPages = Math.max(1, Math.ceil((alerts?.totalCount ?? 0) / (alerts?.pageSize ?? 50)));
  const active = (summary?.open ?? 0) + (summary?.acknowledged ?? 0) + (summary?.escalated ?? 0);

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Security response</p>
          <h1>Security alerts</h1>
          <p className="muted">Deduplicated failed-login, rate-limit and refresh-token-reuse alerts with realtime notification, acknowledgement, ownership and automatic escalation.</p>
        </div>
        <button className="ghost-button" type="button" onClick={() => void load()}>Refresh</button>
      </div>

      <section className="metric-grid">
        <article className="metric-card"><span>Active</span><strong>{active}</strong><small>Open + acknowledged + escalated</small></article>
        <article className="metric-card"><span>Critical</span><strong>{summary?.criticalActive ?? 0}</strong><small>Active critical alerts</small></article>
        <article className="metric-card"><span>Escalated</span><strong>{summary?.escalated ?? 0}</strong><small>Missed acknowledgement SLA</small></article>
        <article className="metric-card"><span>Assigned</span><strong>{summary?.assigned ?? 0}</strong><small>Active alerts with owner</small></article>
        <article className="metric-card"><span>Resolved today</span><strong>{summary?.resolvedToday ?? 0}</strong><small>Manual or recovered</small></article>
      </section>

      <article className="panel management-form-panel">
        <form className="form-grid" onSubmit={submit}>
          <label className="wide-field"><span>Search</span><input value={draft.search} onChange={event => setDraft(value => ({ ...value, search: event.target.value }))} placeholder="Title, source, owner or message" /></label>
          <label><span>Status</span><select value={draft.status} onChange={event => setDraft(value => ({ ...value, status: event.target.value }))}><option value="">All</option><option>Open</option><option>Acknowledged</option><option>Escalated</option><option>Resolved</option></select></label>
          <label><span>Severity</span><select value={draft.severity} onChange={event => setDraft(value => ({ ...value, severity: event.target.value }))}><option value="">All</option><option>Warning</option><option>Critical</option></select></label>
          <label><span>Kind</span><select value={draft.kind} onChange={event => setDraft(value => ({ ...value, kind: event.target.value }))}><option value="">All</option><option value="FailedLoginBurst">Failed login burst</option><option value="RateLimitBurst">Rate-limit burst</option><option value="RefreshTokenReuse">Refresh-token reuse</option></select></label>
          {canManage && <label><span>Assign actions to</span><select value={selectedOwner} onChange={event => setSelectedOwner(event.target.value)}><option value="">Choose owner</option>{assignees.map(item => <option key={item.userId} value={item.userId}>{item.name || item.email}</option>)}</select></label>}
          <div className="wide-field form-actions"><button className="primary-button" type="submit">Apply filters</button><button className="ghost-button" type="button" onClick={() => { setDraft(emptyFilters); setFilters(emptyFilters); setPage(1); }}>Clear</button></div>
        </form>
      </article>

      {error && <div className="panel error-panel"><strong>Security alert action failed.</strong><span>{error}</span></div>}
      {loading && !alerts ? <div className="panel loading-block">Loading security alerts…</div> : (
        <article className="panel table-panel">
          <div className="panel-heading"><div><h2>Alert queue</h2><p>Most urgent active alerts first; resolved alerts follow.</p></div><span>{alerts?.totalCount ?? 0} records</span></div>
          <div className="table-wrap"><table><thead><tr><th>Severity</th><th>Alert</th><th>Status</th><th>Events</th><th>Owner</th><th>Last detected</th>{canManage && <th>Actions</th>}</tr></thead><tbody>
            {(alerts?.items || []).map(alert => <tr key={alert.id}>
              <td><span className={badge(alert.severity)}>{alert.severity}</span></td>
              <td><strong>{alert.title}</strong><small>{alert.kind} · {alert.sourceKey}</small><small>{alert.message}</small>{alert.occurrenceCount > 1 && <small>Occurrence #{alert.occurrenceCount}</small>}</td>
              <td><span className={badge(alert.status)}>{alert.status}</span>{alert.resolutionKind && <small>{alert.resolutionKind}</small>}</td>
              <td>{alert.eventCount}</td>
              <td>{alert.ownerName || alert.ownerEmail || 'Unassigned'}</td>
              <td>{formatDateTime(alert.lastDetectedAtUtc)}{alert.escalatedAtUtc && <small>Escalated {formatDateTime(alert.escalatedAtUtc)}</small>}</td>
              {canManage && <td><div className="form-actions">
                {alert.status !== 'Resolved' && alert.status !== 'Acknowledged' && <button className="ghost-button" disabled={busyId === alert.id} onClick={() => void mutate(alert.id, 'acknowledge')}>Acknowledge</button>}
                {alert.status !== 'Resolved' && <button className="ghost-button" disabled={busyId === alert.id || !selectedOwner} onClick={() => void mutate(alert.id, 'assign')}>Assign</button>}
                {alert.status !== 'Resolved' && <button className="primary-button" disabled={busyId === alert.id} onClick={() => void mutate(alert.id, 'resolve')}>Resolve</button>}
              </div></td>}
            </tr>)}
            {!alerts?.items.length && <tr><td colSpan={canManage ? 7 : 6} className="empty-cell">No security alerts match these filters.</td></tr>}
          </tbody></table></div>
          <div className="audit-pagination"><button className="ghost-button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</button><span>Page {page} of {totalPages}</span><button className="ghost-button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next</button></div>
        </article>
      )}
    </>
  );
}
