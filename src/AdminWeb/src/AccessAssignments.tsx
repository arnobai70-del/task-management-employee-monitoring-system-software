import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

type AccessKind = 'rdp' | 'ip' | 'websites';

interface EmployeeOption {
  id: string;
  employeeCode: string;
  fullName: string;
}

interface RdpAssignment {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  name: string;
  host: string;
  port: number;
  usernameReference: string | null;
  credentialReference: string | null;
  validFrom: string | null;
  expiresOn: string | null;
  isActive: boolean;
  notes: string | null;
}

interface IpAssignment {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  ipAddress: string;
  deviceName: string;
  macAddress: string | null;
  status: 'Active' | 'Reserved' | 'Released';
  assignedOn: string | null;
  releasedOn: string | null;
  notes: string | null;
}

interface WebsiteAssignment {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  name: string;
  url: string;
  usernameReference: string | null;
  accessLevel: 'View' | 'Work' | 'Admin';
  startsOn: string | null;
  expiresOn: string | null;
  isActive: boolean;
  notes: string | null;
}

type Assignment = RdpAssignment | IpAssignment | WebsiteAssignment;

interface RdpDraft {
  employeeId: string;
  name: string;
  host: string;
  port: string;
  usernameReference: string;
  credentialReference: string;
  validFrom: string;
  expiresOn: string;
  isActive: boolean;
  notes: string;
}

interface IpDraft {
  employeeId: string;
  ipAddress: string;
  deviceName: string;
  macAddress: string;
  status: 'Active' | 'Reserved' | 'Released';
  assignedOn: string;
  releasedOn: string;
  notes: string;
}

interface WebsiteDraft {
  employeeId: string;
  name: string;
  url: string;
  usernameReference: string;
  accessLevel: 'View' | 'Work' | 'Admin';
  startsOn: string;
  expiresOn: string;
  isActive: boolean;
  notes: string;
}

const emptyRdp: RdpDraft = {
  employeeId: '', name: '', host: '', port: '3389', usernameReference: '', credentialReference: '',
  validFrom: '', expiresOn: '', isActive: true, notes: ''
};
const emptyIp: IpDraft = {
  employeeId: '', ipAddress: '', deviceName: '', macAddress: '', status: 'Active', assignedOn: '', releasedOn: '', notes: ''
};
const emptyWebsite: WebsiteDraft = {
  employeeId: '', name: '', url: 'https://', usernameReference: '', accessLevel: 'Work', startsOn: '', expiresOn: '', isActive: true, notes: ''
};
const PAGE_SIZE = 100;

function dateOrNull(value: string): string | null {
  return value || null;
}

function Status({ value }: { value: string }) {
  return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>;
}

function EmployeeSelect({ employees, value, onChange }: { employees: EmployeeOption[]; value: string; onChange: (value: string) => void }) {
  return (
    <label><span>Employee</span><select value={value} onChange={event => onChange(event.target.value)} required>
      <option value="">Select employee…</option>
      {employees.map(employee => <option key={employee.id} value={employee.id}>{employee.fullName} ({employee.employeeCode})</option>)}
    </select></label>
  );
}

export default function AccessAssignmentsPage({ kind }: { kind: AccessKind }) {
  const { can } = useAuth();
  const mayManage = can('access.assignments.manage');
  const [employees, setEmployees] = useState<EmployeeOption[]>([]);
  const [items, setItems] = useState<Assignment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [version, setVersion] = useState(0);
  const [rdp, setRdp] = useState<RdpDraft>(emptyRdp);
  const [ip, setIp] = useState<IpDraft>(emptyIp);
  const [website, setWebsite] = useState<WebsiteDraft>(emptyWebsite);

  const config = useMemo(() => {
    if (kind === 'rdp') return { title: 'RDP Assignments', subtitle: 'Assign approved Remote Desktop endpoints without storing RDP passwords.', noun: 'RDP assignment' };
    if (kind === 'ip') return { title: 'IP Assignments', subtitle: 'Track active, reserved and released employee/device IP addresses.', noun: 'IP assignment' };
    return { title: 'Website Assignments', subtitle: 'Assign approved websites and access levels without storing website passwords or session cookies.', noun: 'website assignment' };
  }, [kind]);
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  useEffect(() => {
    setShowForm(false);
    setEditingId(null);
    setNotice('');
    setError('');
    setPage(1);
    setTotalCount(0);
  }, [kind]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    const searchPart = search ? `&search=${encodeURIComponent(search)}` : '';
    Promise.all([
      apiFetch<EmployeeOption[]>('/api/access-assignments/employees'),
      apiFetch<PagedResponse<Assignment>>(`/api/access-assignments/${kind}?page=${page}&pageSize=${PAGE_SIZE}${searchPart}`)
    ])
      .then(([employeeData, assignmentData]) => {
        if (cancelled) return;
        setEmployees(employeeData);
        setItems(assignmentData.items);
        setTotalCount(assignmentData.totalCount);
      })
      .catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load assignments.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [kind, page, search, version]);

  function resetForm() {
    setEditingId(null);
    setRdp(emptyRdp);
    setIp(emptyIp);
    setWebsite(emptyWebsite);
  }

  function openCreate() {
    resetForm();
    setNotice('');
    setError('');
    setShowForm(true);
  }

  function edit(item: Assignment) {
    setEditingId(item.id);
    setNotice('');
    setError('');
    if (kind === 'rdp') {
      const value = item as RdpAssignment;
      setRdp({ employeeId: value.employeeId, name: value.name, host: value.host, port: String(value.port), usernameReference: value.usernameReference || '', credentialReference: value.credentialReference || '', validFrom: value.validFrom || '', expiresOn: value.expiresOn || '', isActive: value.isActive, notes: value.notes || '' });
    } else if (kind === 'ip') {
      const value = item as IpAssignment;
      setIp({ employeeId: value.employeeId, ipAddress: value.ipAddress, deviceName: value.deviceName, macAddress: value.macAddress || '', status: value.status, assignedOn: value.assignedOn || '', releasedOn: value.releasedOn || '', notes: value.notes || '' });
    } else {
      const value = item as WebsiteAssignment;
      setWebsite({ employeeId: value.employeeId, name: value.name, url: value.url, usernameReference: value.usernameReference || '', accessLevel: value.accessLevel, startsOn: value.startsOn || '', expiresOn: value.expiresOn || '', isActive: value.isActive, notes: value.notes || '' });
    }
    setShowForm(true);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      let body: unknown;
      if (kind === 'rdp') {
        body = { ...rdp, port: Number(rdp.port), usernameReference: rdp.usernameReference || null, credentialReference: rdp.credentialReference || null, validFrom: dateOrNull(rdp.validFrom), expiresOn: dateOrNull(rdp.expiresOn), notes: rdp.notes || null };
      } else if (kind === 'ip') {
        body = { ...ip, macAddress: ip.macAddress || null, assignedOn: dateOrNull(ip.assignedOn), releasedOn: dateOrNull(ip.releasedOn), notes: ip.notes || null };
      } else {
        body = { ...website, usernameReference: website.usernameReference || null, startsOn: dateOrNull(website.startsOn), expiresOn: dateOrNull(website.expiresOn), notes: website.notes || null };
      }
      await apiFetch(`/api/access-assignments/${kind}${editingId ? `/${editingId}` : ''}`, { method: editingId ? 'PUT' : 'POST', body: JSON.stringify(body) });
      setNotice(`${config.noun} ${editingId ? 'updated' : 'created'} successfully.`);
      setShowForm(false);
      resetForm();
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : `Unable to save ${config.noun}.`);
    } finally {
      setBusy(false);
    }
  }

  async function quickDeactivate(item: Assignment) {
    if (!mayManage) return;
    if (!window.confirm(`Confirm ${kind === 'ip' ? 'release' : 'deactivation'}? History will be retained.`)) return;
    setBusy(true);
    setError('');
    try {
      if (kind === 'rdp') {
        const value = item as RdpAssignment;
        await apiFetch(`/api/access-assignments/rdp/${value.id}`, { method: 'PUT', body: JSON.stringify({ employeeId: value.employeeId, name: value.name, host: value.host, port: value.port, usernameReference: value.usernameReference, credentialReference: value.credentialReference, validFrom: value.validFrom, expiresOn: value.expiresOn, isActive: false, notes: value.notes }) });
      } else if (kind === 'ip') {
        const value = item as IpAssignment;
        await apiFetch(`/api/access-assignments/ip/${value.id}`, { method: 'PUT', body: JSON.stringify({ employeeId: value.employeeId, ipAddress: value.ipAddress, deviceName: value.deviceName, macAddress: value.macAddress, status: 'Released', assignedOn: value.assignedOn, releasedOn: new Date().toISOString().slice(0, 10), notes: value.notes }) });
      } else {
        const value = item as WebsiteAssignment;
        await apiFetch(`/api/access-assignments/websites/${value.id}`, { method: 'PUT', body: JSON.stringify({ employeeId: value.employeeId, name: value.name, url: value.url, usernameReference: value.usernameReference, accessLevel: value.accessLevel, startsOn: value.startsOn, expiresOn: value.expiresOn, isActive: false, notes: value.notes }) });
      }
      setNotice(kind === 'ip' ? 'IP assignment released.' : 'Assignment deactivated.');
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to update assignment.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="page-header">
        <div><p className="eyebrow">Access administration</p><h1>{config.title}</h1><p className="muted">{config.subtitle}</p></div>
        <div className="header-actions">
          <form className="inline-search" onSubmit={event => { event.preventDefault(); setPage(1); setSearch(searchDraft.trim()); }}><input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Search" /><button className="ghost-button">Search</button></form>
          {mayManage && <button className="primary-button" type="button" onClick={openCreate}>New assignment</button>}
        </div>
      </div>
      {notice && <div className="success-banner" role="status">{notice}</div>}
      {error && <div className="error-banner" role="alert">{error}</div>}
      {showForm && mayManage && <article className="panel management-form-panel">
        <div className="panel-heading"><div><h2>{editingId ? 'Edit' : 'Create'} {config.noun}</h2><p>Backend validation and permission checks remain authoritative.</p></div><button className="ghost-button" type="button" onClick={() => { setShowForm(false); resetForm(); }}>Close</button></div>
        <form onSubmit={submit} className="form-grid">
          {kind === 'rdp' && <>
            <EmployeeSelect employees={employees} value={rdp.employeeId} onChange={employeeId => setRdp(value => ({ ...value, employeeId }))} />
            <label><span>Label</span><input required maxLength={150} value={rdp.name} onChange={event => setRdp(value => ({ ...value, name: event.target.value }))} placeholder="Accounting RDP" /></label>
            <label><span>Host / IP</span><input required maxLength={255} value={rdp.host} onChange={event => setRdp(value => ({ ...value, host: event.target.value }))} placeholder="10.0.0.20 or rdp.example.internal" /></label>
            <label><span>Port</span><input type="number" min={1} max={65535} required value={rdp.port} onChange={event => setRdp(value => ({ ...value, port: event.target.value }))} /></label>
            <label><span>Username reference</span><input maxLength={200} value={rdp.usernameReference} onChange={event => setRdp(value => ({ ...value, usernameReference: event.target.value }))} placeholder="DOMAIN\\username" /></label>
            <label><span>Credential reference</span><input maxLength={500} value={rdp.credentialReference} onChange={event => setRdp(value => ({ ...value, credentialReference: event.target.value }))} placeholder="Vault item/reference only" /><small>Never enter a password here.</small></label>
            <label><span>Valid from</span><input type="date" value={rdp.validFrom} onChange={event => setRdp(value => ({ ...value, validFrom: event.target.value }))} /></label>
            <label><span>Expires on</span><input type="date" value={rdp.expiresOn} onChange={event => setRdp(value => ({ ...value, expiresOn: event.target.value }))} /></label>
            <label className="check-field"><input type="checkbox" checked={rdp.isActive} onChange={event => setRdp(value => ({ ...value, isActive: event.target.checked }))} /><span>Active</span></label>
            <label className="wide-field"><span>Notes</span><textarea maxLength={2000} value={rdp.notes} onChange={event => setRdp(value => ({ ...value, notes: event.target.value }))} /></label>
          </>}
          {kind === 'ip' && <>
            <EmployeeSelect employees={employees} value={ip.employeeId} onChange={employeeId => setIp(value => ({ ...value, employeeId }))} />
            <label><span>IP address</span><input required maxLength={64} value={ip.ipAddress} onChange={event => setIp(value => ({ ...value, ipAddress: event.target.value }))} placeholder="192.168.10.25" /></label>
            <label><span>Device name</span><input required maxLength={150} value={ip.deviceName} onChange={event => setIp(value => ({ ...value, deviceName: event.target.value }))} placeholder="DEV-PC-07" /></label>
            <label><span>MAC address</span><input maxLength={32} value={ip.macAddress} onChange={event => setIp(value => ({ ...value, macAddress: event.target.value }))} placeholder="AA:BB:CC:DD:EE:FF" /></label>
            <label><span>Status</span><select value={ip.status} onChange={event => setIp(value => ({ ...value, status: event.target.value as IpDraft['status'] }))}><option>Active</option><option>Reserved</option><option>Released</option></select></label>
            <label><span>Assigned on</span><input type="date" value={ip.assignedOn} onChange={event => setIp(value => ({ ...value, assignedOn: event.target.value }))} /></label>
            <label><span>Released on</span><input type="date" value={ip.releasedOn} onChange={event => setIp(value => ({ ...value, releasedOn: event.target.value }))} disabled={ip.status !== 'Released'} /></label>
            <label className="wide-field"><span>Notes</span><textarea maxLength={2000} value={ip.notes} onChange={event => setIp(value => ({ ...value, notes: event.target.value }))} /></label>
          </>}
          {kind === 'websites' && <>
            <EmployeeSelect employees={employees} value={website.employeeId} onChange={employeeId => setWebsite(value => ({ ...value, employeeId }))} />
            <label><span>Website name</span><input required maxLength={200} value={website.name} onChange={event => setWebsite(value => ({ ...value, name: event.target.value }))} placeholder="Client CRM" /></label>
            <label className="wide-field"><span>Website URL</span><input type="url" required maxLength={2048} value={website.url} onChange={event => setWebsite(value => ({ ...value, url: event.target.value }))} placeholder="https://crm.example.com" /></label>
            <label><span>Username reference</span><input maxLength={200} value={website.usernameReference} onChange={event => setWebsite(value => ({ ...value, usernameReference: event.target.value }))} /><small>No password or session cookie.</small></label>
            <label><span>Access level</span><select value={website.accessLevel} onChange={event => setWebsite(value => ({ ...value, accessLevel: event.target.value as WebsiteDraft['accessLevel'] }))}><option>View</option><option>Work</option><option>Admin</option></select></label>
            <label><span>Starts on</span><input type="date" value={website.startsOn} onChange={event => setWebsite(value => ({ ...value, startsOn: event.target.value }))} /></label>
            <label><span>Expires on</span><input type="date" value={website.expiresOn} onChange={event => setWebsite(value => ({ ...value, expiresOn: event.target.value }))} /></label>
            <label className="check-field"><input type="checkbox" checked={website.isActive} onChange={event => setWebsite(value => ({ ...value, isActive: event.target.checked }))} /><span>Active</span></label>
            <label className="wide-field"><span>Notes / instructions</span><textarea maxLength={2000} value={website.notes} onChange={event => setWebsite(value => ({ ...value, notes: event.target.value }))} /></label>
          </>}
          <div className="wide-field form-actions"><button className="primary-button" type="submit" disabled={busy}>{busy ? 'Saving…' : editingId ? 'Save changes' : 'Create assignment'}</button><button className="ghost-button" type="button" onClick={() => { setShowForm(false); resetForm(); }}>Cancel</button></div>
        </form>
      </article>}
      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>{config.title}</h2><p>Showing {items.length} of {totalCount}</p></div><button className="ghost-button" type="button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>
        {loading ? <div className="loading-block">Loading current server data…</div> : <div className="table-wrap">
          {kind === 'rdp' && <table><thead><tr><th>Employee</th><th>Endpoint</th><th>User / credential ref</th><th>Validity</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead><tbody>
            {(items as RdpAssignment[]).map(item => <tr key={item.id}><td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td><td><strong>{item.name}</strong><small>{item.host}:{item.port}</small></td><td>{item.usernameReference || '—'}<small>{item.credentialReference || 'No credential reference'}</small></td><td>{item.validFrom || '—'} → {item.expiresOn || 'No expiry'}</td><td><Status value={item.isActive ? 'Active' : 'Inactive'} /></td>{mayManage && <td className="action-cell"><button className="text-button" onClick={() => edit(item)}>Edit</button>{item.isActive && <button className="text-button danger" disabled={busy} onClick={() => void quickDeactivate(item)}>Deactivate</button>}</td>}</tr>)}
            {!items.length && <tr><td colSpan={mayManage ? 6 : 5} className="empty-cell">No RDP assignments found.</td></tr>}
          </tbody></table>}
          {kind === 'ip' && <table><thead><tr><th>Employee</th><th>IP</th><th>Device</th><th>MAC</th><th>Status</th><th>Dates</th>{mayManage && <th>Actions</th>}</tr></thead><tbody>
            {(items as IpAssignment[]).map(item => <tr key={item.id}><td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td><td><strong>{item.ipAddress}</strong></td><td>{item.deviceName}</td><td>{item.macAddress || '—'}</td><td><Status value={item.status} /></td><td>{item.assignedOn || '—'}<small>{item.releasedOn ? `Released ${item.releasedOn}` : ''}</small></td>{mayManage && <td className="action-cell"><button className="text-button" onClick={() => edit(item)}>Edit</button>{item.status !== 'Released' && <button className="text-button danger" disabled={busy} onClick={() => void quickDeactivate(item)}>Release</button>}</td>}</tr>)}
            {!items.length && <tr><td colSpan={mayManage ? 7 : 6} className="empty-cell">No IP assignments found.</td></tr>}
          </tbody></table>}
          {kind === 'websites' && <table><thead><tr><th>Employee</th><th>Website</th><th>Access</th><th>Username ref</th><th>Validity</th><th>Status</th>{mayManage && <th>Actions</th>}</tr></thead><tbody>
            {(items as WebsiteAssignment[]).map(item => <tr key={item.id}><td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td><td><strong>{item.name}</strong><small><a href={item.url} target="_blank" rel="noreferrer">{item.url}</a></small></td><td><Status value={item.accessLevel} /></td><td>{item.usernameReference || '—'}</td><td>{item.startsOn || '—'} → {item.expiresOn || 'No expiry'}</td><td><Status value={item.isActive ? 'Active' : 'Inactive'} /></td>{mayManage && <td className="action-cell"><button className="text-button" onClick={() => edit(item)}>Edit</button>{item.isActive && <button className="text-button danger" disabled={busy} onClick={() => void quickDeactivate(item)}>Deactivate</button>}</td>}</tr>)}
            {!items.length && <tr><td colSpan={mayManage ? 7 : 6} className="empty-cell">No website assignments found.</td></tr>}
          </tbody></table>}
        </div>}
        {!loading && totalPages > 1 && <div className="form-actions">
          <button className="ghost-button" type="button" disabled={page <= 1 || busy} onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="ghost-button" type="button" disabled={page >= totalPages || busy} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next</button>
        </div>}
      </article>
    </>
  );
}