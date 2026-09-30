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

function statusLabel(status: WorkStatus): string {
  return status === 'ToDo' ? 'Ready' : status === 'InProgress' ? 'Working' : status === 'Done' ? 'Completed' : status;
}

function statusClass(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

export default function WebsiteWorkManagementPage() {
  const { can } = useAuth();
  const mayManage = can('tasks.manage');
  const [items, setItems] = useState<WebsiteWork[]>([]);
  const [projects, setProjects] = useState<ProjectOption[]>([]);
  const [members, setMembers] = useState<EmployeeOption[]>([]);
  const [completions, setCompletions] = useState<WebsiteWorkCompletion[]>([]);
  const [draft, setDraft] = useState<WorkDraft>(emptyDraft);
  const [editing, setEditing] = useState<WebsiteWork | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);

  const workingCount = useMemo(() => items.filter(item => item.status === 'InProgress').length, [items]);
  const readyCount = useMemo(() => items.filter(item => item.status === 'ToDo').length, [items]);

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
    };
    window.addEventListener(websiteWorkCompletedEvent, handler);
    return () => window.removeEventListener(websiteWorkCompletedEvent, handler);
  }, []);

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
      setVersion(value => value + 1);
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
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to cancel website work.');
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
          <p className="muted">Assign a website plus a concrete target. Opening the link moves the employee to Working; the employee marks completion manually when the target is finished.</p>
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
        <article className="metric-card"><span>Working</span><strong>{workingCount}</strong><small>Link opened / target in progress</small></article>
        <article className="metric-card"><span>Completed shown</span><strong>{completions.length}</strong><small>Recent durable completions</small></article>
      </div>

      {notice && <div className="success-banner">{notice}</div>}
      {error && <div className="error-banner">{error}</div>}

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
        <div className="panel-heading"><div><h2>Website target register</h2><p>{items.length} loaded · {workingCount} currently working</p></div><button className="ghost-button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>
        {loading ? <div className="loading-block">Loading website work…</div> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Target</th><th>Worker</th><th>Project</th><th>Website</th><th>Due</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead>
            <tbody>
              {items.map(item => (
                <tr key={item.id}>
                  <td><strong>{item.title}</strong><small>{item.instructions || 'No extra instructions'}</small></td>
                  <td>{item.employeeName || 'Unassigned'}<small>{item.employeeCode || '—'}</small></td>
                  <td>{item.projectName}<small>{item.projectCode}</small></td>
                  <td><span title={item.url}>{new URL(item.url).hostname}</span></td>
                  <td>{item.dueDate || '—'}</td>
                  <td><span className={`status-badge status-${statusClass(statusLabel(item.status))}`}>{statusLabel(item.status)}</span>{item.startedAtUtc && item.status === 'InProgress' && <small>Since {formatDateTime(item.startedAtUtc)}</small>}</td>
                  {mayManage && <td className="action-cell"><button className="text-button" disabled={item.status === 'Done' || item.status === 'Cancelled'} onClick={() => editWork(item)}>Edit</button>{item.status !== 'Done' && item.status !== 'Cancelled' && <button className="text-button danger" disabled={busy} onClick={() => void cancelWork(item)}>Cancel</button>}</td>}
                </tr>
              ))}
              {!items.length && <tr><td colSpan={mayManage ? 7 : 6} className="empty-cell">No website work assignments found.</td></tr>}
            </tbody>
          </table></div>
        )}
      </article>

      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>Recent completion notifications</h2><p>Stored from employee completion actions, not just realtime popups.</p></div></div>
        <div className="table-wrap"><table>
          <thead><tr><th>Completed</th><th>Worker</th><th>Work</th><th>Project</th></tr></thead>
          <tbody>
            {completions.map(item => <tr key={`${item.taskId}-${item.completedAtUtc}`}><td>{formatDateTime(item.completedAtUtc)}</td><td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td><td>{item.taskTitle}</td><td>{item.projectName}</td></tr>)}
            {!completions.length && <tr><td colSpan={4} className="empty-cell">No website work has been completed yet.</td></tr>}
          </tbody>
        </table></div>
      </article>
    </>
  );
}
