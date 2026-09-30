import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import type { AuditLog, PagedResponse } from './types';

interface AuditFilters {
  search: string;
  action: string;
  targetType: string;
  from: string;
  to: string;
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

export default function AuditLogsPage() {
  const [draft, setDraft] = useState<AuditFilters>(emptyFilters);
  const [filters, setFilters] = useState<AuditFilters>(emptyFilters);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<PagedResponse<AuditLog> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);

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

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Security & administration</p>
          <h1>Audit logs</h1>
          <p className="muted">Read-only history of authentication, administration, access, task, survey and monitoring actions recorded by the central API.</p>
        </div>
        <button className="ghost-button" type="button" onClick={() => setReloadToken(value => value + 1)}>Refresh</button>
      </div>

      <article className="panel management-form-panel">
        <form className="form-grid" onSubmit={submit}>
          <label className="wide-field">
            <span>Search</span>
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
            <div><h2>Recorded events</h2><p>Newest events first</p></div>
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
