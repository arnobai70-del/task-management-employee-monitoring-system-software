import { useEffect, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

interface Department {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  employeeCount: number;
}

interface Role {
  id: string;
  name: string;
  isActive: boolean;
}

interface EmployeeOption {
  id: string;
  employeeCode: string;
  fullName: string;
}

interface EmployeeRecord {
  id: string;
  userId: string;
  employeeCode: string;
  fullName: string;
  email: string;
  jobTitle: string;
  phone: string | null;
  employmentType: 'FullTime' | 'PartTime' | 'Contract' | 'Intern' | 'Temporary';
  joinedOn: string | null;
  isActive: boolean;
  departmentId: string | null;
  departmentName: string | null;
  supervisorEmployeeId: string | null;
  supervisorName: string | null;
  roles: Role[];
}

interface EmployeeDraft {
  employeeCode: string;
  fullName: string;
  email: string;
  password: string;
  jobTitle: string;
  phone: string;
  employmentType: EmployeeRecord['employmentType'];
  joinedOn: string;
  departmentId: string;
  supervisorEmployeeId: string;
  isActive: boolean;
  roleIds: string[];
}

const emptyEmployee: EmployeeDraft = {
  employeeCode: '', fullName: '', email: '', password: '', jobTitle: '', phone: '',
  employmentType: 'FullTime', joinedOn: '', departmentId: '', supervisorEmployeeId: '', isActive: true, roleIds: []
};

function Status({ active }: { active: boolean }) {
  return <span className={`status-badge status-${active ? 'active' : 'inactive'}`}>{active ? 'Active' : 'Inactive'}</span>;
}

export function DepartmentManagementPage() {
  const { can } = useAuth();
  const mayManage = can('departments.manage');
  const [items, setItems] = useState<Department[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [version, setVersion] = useState(0);
  const [editing, setEditing] = useState<Department | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [isActive, setIsActive] = useState(true);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    apiFetch<Department[]>('/api/departments')
      .then(data => { if (!cancelled) setItems(data); })
      .catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load departments.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [version]);

  function openCreate() {
    setEditing(null); setCode(''); setName(''); setIsActive(true); setError(''); setNotice(''); setShowForm(true);
  }

  function openEdit(item: Department) {
    setEditing(item); setCode(item.code); setName(item.name); setIsActive(item.isActive); setError(''); setNotice(''); setShowForm(true);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/departments${editing ? `/${editing.id}` : ''}`, {
        method: editing ? 'PUT' : 'POST',
        body: JSON.stringify(editing ? { code, name, isActive } : { code, name })
      });
      setNotice(`Department ${editing ? 'updated' : 'created'} successfully.`);
      setShowForm(false); setEditing(null); setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save department.');
    } finally { setBusy(false); }
  }

  async function deactivate(item: Department) {
    if (!mayManage || !window.confirm(`Deactivate ${item.name}? Existing employee history will be retained.`)) return;
    setBusy(true); setError('');
    try {
      await apiFetch(`/api/departments/${item.id}`, { method: 'PUT', body: JSON.stringify({ code: item.code, name: item.name, isActive: false }) });
      setNotice('Department deactivated.'); setVersion(value => value + 1);
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Unable to deactivate department.'); }
    finally { setBusy(false); }
  }

  return <>
    <div className="page-header"><div><p className="eyebrow">Organization</p><h1>Departments</h1><p className="muted">Manage department codes and lifecycle without deleting historical references.</p></div>{mayManage && <button className="primary-button" onClick={openCreate}>New department</button>}</div>
    {notice && <div className="success-banner">{notice}</div>}{error && <div className="error-banner">{error}</div>}
    {showForm && mayManage && <article className="panel management-form-panel"><div className="panel-heading"><div><h2>{editing ? 'Edit department' : 'Create department'}</h2></div><button className="ghost-button" onClick={() => setShowForm(false)}>Close</button></div><form className="form-grid" onSubmit={submit}>
      <label><span>Code</span><input required maxLength={50} value={code} onChange={event => setCode(event.target.value)} /></label>
      <label><span>Name</span><input required minLength={2} maxLength={150} value={name} onChange={event => setName(event.target.value)} /></label>
      {editing && <label className="check-field"><input type="checkbox" checked={isActive} onChange={event => setIsActive(event.target.checked)} /><span>Active</span></label>}
      <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button><button type="button" className="ghost-button" onClick={() => setShowForm(false)}>Cancel</button></div>
    </form></article>}
    <article className="panel table-panel"><div className="panel-heading"><div><h2>Department directory</h2><p>{items.length} departments</p></div><button className="ghost-button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>{loading ? <div className="loading-block">Loading…</div> : <div className="table-wrap"><table><thead><tr><th>Code</th><th>Name</th><th>Employees</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead><tbody>{items.map(item => <tr key={item.id}><td><strong>{item.code}</strong></td><td>{item.name}</td><td>{item.employeeCount}</td><td><Status active={item.isActive} /></td>{mayManage && <td className="action-cell"><button className="text-button" onClick={() => openEdit(item)}>Edit</button>{item.isActive && <button className="text-button danger" disabled={busy} onClick={() => void deactivate(item)}>Deactivate</button>}</td>}</tr>)}</tbody></table></div>}</article>
  </>;
}

export function EmployeeManagementPage() {
  const { can } = useAuth();
  const mayManage = can('employees.manage') && can('roles.manage');
  const [items, setItems] = useState<EmployeeRecord[]>([]);
  const [departments, setDepartments] = useState<Array<{ id: string; code: string; name: string }>>([]);
  const [roles, setRoles] = useState<Array<{ id: string; name: string }>>([]);
  const [supervisors, setSupervisors] = useState<EmployeeOption[]>([]);
  const [draft, setDraft] = useState<EmployeeDraft>(emptyEmployee);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true); setError('');
    const suffix = search ? `&search=${encodeURIComponent(search)}` : '';
    Promise.all([
      apiFetch<PagedResponse<EmployeeRecord>>(`/api/employees?page=1&pageSize=100${suffix}`),
      apiFetch<Array<{ id: string; code: string; name: string }>>('/api/admin-lookups/employees/departments'),
      apiFetch<Array<{ id: string; name: string }>>('/api/admin-lookups/employees/roles'),
      apiFetch<EmployeeOption[]>('/api/admin-lookups/employees/supervisors')
    ]).then(([employees, departmentOptions, roleOptions, supervisorOptions]) => {
      if (cancelled) return;
      setItems(employees.items); setDepartments(departmentOptions); setRoles(roleOptions); setSupervisors(supervisorOptions);
    }).catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load employee administration data.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [search, version]);

  function openCreate() { setEditingId(null); setDraft(emptyEmployee); setError(''); setNotice(''); setShowForm(true); }
  function openEdit(item: EmployeeRecord) {
    setEditingId(item.id);
    setDraft({ employeeCode: item.employeeCode, fullName: item.fullName, email: item.email, password: '', jobTitle: item.jobTitle, phone: item.phone || '', employmentType: item.employmentType, joinedOn: item.joinedOn || '', departmentId: item.departmentId || '', supervisorEmployeeId: item.supervisorEmployeeId || '', isActive: item.isActive, roleIds: item.roles.filter(role => role.isActive).map(role => role.id) });
    setError(''); setNotice(''); setShowForm(true);
  }
  function toggleRole(roleId: string) { setDraft(value => ({ ...value, roleIds: value.roleIds.includes(roleId) ? value.roleIds.filter(id => id !== roleId) : [...value.roleIds, roleId] })); }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!mayManage) return;
    setBusy(true); setError(''); setNotice('');
    const body = { employeeCode: draft.employeeCode, fullName: draft.fullName, email: draft.email, jobTitle: draft.jobTitle, phone: draft.phone || null, employmentType: draft.employmentType, joinedOn: draft.joinedOn || null, departmentId: draft.departmentId || null, supervisorEmployeeId: draft.supervisorEmployeeId || null, roleIds: draft.roleIds, isActive: draft.isActive, ...(editingId ? {} : { password: draft.password }) };
    try {
      await apiFetch(`/api/employees${editingId ? `/${editingId}` : ''}`, { method: editingId ? 'PUT' : 'POST', body: JSON.stringify(body) });
      setNotice(`Employee ${editingId ? 'updated' : 'created'} successfully.`); setShowForm(false); setEditingId(null); setVersion(value => value + 1);
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Unable to save employee.'); }
    finally { setBusy(false); }
  }

  async function deactivate(item: EmployeeRecord) {
    if (!mayManage || !window.confirm(`Deactivate ${item.fullName}? The linked login and active refresh tokens will be disabled/revoked.`)) return;
    setBusy(true); setError('');
    try {
      await apiFetch(`/api/employees/${item.id}`, { method: 'PUT', body: JSON.stringify({ employeeCode: item.employeeCode, fullName: item.fullName, email: item.email, jobTitle: item.jobTitle, phone: item.phone, employmentType: item.employmentType, joinedOn: item.joinedOn, departmentId: item.departmentId, supervisorEmployeeId: item.supervisorEmployeeId, isActive: false, roleIds: item.roles.filter(role => role.isActive).map(role => role.id) }) });
      setNotice('Employee deactivated and active sessions revoked.'); setVersion(value => value + 1);
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Unable to deactivate employee.'); }
    finally { setBusy(false); }
  }

  return <>
    <div className="page-header"><div><p className="eyebrow">People administration</p><h1>Employees & roles</h1><p className="muted">Provision accounts, reporting lines, departments and role assignments.</p></div><div className="header-actions"><form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); }}><input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Search employees" /><button className="ghost-button">Search</button></form>{mayManage && <button className="primary-button" onClick={openCreate}>New employee</button>}</div></div>
    {!mayManage && can('employees.manage') && <div className="error-banner">Employee create/update with role assignment also requires <strong>roles.manage</strong>.</div>}
    {notice && <div className="success-banner">{notice}</div>}{error && <div className="error-banner">{error}</div>}
    {showForm && mayManage && <article className="panel management-form-panel"><div className="panel-heading"><div><h2>{editingId ? 'Edit employee' : 'Create employee'}</h2><p>Role changes are protected separately by the backend.</p></div><button className="ghost-button" onClick={() => setShowForm(false)}>Close</button></div><form className="form-grid" onSubmit={submit}>
      <label><span>Employee code</span><input required maxLength={50} value={draft.employeeCode} onChange={event => setDraft(value => ({ ...value, employeeCode: event.target.value }))} /></label>
      <label><span>Full name</span><input required minLength={2} maxLength={200} value={draft.fullName} onChange={event => setDraft(value => ({ ...value, fullName: event.target.value }))} /></label>
      <label><span>Email</span><input type="email" required maxLength={320} value={draft.email} onChange={event => setDraft(value => ({ ...value, email: event.target.value }))} /></label>
      {!editingId && <label><span>Initial password</span><input type="password" required minLength={12} maxLength={256} autoComplete="new-password" value={draft.password} onChange={event => setDraft(value => ({ ...value, password: event.target.value }))} /><small>Minimum 12 characters. The server stores only the password hash.</small></label>}
      <label><span>Job title</span><input required minLength={2} maxLength={150} value={draft.jobTitle} onChange={event => setDraft(value => ({ ...value, jobTitle: event.target.value }))} /></label>
      <label><span>Phone</span><input maxLength={50} value={draft.phone} onChange={event => setDraft(value => ({ ...value, phone: event.target.value }))} /></label>
      <label><span>Employment type</span><select value={draft.employmentType} onChange={event => setDraft(value => ({ ...value, employmentType: event.target.value as EmployeeRecord['employmentType'] }))}><option>FullTime</option><option>PartTime</option><option>Contract</option><option>Intern</option><option>Temporary</option></select></label>
      <label><span>Joined on</span><input type="date" value={draft.joinedOn} onChange={event => setDraft(value => ({ ...value, joinedOn: event.target.value }))} /></label>
      <label><span>Department</span><select value={draft.departmentId} onChange={event => setDraft(value => ({ ...value, departmentId: event.target.value }))}><option value="">No department</option>{departments.map(item => <option key={item.id} value={item.id}>{item.name} ({item.code})</option>)}</select></label>
      <label><span>Supervisor</span><select value={draft.supervisorEmployeeId} onChange={event => setDraft(value => ({ ...value, supervisorEmployeeId: event.target.value }))}><option value="">No supervisor</option>{supervisors.filter(item => item.id !== editingId).map(item => <option key={item.id} value={item.id}>{item.fullName} ({item.employeeCode})</option>)}</select></label>
      <fieldset className="wide-field role-fieldset"><legend>Roles</legend><div className="role-grid">{roles.map(role => <label className="check-field" key={role.id}><input type="checkbox" checked={draft.roleIds.includes(role.id)} onChange={() => toggleRole(role.id)} /><span>{role.name}</span></label>)}</div></fieldset>
      {editingId && <label className="check-field"><input type="checkbox" checked={draft.isActive} onChange={event => setDraft(value => ({ ...value, isActive: event.target.checked }))} /><span>Active employee/login</span></label>}
      <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Saving…' : 'Save employee'}</button><button type="button" className="ghost-button" onClick={() => setShowForm(false)}>Cancel</button></div>
    </form></article>}
    <article className="panel table-panel"><div className="panel-heading"><div><h2>Employee directory</h2><p>{items.length} loaded</p></div><button className="ghost-button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>{loading ? <div className="loading-block">Loading…</div> : <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Job / department</th><th>Supervisor</th><th>Roles</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead><tbody>{items.map(item => <tr key={item.id}><td><strong>{item.fullName}</strong><small>{item.employeeCode} · {item.email}</small></td><td>{item.jobTitle}<small>{item.departmentName || 'No department'}</small></td><td>{item.supervisorName || '—'}</td><td>{item.roles.map(role => role.name).join(', ') || '—'}</td><td><Status active={item.isActive} /></td>{mayManage && <td className="action-cell"><button className="text-button" onClick={() => openEdit(item)}>Edit</button>{item.isActive && <button className="text-button danger" disabled={busy} onClick={() => void deactivate(item)}>Deactivate</button>}</td>}</tr>)}</tbody></table></div>}</article>
  </>;
}
