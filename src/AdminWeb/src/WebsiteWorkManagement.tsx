import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import { websiteWorkCompletedEvent, type WebsiteWorkCompletion } from './WebsiteWorkRealtimeNotice';
import './management.css';

interface ProjectOption { id: string; code: string; name: string; status: string; }
interface EmployeeOption { id: string; employeeCode: string; fullName: string; }

type WorkStatus = 'ToDo' | 'InProgress' | 'Blocked' | 'Done' | 'Cancelled';
type WorkPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
type ReviewState = 'NotSubmitted' | 'PendingReview' | 'Approved' | 'CorrectionRequired';

interface WebsiteWork {
  id: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  employeeId: string | null;
  employeeCode: string | null;
  employeeName: string | null;
  title: string;
  instructions: string | null;
  url: string;
  status: WorkStatus;
  priority: WorkPriority;
  dueDate: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  reviewState: ReviewState;
  reviewComment: string | null;
  submittedAtUtc: string | null;
  reviewedAtUtc: string | null;
}

interface WebsiteWorkActiveProgress {
  taskId: string;
  projectId: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  taskTitle: string;
  startedAtUtc: string;
  elapsedSeconds: number;
  dueDate: string | null;
  isOverdue: boolean;
}

interface WebsiteWorkEmployeeToday {
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  workingNow: number;
  pendingReview: number;
  submittedToday: number;
  approvedToday: number;
  reopenedToday: number;
  lastSubmittedAtUtc: string | null;
  lastApprovedAtUtc: string | null;
}

interface WebsiteWorkProgress {
  generatedAtUtc: string;
  utcOffsetMinutes: number;
  workingNow: number;
  pendingReview: number;
  submittedToday: number;
  approvedToday: number;
  reopenedToday: number;
  activeWork: WebsiteWorkActiveProgress[];
  employees: WebsiteWorkEmployeeToday[];
}

interface WorkDraft {
  projectId: string;
  employeeId: string;
  title: string;
  instructions: string;
  url: string;
  priority: WorkPriority;
  dueDate: string;
}

const emptyDraft: WorkDraft = {
  projectId: '',
  employeeId: '',
  title: '',
  instructions: '',
  url: '',
  priority: 'Normal',
  dueDate: ''
};

function workStatusLabel(item: WebsiteWork): string {
  if (item.reviewState === 'PendingReview') return 'Pending Review';
  if (item.reviewState === 'Approved') return 'Approved';
  if (item.reviewState === 'CorrectionRequired') return 'Correction Required';
  return item.status === 'ToDo' ? 'Ready' : item.status === 'InProgress' ? 'Working' : item.status === 'Done' ? 'Completed' : item.status;
}

function statusClass(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function formatWorkingDuration(startedAtUtc: string, nowMs: number): string {
  const startedMs = new Date(startedAtUtc).getTime();
  if (Number.isNaN(startedMs)) return '—';
  const totalSeconds = Math.max(0, Math.floor((nowMs - startedMs) / 1000));
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) return `${days}d ${hours}h ${minutes}m`;
  if (hours > 0) return `${hours}h ${minutes}m ${seconds}s`;
  return `${minutes}m ${seconds}s`;
}

export default function WebsiteWorkManagementPage() {
  const { can } = useAuth();
  const mayManage = can('tasks.manage');
  const [items, setItems] = useState<WebsiteWork[]>([]);
  const [projects, setProjects] = useState<ProjectOption[]>([]);
  const [members, setMembers] = useState<EmployeeOption[]>([]);
  const [completions, setCompletions] = useState<WebsiteWorkCompletion[]>([]);
  const [progress, setProgress] = useState<WebsiteWorkProgress | null>(null);
  const [draft, setDraft] = useState<WorkDraft>(emptyDraft);
  const [editing, setEditing] = useState<WebsiteWork | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [progressLoading, setProgressLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [progressError, setProgressError] = useState('');
  const [version, setVersion] = useState(0);
  const [progressVersion, setProgressVersion] = useState(0);
  const [clockNow, setClockNow] = useState(() => Date.now());

  const readyCount = useMemo(() => items.filter(item => item.status === 'ToDo').length, [items]);
  const workingCount = progress?.workingNow ?? items.filter(item => item.status === 'InProgress').length;
  const pendingReview = progress?.pendingReview ?? items.filter(item => item.reviewState === 'PendingReview').length;
  const approvedToday = progress?.approvedToday ?? 0;

  useEffect(() => {
    const timer = window.setInterval(() => setClockNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    const suffix = search ? `&search=${encodeURIComponent(search)}` : '';
    Promise.all([
      apiFetch<PagedResponse<WebsiteWork>>(`/api/website-work?page=1&pageSize=100${suffix}`),
      apiFetch<ProjectOption[]>('/api/admin-lookups/tasks/projects'),
      apiFetch<PagedResponse<WebsiteWorkCompletion>>('/api/website-work/completions?page=1&pageSize=20')
    ])
      .then(([work, projectOptions, completionData]) => {
        if (!cancelled) {
          setItems(work.items);
          setProjects(projectOptions);
          setCompletions(completionData.items);
        }
      })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load website work.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  }, [search, version]);

  useEffect(() => {
    let cancelled = false;
    const utcOffsetMinutes = -new Date().getTimezoneOffset();

    const loadProgress = async (showLoading: boolean) => {
      if (showLoading) setProgressLoading(true);
      try {
        const snapshot = await apiFetch<WebsiteWorkProgress>(`/api/website-work/progress?utcOffsetMinutes=${utcOffsetMinutes}`);
        if (!cancelled) {
          setProgress(snapshot);
          setProgressError('');
        }
      } catch (caught) {
        if (!cancelled) setProgressError(caught instanceof Error ? caught.message : 'Unable to load live work progress.');
      } finally {
        if (!cancelled && showLoading) setProgressLoading(false);
      }
    };

    void loadProgress(true);
    const timer = window.setInterval(() => void loadProgress(false), 5_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [progressVersion]);

  useEffect(() => {
    if (!draft.projectId) {
      setMembers([]);
      return;
    }
    let cancelled = false;
    apiFetch<EmployeeOption[]>(`/api/admin-lookups/tasks/projects/${draft.projectId}/members`)
      .then(data => { if (!cancelled) setMembers(data); })
      .catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load project members.'); });
    return () => { cancelled = true; };
  }, [draft.projectId]);

  useEffect(() => {
    const handler = (event: Event) => {
      const completion = (event as CustomEvent<WebsiteWorkCompletion>).detail;
      if (!completion) return;
      setCompletions(current => [completion, ...current.filter(item => item.taskId !== completion.taskId)].slice(0, 20));
      setNotice(completion.message);
      setVersion(value => value + 1);
      setProgressVersion(value => value + 1);
    };
    window.addEventListener(websiteWorkCompletedEvent, handler);
    return () => window.removeEventListener(websiteWorkCompletedEvent, handler);
  }, []);

  function refreshAll() {
    setVersion(value => value + 1);
    setProgressVersion(value => value + 1);
  }

  function newWork() {
    setEditing(null);
    setDraft(emptyDraft);
    setShowForm(true);
    setNotice('');
    setError('');
  }

  function editWork(item: WebsiteWork) {
    setEditing(item);
    setDraft({
      projectId: item.projectId,
      employeeId: item.employeeId || '',
      title: item.title,
      instructions: item.instructions || '',
      url: item.url,
      priority: item.priority,
      dueDate: item.dueDate || ''
    });
    setShowForm(true);
    setNotice('');
    setError('');
  }

  async function saveWork(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const payload = {
        projectId: draft.projectId,
        employeeId: draft.employeeId,
        title: draft.title,
        instructions: draft.instructions || null,
        url: draft.url,
        priority: draft.priority,
        dueDate: draft.dueDate || null
      };
      await apiFetch(`/api/website-work${editing ? `/${editing.id}` : ''}`, {
        method: editing ? 'PUT' : 'POST',
        body: JSON.stringify(payload)
      });
      setNotice(`Website work ${editing ? 'updated' : 'assigned'}.`);
      setShowForm(false);
      setEditing(null);
      refreshAll();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save website work.');
    } finally {
      setBusy(false);
    }
  }

  async function cancelWork(item: WebsiteWork) {
    if (!mayManage || !window.confirm(`Cancel "${item.title}"?`)) return;
    setBusy(true);
    setError('');
    try {
      await apiFetch(`/api/tasks/${item.id}/status`, { method: 'PUT', body: JSON.stringify({ status: 'Cancelled' }) });
      setNotice('Website work cancelled.');
      refreshAll();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to cancel website work.');
    } finally {
      setBusy(false);
    }
  }

  async function approveWork(item: WebsiteWork) {
    if (!mayManage || item.reviewState !== 'PendingReview') return;
    const comment = window.prompt('Optional approval note:', '')?.trim() || null;
    setBusy(true);
    setError('');
    try {
      await apiFetch(`/api/website-work/${item.id}/approve`, {
        method: 'POST',
        body: JSON.stringify({ comment })
      });
      setNotice(`${item.employeeName || 'Worker'} approved: ${item.title}`);
      refreshAll();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to approve website work.');
    } finally {
      setBusy(false);
    }
  }

  async function reopenWork(item: WebsiteWork) {
    if (!mayManage || item.reviewState !== 'PendingReview') return;
    const comment = window.prompt('What must the worker correct before resubmitting?')?.trim();
    if (!comment) return;
    if (comment.length < 3) {
      setError('Correction comment must contain at least 3 characters.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      await apiFetch(`/api/website-work/${item.id}/reopen`, {
        method: 'POST',
        body: JSON.stringify({ comment })
      });
      setNotice(`${item.employeeName || 'Worker'} was asked to correct: ${item.title}`);
      refreshAll();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to reopen website work.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Target work administration</p>
          <h1>Website Work</h1>
          <p className="muted">Assign website targets, watch live work, and review worker completion submissions before they become final.</p>
        </div>
        <div className="header-actions">
          <form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); }}>
            <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Search target or employee" />
            <button className="ghost-button">Search</button>
          </form>
          {mayManage && <button className="primary-button" onClick={newWork}>Assign website work</button>}
        </div>
      </div>

      <div className="metric-grid compact-metrics">
        <article className="metric-card"><span>Ready</span><strong>{readyCount}</strong><small>Waiting to be opened</small></article>
        <article className="metric-card"><span>Working now</span><strong>{workingCount}</strong><small>Live target activity</small></article>
        <article className="metric-card"><span>Pending review</span><strong>{pendingReview}</strong><small>Worker completion submissions</small></article>
        <article className="metric-card"><span>Approved today</span><strong>{approvedToday}</strong><small>Browser-local day</small></article>
      </div>

      {notice && <div className="success-banner">{notice}</div>}
      {error && <div className="error-banner">{error}</div>}
      {progressError && <div className="error-banner">Live progress delayed: {progressError}</div>}

      <section className="dashboard-grid website-work-live-grid">
        <article className="panel table-panel">
          <div className="panel-heading">
            <div><h2>Live work progress</h2><p>Auto-refresh every 5 seconds · duration updates every second</p></div>
            <button className="ghost-button" onClick={refreshAll}>Refresh now</button>
          </div>
          {progressLoading && !progress ? <div className="loading-block">Loading live progress…</div> : (
            <div className="table-wrap"><table>
              <thead><tr><th>Worker</th><th>Current target</th><th>Project</th><th>Working for</th><th>Started</th><th>Due</th></tr></thead>
              <tbody>
                {(progress?.activeWork || []).map(item => (
                  <tr key={item.taskId}>
                    <td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td>
                    <td><strong>{item.taskTitle}</strong><small><span className="status-badge status-working">Working</span></small></td>
                    <td>{item.projectName}</td>
                    <td><strong className="live-duration">{formatWorkingDuration(item.startedAtUtc, clockNow)}</strong></td>
                    <td>{formatDateTime(item.startedAtUtc)}</td>
                    <td>{item.dueDate || '—'}{item.isOverdue && <small><span className="status-badge status-urgent">Overdue</span></small>}</td>
                  </tr>
                ))}
                {!progress?.activeWork.length && <tr><td colSpan={6} className="empty-cell">No worker is currently on Website Work.</td></tr>}
              </tbody>
            </table></div>
          )}
        </article>

        <article className="panel table-panel">
          <div className="panel-heading"><div><h2>Today by worker</h2><p>Current browser timezone · submitted / approved / reopened review activity</p></div></div>
          <div className="table-wrap"><table>
            <thead><tr><th>Worker</th><th>Working</th><th>Pending</th><th>Submitted</th><th>Approved</th><th>Reopened</th><th>Last approved</th></tr></thead>
            <tbody>
              {(progress?.employees || []).map(item => (
                <tr key={item.employeeId}>
                  <td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td>
                  <td>{item.workingNow}</td>
                  <td><strong>{item.pendingReview}</strong></td>
                  <td>{item.submittedToday}</td>
                  <td><strong>{item.approvedToday}</strong></td>
                  <td>{item.reopenedToday}</td>
                  <td>{formatDateTime(item.lastApprovedAtUtc)}</td>
                </tr>
              ))}
              {!progress?.employees.length && <tr><td colSpan={7} className="empty-cell">No Website Work activity today.</td></tr>}
            </tbody>
          </table></div>
        </article>
      </section>

      {showForm && mayManage && (
        <article className="panel management-form-panel">
          <div className="panel-heading"><h2>{editing ? 'Edit website work' : 'Assign website work'}</h2><button className="ghost-button" onClick={() => setShowForm(false)}>Close</button></div>
          <form className="form-grid" onSubmit={saveWork}>
            <label>
              <span>Project</span>
              <select required disabled={Boolean(editing)} value={draft.projectId} onChange={event => setDraft(value => ({ ...value, projectId: event.target.value, employeeId: '' }))}>
                <option value="">Select project…</option>
                {projects.map(item => <option key={item.id} value={item.id}>{item.name} ({item.code})</option>)}
              </select>
            </label>
            <label>
              <span>Worker</span>
              <select required value={draft.employeeId} onChange={event => setDraft(value => ({ ...value, employeeId: event.target.value }))}>
                <option value="">Select project member…</option>
                {members.map(item => <option key={item.id} value={item.id}>{item.fullName} ({item.employeeCode})</option>)}
              </select>
            </label>
            <label className="wide-field">
              <span>Work target</span>
              <input required minLength={2} maxLength={200} value={draft.title} onChange={event => setDraft(value => ({ ...value, title: event.target.value }))} placeholder="Example: InboxDollars - complete $5 target" />
            </label>
            <label className="wide-field">
              <span>Website URL</span>
              <input type="url" required maxLength={2048} value={draft.url} onChange={event => setDraft(value => ({ ...value, url: event.target.value }))} placeholder="https://..." />
              <small>The worker opens this link from the Windows app. Embedded URL credentials are rejected.</small>
            </label>
            <label><span>Priority</span><select value={draft.priority} onChange={event => setDraft(value => ({ ...value, priority: event.target.value as WorkPriority }))}><option>Low</option><option>Normal</option><option>High</option><option>Urgent</option></select></label>
            <label><span>Due date</span><input type="date" value={draft.dueDate} onChange={event => setDraft(value => ({ ...value, dueDate: event.target.value }))} /></label>
            <label className="wide-field"><span>Instructions</span><textarea maxLength={4000} value={draft.instructions} onChange={event => setDraft(value => ({ ...value, instructions: event.target.value }))} placeholder="Optional details about the target" /></label>
            <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Assign work'}</button></div>
          </form>
        </article>
      )}

      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>Website target register</h2><p>{items.length} loaded · {workingCount} working · {pendingReview} pending review</p></div><button className="ghost-button" onClick={refreshAll}>Refresh</button></div>
        {loading ? <div className="loading-block">Loading website work…</div> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Target</th><th>Worker</th><th>Project</th><th>Website</th><th>Due</th><th>Status / review</th>{mayManage && <th>Actions</th>}</tr></thead>
            <tbody>
              {items.map(item => {
                const label = workStatusLabel(item);
                const reviewLocked = item.reviewState === 'PendingReview' || item.reviewState === 'Approved';
                return (
                  <tr key={item.id}>
                    <td><strong>{item.title}</strong><small>{item.instructions || 'No extra instructions'}</small></td>
                    <td>{item.employeeName || 'Unassigned'}<small>{item.employeeCode || '—'}</small></td>
                    <td>{item.projectName}<small>{item.projectCode}</small></td>
                    <td><span title={item.url}>{new URL(item.url).hostname}</span></td>
                    <td>{item.dueDate || '—'}</td>
                    <td>
                      <span className={`status-badge status-${statusClass(label)}`}>{label}</span>
                      {item.status === 'InProgress' && item.startedAtUtc && <small>Since {formatDateTime(item.startedAtUtc)}</small>}
                      {item.submittedAtUtc && item.reviewState === 'PendingReview' && <small>Submitted {formatDateTime(item.submittedAtUtc)}</small>}
                      {item.reviewComment && <small>Manager note: {item.reviewComment}</small>}
                      {item.reviewState === 'Approved' && item.reviewedAtUtc && <small>Approved {formatDateTime(item.reviewedAtUtc)}</small>}
                    </td>
                    {mayManage && <td className="action-cell">
                      {item.reviewState === 'PendingReview' && <button className="text-button" disabled={busy} onClick={() => void approveWork(item)}>Approve</button>}
                      {item.reviewState === 'PendingReview' && <button className="text-button danger" disabled={busy} onClick={() => void reopenWork(item)}>Reopen</button>}
                      <button className="text-button" disabled={reviewLocked || item.status === 'Done' || item.status === 'Cancelled'} onClick={() => editWork(item)}>Edit</button>
                      {item.status !== 'Done' && item.status !== 'Cancelled' && <button className="text-button danger" disabled={busy} onClick={() => void cancelWork(item)}>Cancel</button>}
                    </td>}
                  </tr>
                );
              })}
              {!items.length && <tr><td colSpan={mayManage ? 7 : 6} className="empty-cell">No website work assignments found.</td></tr>}
            </tbody>
          </table></div>
        )}
      </article>

      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>Recent completion submissions</h2><p>Durable worker submissions waiting for or already processed by manager review.</p></div></div>
        <div className="table-wrap"><table>
          <thead><tr><th>Submitted</th><th>Worker</th><th>Work</th><th>Project</th></tr></thead>
          <tbody>
            {completions.map(item => <tr key={`${item.taskId}-${item.completedAtUtc}`}><td>{formatDateTime(item.completedAtUtc)}</td><td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td><td>{item.taskTitle}</td><td>{item.projectName}</td></tr>)}
            {!completions.length && <tr><td colSpan={4} className="empty-cell">No website work completion has been submitted yet.</td></tr>}
          </tbody>
        </table></div>
      </article>
    </>
  );
}
