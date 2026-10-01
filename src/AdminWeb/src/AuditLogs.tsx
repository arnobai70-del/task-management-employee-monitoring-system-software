import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch, apiUrl, getValidAccessToken } from './api';
import { useAuth } from './auth';
import type { AuditLog, PagedResponse } from './types';

interface AuditFilters {
  search: string;
  action: string;
  targetType: string;
  from: string;
  to: string;
}

interface SecurityOverview {
  generatedAtUtc: string;
  fromUtc: string;
  toUtc: string;
  failedLogins: number;
  blockedLogins: number;
  successfulLogins: number;
  refreshReuseDetections: number;
  rateLimitRejections: number;
  privilegedActions: number;
  uniqueSecuritySourceIps: number;
}

interface SecurityEvent {
  id: string;
  actorEmail: string | null;
  action: string;
  targetType: string;
  targetId: string | null;
  ipAddress: string | null;
  createdAtUtc: string;
}

interface SecuritySource {
  source: string;
  failedLogins: number;
  blockedLogins: number;
  refreshReuseDetections: number;
  rateLimitRejections: number;
  lastSeenAtUtc: string;
}

interface SecurityCorrelation {
  kind: string;
  source: string;
  severity: string;
  eventCount: number;
  message: string;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
}

interface PrivilegedAction {
  id: string;
  actorEmail: string | null;
  action: string;
  targetType: string;
  targetId: string | null;
  ipAddress: string | null;
  createdAtUtc: string;
}

interface AuditIntegrity {
  recordCount: number;
  sha256: string;
  truncated: boolean;
  minimumRetentionDays: number;
  coverageDays: number;
  hasMinimumRetentionCoverage: boolean;
  oldestRecordAtUtc: string | null;
  newestRecordAtUtc: string | null;
  structurallyInvalidRecords: number;
  status: string;
}

interface SecurityDashboard {
  overview: SecurityOverview;
  recentEvents: SecurityEvent[];
  topSources: SecuritySource[];
  correlations: SecurityCorrelation[];
  recentPrivilegedActions: PrivilegedAction[];
  integrity: AuditIntegrity;
}

const emptyFilters: AuditFilters = { search: '', action: '', targetType: '', from: '', to: '' };

function formatDateTime(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' }).format(parsed);
}

function buildPath(filters: AuditFilters, page: number): string {
  const params = new URLSearchParams({ page: String(page), pageSize: '50' });
  if (filters.search) params.set('search', filters.search);
  if (filters.action) params.set('action', filters.action);
  if (filters.targetType) params.set('targetType', filters.targetType);
  if (filters.from) params.set('fromUtc', `${filters.from}T00:00:00.000Z`);
  if (filters.to) params.set('toUtc', `${filters.to}T23:59:59.999Z`);
  return `/api/audit-logs?${params.toString()}`;
}

function correlationClass(value: string): string {
  return `status-badge status-${value.toLowerCase()}`;
}

export default function AuditLogsPage() {
  const { can } = useAuth();
  const [draft, setDraft] = useState<AuditFilters>(emptyFilters);
  const [filters, setFilters] = useState<AuditFilters>(emptyFilters);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<PagedResponse<AuditLog> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);
  const [securityWindow, setSecurityWindow] = useState(24);
  const [security, setSecurity] = useState<SecurityDashboard | null>(null);
  const [securityLoading, setSecurityLoading] = useState(true);
  const [securityError, setSecurityError] = useState('');
  const [exporting, setExporting] = useState(false);
  const [exportNotice, setExportNotice] = useState('');

  const path = useMemo(() => buildPath(filters, page), [filters, page]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    void apiFetch<PagedResponse<AuditLog>>(path)
      .then(response => { if (!cancelled) setData(response); })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load audit logs.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [path, reloadToken]);

  useEffect(() => {
    let cancelled = false;
    setSecurityLoading(true);
    setSecurityError('');
    void apiFetch<SecurityDashboard>(`/api/security-observability/dashboard?windowHours=${securityWindow}`)
      .then(response => { if (!cancelled) setSecurity(response); })
      .catch(caught => {
        if (!cancelled) setSecurityError(caught instanceof Error ? caught.message : 'Unable to load security observability.');
      })
      .finally(() => { if (!cancelled) setSecurityLoading(false); });
    return () => { cancelled = true; };
  }, [securityWindow, reloadToken]);

  const totalPages = Math.max(1, Math.ceil((data?.totalCount ?? 0) / (data?.pageSize ?? 50)));

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPage(1);
    setFilters({
      search: draft.search.trim(),
      action: draft.action.trim(),
      targetType: draft.targetType.trim(),
      from: draft.from,
      to: draft.to
    });
  }

  function clear() {
    setDraft(emptyFilters);
    setFilters(emptyFilters);
    setPage(1);
  }

  async function exportAudit() {
    setExporting(true);
    setExportNotice('');
    try {
      const params = new URLSearchParams();
      if (filters.from) params.set('fromUtc', `${filters.from}T00:00:00.000Z`);
      if (filters.to) params.set('toUtc', `${filters.to}T23:59:59.999Z`);
      const token = await getValidAccessToken();
      const suffix = params.size ? `?${params.toString()}` : '';
      const response = await fetch(apiUrl(`/api/security-observability/audit-export.csv${suffix}`), {
        headers: { Authorization: `Bearer ${token}` }
      });
      if (!response.ok) throw new Error(`Audit export failed (${response.status}).`);
      const blob = await response.blob();
      const disposition = response.headers.get('content-disposition') || '';
      const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i);
      const filename = match ? decodeURIComponent(match[1].replace(/\"/g, '')) : 'task-monitoring-audit.csv';
      const objectUrl = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = objectUrl;
      link.download = filename;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(objectUrl);
      const sha = response.headers.get('x-audit-sha256') || 'unavailable';
      const count = response.headers.get('x-audit-record-count') || '0';
      const truncated = response.headers.get('x-audit-truncated') === 'true';
      setExportNotice(`Exported ${count} records. SHA-256 ${sha}${truncated ? ' (record limit reached)' : ''}.`);
    } catch (caught) {
      setExportNotice(caught instanceof Error ? caught.message : 'Unable to export audit records.');
    } finally {
      setExporting(false);
    }
  }

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Security & administration</p>
          <h1>Security observability & audit</h1>
          <p className="muted">Authentication signals, rate-limit activity, privileged actions, correlations and read-only compliance history from the central API.</p>
        </div>
        <div className="form-actions">
          {can('audit.export') && <button className="primary-button" type="button" disabled={exporting} onClick={() => void exportAudit()}>{exporting ? 'Exporting…' : 'Export CSV'}</button>}
          <button className="ghost-button" type="button" onClick={() => setReloadToken(value => value + 1)}>Refresh</button>
        </div>
      </div>

      {exportNotice && <div className="panel"><small className="audit-details">{exportNotice}</small></div>}

      <article className="panel management-form-panel">
        <label>
          <span>Security window</span>
          <select value={securityWindow} onChange={event => setSecurityWindow(Number(event.target.value))}>
            <option value={1}>Last hour</option>
            <option value={6}>Last 6 hours</option>
            <option value={24}>Last 24 hours</option>
            <option value={72}>Last 3 days</option>
            <option value={168}>Last 7 days</option>
          </select>
        </label>
      </article>

      {securityLoading && !security ? (
        <div className="panel loading-block">Loading security signals…</div>
      ) : securityError ? (
        <div className="panel error-panel"><strong>Could not load security observability.</strong><span>{securityError}</span></div>
      ) : security && (
        <>
          <section className="metric-grid">
            <article className="metric-card"><span>Failed logins</span><strong>{security.overview.failedLogins}</strong><small>{security.overview.blockedLogins} blocked</small></article>
            <article className="metric-card"><span>Successful logins</span><strong>{security.overview.successfulLogins}</strong><small>Selected window</small></article>
            <article className="metric-card"><span>Token reuse</span><strong>{security.overview.refreshReuseDetections}</strong><small>Critical auth signal</small></article>
            <article className="metric-card"><span>Rate limited</span><strong>{security.overview.rateLimitRejections}</strong><small>429 rejections</small></article>
            <article className="metric-card"><span>Privileged actions</span><strong>{security.overview.privilegedActions}</strong><small>Authenticated non-auth actions</small></article>
            <article className="metric-card"><span>Source IPs</span><strong>{security.overview.uniqueSecuritySourceIps}</strong><small>Observed security sources</small></article>
          </section>

          <section className="dashboard-grid">
            <article className="panel">
              <div className="panel-heading"><div><h2>Audit integrity snapshot</h2><p>Deterministic fingerprint of the selected window</p></div><span className={correlationClass(security.integrity.status)}>{security.integrity.status}</span></div>
              <div className="progress-list">
                <div className="progress-row"><span>Records fingerprinted</span><strong>{security.integrity.recordCount}</strong></div>
                <div className="progress-row"><span>Invalid structures</span><strong>{security.integrity.structurallyInvalidRecords}</strong></div>
                <div className="progress-row"><span>History coverage</span><strong>{security.integrity.coverageDays.toFixed(1)} days</strong></div>
                <div className="progress-row"><span>Minimum retention target</span><strong>{security.integrity.minimumRetentionDays} days</strong></div>
                <div className="progress-row"><span>Retention coverage</span><strong>{security.integrity.hasMinimumRetentionCoverage ? 'Met' : 'Building'}</strong></div>
              </div>
              <details><summary>SHA-256 fingerprint</summary><pre className="audit-details">{security.integrity.sha256}</pre></details>
              {security.integrity.truncated && <p className="muted">Fingerprint is partial because the configured record limit was reached.</p>}
            </article>

            <article className="panel table-panel">
              <div className="panel-heading"><div><h2>Correlated security signals</h2><p>Threshold-based detection within the configured correlation window</p></div><span>{security.correlations.length} active</span></div>
              <div className="table-wrap"><table><thead><tr><th>Severity</th><th>Kind</th><th>Source</th><th>Events</th><th>Last seen</th></tr></thead><tbody>
                {security.correlations.map(item => <tr key={`${item.kind}-${item.source}`}><td><span className={correlationClass(item.severity)}>{item.severity}</span></td><td><strong>{item.kind}</strong><small>{item.message}</small></td><td>{item.source}</td><td>{item.eventCount}</td><td>{formatDateTime(item.lastSeenAtUtc)}</td></tr>)}
                {!security.correlations.length && <tr><td colSpan={5} className="empty-cell">No threshold-based security correlation is active.</td></tr>}
              </tbody></table></div>
            </article>
          </section>

          <section className="dashboard-grid">
            <article className="panel table-panel">
              <div className="panel-heading"><div><h2>Top security sources</h2><p>Authentication and rate-limit signals grouped by IP</p></div></div>
              <div className="table-wrap"><table><thead><tr><th>Source</th><th>Failed</th><th>Blocked</th><th>Token reuse</th><th>Rate limited</th><th>Last seen</th></tr></thead><tbody>
                {security.topSources.map(item => <tr key={item.source}><td><strong>{item.source}</strong></td><td>{item.failedLogins}</td><td>{item.blockedLogins}</td><td>{item.refreshReuseDetections}</td><td>{item.rateLimitRejections}</td><td>{formatDateTime(item.lastSeenAtUtc)}</td></tr>)}
                {!security.topSources.length && <tr><td colSpan={6} className="empty-cell">No source IP security signals in this window.</td></tr>}
              </tbody></table></div>
            </article>

            <article className="panel table-panel">
              <div className="panel-heading"><div><h2>Recent privileged actions</h2><p>Authenticated non-authentication audit activity</p></div></div>
              <div className="table-wrap"><table><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th><th>Source</th></tr></thead><tbody>
                {security.recentPrivilegedActions.slice(0, 12).map(item => <tr key={item.id}><td>{formatDateTime(item.createdAtUtc)}</td><td>{item.actorEmail || 'User'}</td><td><strong>{item.action}</strong></td><td>{item.targetType}<small>{item.targetId || '—'}</small></td><td>{item.ipAddress || '—'}</td></tr>)}
                {!security.recentPrivilegedActions.length && <tr><td colSpan={5} className="empty-cell">No privileged actions in this window.</td></tr>}
              </tbody></table></div>
            </article>
          </section>

          <article className="panel table-panel">
            <div className="panel-heading"><div><h2>Recent security events</h2><p>Authentication and rate-limit events</p></div></div>
            <div className="table-wrap"><table><thead><tr><th>Time</th><th>Action</th><th>Actor</th><th>Source</th><th>Target</th></tr></thead><tbody>
              {security.recentEvents.slice(0, 25).map(item => <tr key={item.id}><td>{formatDateTime(item.createdAtUtc)}</td><td><strong>{item.action}</strong></td><td>{item.actorEmail || 'Anonymous / system'}</td><td>{item.ipAddress || '—'}</td><td>{item.targetType}<small>{item.targetId || '—'}</small></td></tr>)}
              {!security.recentEvents.length && <tr><td colSpan={5} className="empty-cell">No security events in this window.</td></tr>}
            </tbody></table></div>
          </article>
        </>
      )}

      <article className="panel management-form-panel">
        <form className="form-grid" onSubmit={submit}>
          <label className="wide-field">
            <span>Search audit history</span>
            <input value={draft.search} onChange={event => setDraft(value => ({ ...value, search: event.target.value }))} placeholder="Action, target type, target ID or IP address" maxLength={200} />
          </label>
          <label>
            <span>Exact action</span>
            <input value={draft.action} onChange={event => setDraft(value => ({ ...value, action: event.target.value }))} placeholder="employee.updated" maxLength={150} />
          </label>
          <label>
            <span>Exact target type</span>
            <input value={draft.targetType} onChange={event => setDraft(value => ({ ...value, targetType: event.target.value }))} placeholder="Employee" maxLength={100} />
          </label>
          <label>
            <span>From date (UTC)</span>
            <input type="date" value={draft.from} onChange={event => setDraft(value => ({ ...value, from: event.target.value }))} />
          </label>
          <label>
            <span>To date (UTC)</span>
            <input type="date" value={draft.to} onChange={event => setDraft(value => ({ ...value, to: event.target.value }))} min={draft.from || undefined} />
          </label>
          <div className="wide-field form-actions">
            <button className="primary-button" type="submit">Apply filters</button>
            <button className="ghost-button" type="button" onClick={clear}>Clear</button>
          </div>
        </form>
      </article>

      {loading && !data ? (
        <div className="panel loading-block">Loading audit history…</div>
      ) : error ? (
        <div className="panel error-panel">
          <strong>Could not load audit logs.</strong>
          <span>{error}</span>
          <button className="ghost-button" type="button" onClick={() => setReloadToken(value => value + 1)}>Retry</button>
        </div>
      ) : (
        <article className="panel table-panel">
          <div className="panel-heading">
            <div><h2>Recorded audit events</h2><p>Newest events first</p></div>
            <span>{data?.totalCount ?? 0} records</span>
          </div>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Time</th><th>Action</th><th>Actor</th><th>Target</th><th>Source</th><th>Details</th></tr></thead>
              <tbody>
                {(data?.items || []).map(log => (
                  <tr key={log.id}>
                    <td>{formatDateTime(log.createdAtUtc)}</td>
                    <td><strong>{log.action}</strong></td>
                    <td>{log.actorEmail || 'System / anonymous'}{log.actorUserId && <small>{log.actorUserId}</small>}</td>
                    <td><strong>{log.targetType}</strong><small>{log.targetId || '—'}</small></td>
                    <td>{log.ipAddress || '—'}</td>
                    <td>
                      {log.metadataJson || log.userAgent ? (
                        <details>
                          <summary>View</summary>
                          {log.metadataJson && <pre className="audit-details">{log.metadataJson}</pre>}
                          {log.userAgent && <small>{log.userAgent}</small>}
                        </details>
                      ) : '—'}
                    </td>
                  </tr>
                ))}
                {!data?.items.length && <tr><td colSpan={6} className="empty-cell">No audit events match these filters.</td></tr>}
              </tbody>
            </table>
          </div>
          <div className="audit-pagination">
            <button className="ghost-button" type="button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</button>
            <span>Page {page} of {totalPages}</span>
            <button className="ghost-button" type="button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next</button>
          </div>
        </article>
      )}
    </>
  );
}
