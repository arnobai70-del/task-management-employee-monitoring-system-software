import { useEffect, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

interface EmployeeOption {
  id: string;
  employeeCode: string;
  fullName: string;
}

interface ExternalSurveyAssignment {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  title: string;
  url: string;
  startsOn: string | null;
  dueDate: string | null;
  isActive: boolean;
  instructions: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface SurveyDraft {
  employeeId: string;
  title: string;
  url: string;
  startsOn: string;
  dueDate: string;
  isActive: boolean;
  instructions: string;
}

const emptyDraft: SurveyDraft = {
  employeeId: '',
  title: '',
  url: 'https://',
  startsOn: '',
  dueDate: '',
  isActive: true,
  instructions: ''
};

function dateOrNull(value: string): string | null {
  return value || null;
}

function Status({ active }: { active: boolean }) {
  return <span className={`status-badge status-${active ? 'active' : 'inactive'}`}>{active ? 'Active' : 'Inactive'}</span>;
}

export default function SurveyManagementPage() {
  const { can } = useAuth();
  const mayManage = can('surveys.manage');
  const [items, setItems] = useState<ExternalSurveyAssignment[]>([]);
  const [employees, setEmployees] = useState<EmployeeOption[]>([]);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [draft, setDraft] = useState<SurveyDraft>(emptyDraft);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    const suffix = search ? `&search=${encodeURIComponent(search)}` : '';
    const requests: Promise<unknown>[] = [apiFetch<PagedResponse<ExternalSurveyAssignment>>(`/api/survey-links?page=1&pageSize=100${suffix}`)];
    if (mayManage) requests.push(apiFetch<EmployeeOption[]>('/api/survey-links/employees'));

    Promise.all(requests)
      .then(results => {
        if (cancelled) return;
        const page = results[0] as PagedResponse<ExternalSurveyAssignment>;
        setItems(page.items);
        setEmployees(mayManage ? results[1] as EmployeeOption[] : []);
      })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load survey website assignments.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    return () => { cancelled = true; };
  }, [search, version, mayManage]);

  function openCreate() {
    setEditingId(null);
    setDraft(emptyDraft);
    setShowForm(true);
    setError('');
    setNotice('');
  }

  function edit(item: ExternalSurveyAssignment) {
    setEditingId(item.id);
    setDraft({
      employeeId: item.employeeId,
      title: item.title,
      url: item.url,
      startsOn: item.startsOn || '',
      dueDate: item.dueDate || '',
      isActive: item.isActive,
      instructions: item.instructions || ''
    });
    setShowForm(true);
    setError('');
    setNotice('');
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const payload = {
        employeeId: draft.employeeId,
        title: draft.title.trim(),
        url: draft.url.trim(),
        startsOn: dateOrNull(draft.startsOn),
        dueDate: dateOrNull(draft.dueDate),
        isActive: draft.isActive,
        instructions: draft.instructions.trim() || null
      };
      await apiFetch(`/api/survey-links${editingId ? `/${editingId}` : ''}`, {
        method: editingId ? 'PUT' : 'POST',
        body: JSON.stringify(payload)
      });
      setNotice(`Survey website assignment ${editingId ? 'updated' : 'created'} successfully.`);
      setShowForm(false);
      setEditingId(null);
      setDraft(emptyDraft);
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save survey website assignment.');
    } finally {
      setBusy(false);
    }
  }

  async function deactivate(item: ExternalSurveyAssignment) {
    if (!mayManage || !item.isActive) return;
    if (!window.confirm(`Deactivate “${item.title}” for ${item.employeeName}?`)) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      await apiFetch(`/api/survey-links/${item.id}`, {
        method: 'PUT',
        body: JSON.stringify({
          employeeId: item.employeeId,
          title: item.title,
          url: item.url,
          startsOn: item.startsOn,
          dueDate: item.dueDate,
          isActive: false,
          instructions: item.instructions
        })
      });
      setNotice('Survey website assignment deactivated.');
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to deactivate survey website assignment.');
    } finally {
      setBusy(false);
    }
  }

  return <>
    <div className="page-header">
      <div>
        <p className="eyebrow">Survey operations</p>
        <h1>Survey website assignments</h1>
        <p className="muted">Assign the website where the survey work already exists. Employees open the assigned URL from the Windows workspace and complete the work on that external site.</p>
      </div>
      <div className="header-actions">
        <form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); }}>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Search employee, title or URL" />
          <button className="ghost-button">Search</button>
        </form>
        {mayManage && <button className="primary-button" type="button" onClick={openCreate}>New survey assignment</button>}
      </div>
    </div>

    <div className="panel" style={{ marginBottom: '1rem' }}>
      <strong>External survey model</strong>
      <p className="muted" style={{ marginBottom: 0 }}>The boss does not build questionnaires here. This system stores only the employee assignment, survey website URL, schedule and instructions. No survey answers, website passwords, cookies or form contents are collected by this admin screen.</p>
    </div>

    {notice && <div className="success-banner" role="status">{notice}</div>}
    {error && <div className="error-banner" role="alert">{error}</div>}

    {showForm && mayManage && <article className="panel management-form-panel">
      <div className="panel-heading">
        <div><h2>{editingId ? 'Edit survey assignment' : 'Assign survey website'}</h2><p>Use the exact external URL the employee must open.</p></div>
        <button className="ghost-button" type="button" onClick={() => setShowForm(false)}>Close</button>
      </div>
      <form className="form-grid" onSubmit={save}>
        <label><span>Employee</span><select required value={draft.employeeId} onChange={event => setDraft(value => ({ ...value, employeeId: event.target.value }))}>
          <option value="">Select employee…</option>
          {employees.map(employee => <option key={employee.id} value={employee.id}>{employee.fullName} ({employee.employeeCode})</option>)}
        </select></label>
        <label><span>Assignment title</span><input required minLength={2} maxLength={200} value={draft.title} onChange={event => setDraft(value => ({ ...value, title: event.target.value }))} placeholder="Customer feedback survey" /></label>
        <label className="wide-field"><span>Survey website URL</span><input type="url" required maxLength={2048} value={draft.url} onChange={event => setDraft(value => ({ ...value, url: event.target.value }))} placeholder="https://survey-provider.example/form/123" /><small>Only HTTP/HTTPS links are accepted. Do not put usernames or passwords inside the URL.</small></label>
        <label><span>Starts on</span><input type="date" value={draft.startsOn} onChange={event => setDraft(value => ({ ...value, startsOn: event.target.value }))} /></label>
        <label><span>Due date</span><input type="date" value={draft.dueDate} onChange={event => setDraft(value => ({ ...value, dueDate: event.target.value }))} /></label>
        <label className="check-field"><input type="checkbox" checked={draft.isActive} onChange={event => setDraft(value => ({ ...value, isActive: event.target.checked }))} /><span>Active assignment</span></label>
        <label className="wide-field"><span>Instructions</span><textarea maxLength={2000} value={draft.instructions} onChange={event => setDraft(value => ({ ...value, instructions: event.target.value }))} placeholder="What the employee should do after opening the link." /></label>
        <div className="wide-field form-actions">
          <button className="primary-button" type="submit" disabled={busy}>{busy ? 'Saving…' : editingId ? 'Save changes' : 'Assign survey'}</button>
          <button className="ghost-button" type="button" onClick={() => setShowForm(false)}>Cancel</button>
        </div>
      </form>
    </article>}

    <article className="panel table-panel">
      <div className="panel-heading"><div><h2>Assigned external surveys</h2><p>{items.length} loaded</p></div><button className="ghost-button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>
      {loading ? <div className="loading-block">Loading current server data…</div> : <div className="table-wrap"><table>
        <thead><tr><th>Employee</th><th>Survey work</th><th>Schedule</th><th>Instructions</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead>
        <tbody>
          {items.map(item => <tr key={item.id}>
            <td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td>
            <td><strong>{item.title}</strong><small><a href={item.url} target="_blank" rel="noreferrer">{item.url}</a></small></td>
            <td>{item.startsOn || 'Available now'}<small>{item.dueDate ? `Due ${item.dueDate}` : 'No due date'}</small></td>
            <td>{item.instructions || '—'}</td>
            <td><Status active={item.isActive} /></td>
            {mayManage && <td className="action-cell"><button className="text-button" type="button" onClick={() => edit(item)}>Edit</button>{item.isActive && <button className="text-button danger" type="button" disabled={busy} onClick={() => void deactivate(item)}>Deactivate</button>}</td>}
          </tr>)}
          {!items.length && <tr><td colSpan={mayManage ? 6 : 5} className="empty-cell">No external survey assignments found.</td></tr>}
        </tbody>
      </table></div>}
    </article>
  </>;
}
