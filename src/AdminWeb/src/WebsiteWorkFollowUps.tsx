import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch } from './api';

type FollowUpState = 'Pending' | 'Overdue' | 'Resolved';

interface FollowUpItem {
  taskId: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  title: string;
  taskStatus: string;
  taskDueDate: string | null;
  state: FollowUpState;
  assignedAtUtc: string;
  assignedByUserId: string | null;
  assignedByEmail: string | null;
  assignmentNote: string;
  dueAtUtc: string;
  resolvedAtUtc: string | null;
  resolvedByUserId: string | null;
  resolvedByEmail: string | null;
  resolutionNote: string | null;
}

interface FollowUpInboxResponse {
  generatedAtUtc: string;
  pending: number;
  overdue: number;
  resolved: number;
  totalCount: number;
  items: FollowUpItem[];
}

interface AttentionActionResponse {
  taskId: string;
  disposition: string;
  actionAtUtc: string;
  message: string;
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function formatDate(value: string | null): string {
  if (!value) return '—';
  const date = new Date(`${value}T00:00:00`);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(date);
}

function workStatusLabel(value: string): string {
  if (value === 'ToDo') return 'Ready';
  if (value === 'InProgress') return 'Working';
  if (value === 'Blocked') return 'Pending Review';
  if (value === 'Done') return 'Approved';
  if (value === 'Cancelled') return 'Cancelled';
  return value;
}

function statusClass(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

export default function WebsiteWorkFollowUpsPage() {
  const [includeResolved, setIncludeResolved] = useState(false);
  const [data, setData] = useState<FollowUpInboxResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);
  const [resolveTaskId, setResolveTaskId] = useState<string | null>(null);
  const [resolutionNote, setResolutionNote] = useState('');
  const [busy, setBusy] = useState(false);

  const selected = useMemo(
    () => data?.items.find(item => item.taskId === resolveTaskId) ?? null,
    [data, resolveTaskId]
  );

  useEffect(() => {
    let cancelled = false;

    const load = async (showLoading: boolean) => {
      if (showLoading) setLoading(true);
      try {
        const result = await apiFetch<FollowUpInboxResponse>(
          `/api/website-work/follow-ups/mine?includeResolved=${includeResolved}`
        );
        if (!cancelled) {
          setData(result);
          setError('');
          if (resolveTaskId && !result.items.some(item => item.taskId === resolveTaskId)) {
            setResolveTaskId(null);
            setResolutionNote('');
          }
        }
      } catch (caught) {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load your Website Work follow-ups.');
      } finally {
        if (!cancelled && showLoading) setLoading(false);
      }
    };

    void load(true);
    const timer = window.setInterval(() => void load(false), 20_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [includeResolved, version, resolveTaskId]);

  function openResolve(item: FollowUpItem) {
    setResolveTaskId(item.taskId);
    setResolutionNote('');
    setError('');
  }

  async function resolveFollowUp(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!resolveTaskId) return;

    setBusy(true);
    setError('');
    try {
      await apiFetch<AttentionActionResponse>(`/api/website-work/follow-ups/${resolveTaskId}/resolve`, {
        method: 'POST',
        body: JSON.stringify({ note: resolutionNote.trim() || null })
      });
      setResolveTaskId(null);
      setResolutionNote('');
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to resolve this follow-up.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Website Work</p>
          <h1>My Follow-ups</h1>
          <p className="muted">Follow-up actions assigned to your manager account. Overdue items stay visible until you resolve them or the Website Work lifecycle changes.</p>
        </div>
        <div className="header-actions" style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <label style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
            <input type="checkbox" checked={includeResolved} onChange={event => setIncludeResolved(event.target.checked)} /> Show resolved
          </label>
          <button className="ghost-button" type="button" onClick={() => setVersion(value => value + 1)}>Refresh</button>
        </div>
      </div>

      {data && <section className="metric-grid">
        <article className="metric-card"><span>Pending</span><strong>{data.pending}</strong><small>Due in the future</small></article>
        <article className="metric-card"><span>Overdue</span><strong>{data.overdue}</strong><small>Needs action now</small></article>
        <article className="metric-card"><span>Resolved</span><strong>{data.resolved}</strong><small>Current lifecycle</small></article>
      </section>}

      {error && <div className="error-banner" role="alert">{error}</div>}

      <article className="panel table-panel">
        <div className="panel-heading">
          <div><h2>Assigned follow-ups</h2><p>Auto-refresh every 20 seconds</p></div>
          {data && <span>{data.totalCount} shown · updated {formatDateTime(data.generatedAtUtc)}</span>}
        </div>
        {loading && !data ? <div className="loading-block">Loading your follow-ups…</div> : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>State</th><th>Worker / target</th><th>Project</th><th>Follow-up</th><th>Due</th><th>Work state</th><th>Action</th></tr></thead>
              <tbody>
                {(data?.items || []).map(item => (
                  <tr key={item.taskId}>
                    <td><span className={`status-badge status-${statusClass(item.state)}`}>{item.state}</span></td>
                    <td>
                      <strong>{item.employeeName}</strong><small>{item.employeeCode}</small>
                      <small>{item.title}</small>
                    </td>
                    <td><strong>{item.projectName}</strong><small>{item.projectCode}</small></td>
                    <td>
                      <strong>{item.assignmentNote || 'Follow up on this attention item.'}</strong>
                      <small>Assigned {formatDateTime(item.assignedAtUtc)}{item.assignedByEmail ? ` by ${item.assignedByEmail}` : ''}</small>
                      {item.state === 'Resolved' && <small>Resolved {formatDateTime(item.resolvedAtUtc)}{item.resolvedByEmail ? ` by ${item.resolvedByEmail}` : ''}{item.resolutionNote ? ` · ${item.resolutionNote}` : ''}</small>}
                    </td>
                    <td>
                      <strong>{formatDateTime(item.dueAtUtc)}</strong>
                      {item.taskDueDate && <small>Target due {formatDate(item.taskDueDate)}</small>}
                    </td>
                    <td><span className={`status-badge status-${statusClass(workStatusLabel(item.taskStatus))}`}>{workStatusLabel(item.taskStatus)}</span></td>
                    <td>
                      <NavLink className="text-link" to="/website-work">Open Website Work →</NavLink>
                      {item.state !== 'Resolved' && <button className="ghost-button" type="button" onClick={() => openResolve(item)} style={{ marginTop: 8 }}>Resolve</button>}
                    </td>
                  </tr>
                ))}
                {!data?.items.length && <tr><td colSpan={7} className="empty-cell">No follow-ups are currently assigned to you.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
      </article>

      {selected && selected.state !== 'Resolved' && <article className="panel">
        <div className="panel-heading">
          <div><h2>Resolve follow-up</h2><p>{selected.employeeName} · {selected.title}</p></div>
          <button className="ghost-button" type="button" onClick={() => { setResolveTaskId(null); setResolutionNote(''); }}>Cancel</button>
        </div>
        <form className="stack-lg" onSubmit={event => void resolveFollowUp(event)} style={{ marginTop: 0 }}>
          <label>
            <span>Resolution note (optional)</span>
            <textarea value={resolutionNote} onChange={event => setResolutionNote(event.target.value)} maxLength={1000} rows={4} placeholder="What did you check or resolve?" />
          </label>
          <button className="primary-button" type="submit" disabled={busy}>{busy ? 'Resolving…' : 'Mark follow-up resolved'}</button>
        </form>
      </article>}
    </>
  );
}
