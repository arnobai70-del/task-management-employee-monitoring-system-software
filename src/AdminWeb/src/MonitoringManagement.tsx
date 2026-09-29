import { useEffect, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

interface MonitoringPolicy {
  id: string | null;
  isEnabled: boolean;
  sampleIntervalSeconds: number;
  retentionDays: number;
  disclosureText: string;
  updatedAtUtc: string | null;
}

interface ApplicationRule {
  id: string;
  processName: string;
  displayName: string;
  captureWindowTitle: boolean;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface DomainRule {
  id: string;
  domain: string;
  displayName: string;
  includeSubdomains: boolean;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface MonitoringActivity {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  departmentName: string | null;
  kind: 'Application' | 'BusinessDomain';
  processName: string | null;
  applicationName: string | null;
  windowTitle: string | null;
  domain: string | null;
  startedAtUtc: string;
  lastObservedAtUtc: string;
  sampleCount: number;
  approximateDurationSeconds: number;
}

function dateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function duration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return remainder ? `${minutes}m ${remainder}s` : `${minutes}m`;
}

function Status({ active }: { active: boolean }) {
  return <span className={`status-badge status-${active ? 'active' : 'inactive'}`}>{active ? 'Active' : 'Inactive'}</span>;
}

export default function MonitoringManagementPage() {
  const { can } = useAuth();
  const mayManage = can('monitoring.manage');
  const [policy, setPolicy] = useState<MonitoringPolicy | null>(null);
  const [applications, setApplications] = useState<ApplicationRule[]>([]);
  const [domains, setDomains] = useState<DomainRule[]>([]);
  const [activity, setActivity] = useState<PagedResponse<MonitoringActivity> | null>(null);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [version, setVersion] = useState(0);

  const [policyEnabled, setPolicyEnabled] = useState(true);
  const [sampleInterval, setSampleInterval] = useState(30);
  const [retentionDays, setRetentionDays] = useState(30);
  const [disclosure, setDisclosure] = useState('');

  const [appEditing, setAppEditing] = useState<ApplicationRule | null>(null);
  const [appProcess, setAppProcess] = useState('');
  const [appName, setAppName] = useState('');
  const [appCaptureTitle, setAppCaptureTitle] = useState(false);
  const [appActive, setAppActive] = useState(true);

  const [domainEditing, setDomainEditing] = useState<DomainRule | null>(null);
  const [domainHost, setDomainHost] = useState('');
  const [domainName, setDomainName] = useState('');
  const [domainSubdomains, setDomainSubdomains] = useState(false);
  const [domainActive, setDomainActive] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    const query = new URLSearchParams({ page: '1', pageSize: '100' });
    if (search) query.set('search', search);
    if (kind) query.set('kind', kind);
    Promise.all([
      apiFetch<MonitoringPolicy>('/api/monitoring/policy'),
      apiFetch<ApplicationRule[]>('/api/monitoring/applications?includeInactive=true'),
      apiFetch<DomainRule[]>('/api/monitoring/domains?includeInactive=true'),
      apiFetch<PagedResponse<MonitoringActivity>>(`/api/monitoring/activity?${query.toString()}`)
    ]).then(([policyResult, appResult, domainResult, activityResult]) => {
      if (cancelled) return;
      setPolicy(policyResult);
      setPolicyEnabled(policyResult.isEnabled);
      setSampleInterval(policyResult.sampleIntervalSeconds);
      setRetentionDays(policyResult.retentionDays);
      setDisclosure(policyResult.disclosureText);
      setApplications(appResult);
      setDomains(domainResult);
      setActivity(activityResult);
    }).catch(caught => {
      if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load monitoring data.');
    }).finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => { cancelled = true; };
  }, [version, search, kind]);

  async function savePolicy(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch('/api/monitoring/policy', {
        method: 'PUT',
        body: JSON.stringify({ isEnabled: policyEnabled, sampleIntervalSeconds: sampleInterval, retentionDays, disclosureText: disclosure })
      });
      setNotice('Monitoring policy updated. Retention cleanup runs automatically.');
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to update monitoring policy.');
    } finally { setBusy(false); }
  }

  function editApplication(item?: ApplicationRule) {
    setAppEditing(item || null);
    setAppProcess(item?.processName || '');
    setAppName(item?.displayName || '');
    setAppCaptureTitle(item?.captureWindowTitle || false);
    setAppActive(item?.isActive ?? true);
    setError(''); setNotice('');
  }

  async function saveApplication(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/monitoring/applications${appEditing ? `/${appEditing.id}` : ''}`, {
        method: appEditing ? 'PUT' : 'POST',
        body: JSON.stringify({ processName: appProcess, displayName: appName, captureWindowTitle: appCaptureTitle, isActive: appActive })
      });
      setNotice(`Application rule ${appEditing ? 'updated' : 'created'}.`);
      editApplication();
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save application rule.');
    } finally { setBusy(false); }
  }

  function editDomain(item?: DomainRule) {
    setDomainEditing(item || null);
    setDomainHost(item?.domain || '');
    setDomainName(item?.displayName || '');
    setDomainSubdomains(item?.includeSubdomains || false);
    setDomainActive(item?.isActive ?? true);
    setError(''); setNotice('');
  }

  async function saveDomain(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManage) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/monitoring/domains${domainEditing ? `/${domainEditing.id}` : ''}`, {
        method: domainEditing ? 'PUT' : 'POST',
        body: JSON.stringify({ domain: domainHost, displayName: domainName, includeSubdomains: domainSubdomains, isActive: domainActive })
      });
      setNotice(`Business-domain rule ${domainEditing ? 'updated' : 'created'}.`);
      editDomain();
      setVersion(value => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save business-domain rule.');
    } finally { setBusy(false); }
  }

  return <>
    <div className="page-header"><div><p className="eyebrow">Transparency & privacy</p><h1>Monitoring</h1><p className="muted">Approved work-application and business-hostname telemetry only. No keystrokes, passwords, screenshots, page content, browser history, microphone/camera data or unrelated files.</p></div><button className="ghost-button" onClick={() => setVersion(value => value + 1)}>Refresh</button></div>
    {notice && <div className="success-banner">{notice}</div>}{error && <div className="error-banner">{error}</div>}

    <article className="panel management-form-panel">
      <div className="panel-heading"><div><h2>Monitoring policy</h2><p>{policy?.updatedAtUtc ? `Last updated ${dateTime(policy.updatedAtUtc)}` : 'Default policy is active until saved.'}</p></div><Status active={policy?.isEnabled ?? true} /></div>
      <form className="form-grid" onSubmit={savePolicy}>
        <label className="check-field"><input type="checkbox" checked={policyEnabled} disabled={!mayManage} onChange={event => setPolicyEnabled(event.target.checked)} /><span>Approved activity monitoring enabled</span></label>
        <label><span>Sample interval (seconds)</span><input type="number" min={15} max={300} value={sampleInterval} disabled={!mayManage} onChange={event => setSampleInterval(Number(event.target.value))} /></label>
        <label><span>Retention (days)</span><input type="number" min={1} max={365} value={retentionDays} disabled={!mayManage} onChange={event => setRetentionDays(Number(event.target.value))} /></label>
        <label className="wide-field"><span>Employee disclosure</span><textarea rows={5} minLength={40} maxLength={2000} required value={disclosure} disabled={!mayManage} onChange={event => setDisclosure(event.target.value)} /></label>
        {mayManage && <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>Save policy</button></div>}
      </form>
    </article>

    <section className="dashboard-grid">
      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>Approved applications</h2><p>Window titles are read only when explicitly enabled per rule.</p></div>{mayManage && <button className="ghost-button" onClick={() => editApplication()}>New rule</button>}</div>
        {mayManage && <form className="form-grid compact-form" onSubmit={saveApplication}>
          <label><span>Process name</span><input required maxLength={120} placeholder="chrome or code" value={appProcess} onChange={event => setAppProcess(event.target.value)} /></label>
          <label><span>Display name</span><input required maxLength={160} value={appName} onChange={event => setAppName(event.target.value)} /></label>
          <label className="check-field"><input type="checkbox" checked={appCaptureTitle} onChange={event => setAppCaptureTitle(event.target.checked)} /><span>Capture window title</span></label>
          <label className="check-field"><input type="checkbox" checked={appActive} onChange={event => setAppActive(event.target.checked)} /><span>Active</span></label>
          <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{appEditing ? 'Update rule' : 'Create rule'}</button>{appEditing && <button type="button" className="ghost-button" onClick={() => editApplication()}>Cancel</button>}</div>
        </form>}
        <div className="table-wrap"><table><thead><tr><th>Application</th><th>Process</th><th>Title capture</th><th>Status</th>{mayManage && <th>Action</th>}</tr></thead><tbody>
          {applications.map(item => <tr key={item.id}><td><strong>{item.displayName}</strong></td><td>{item.processName}</td><td>{item.captureWindowTitle ? 'Enabled' : 'Off'}</td><td><Status active={item.isActive} /></td>{mayManage && <td><button className="text-button" onClick={() => editApplication(item)}>Edit</button></td>}</tr>)}
          {!applications.length && <tr><td colSpan={mayManage ? 5 : 4} className="empty-cell">No approved application rules.</td></tr>}
        </tbody></table></div>
      </article>

      <article className="panel table-panel">
        <div className="panel-heading"><div><h2>Approved business domains</h2><p>Hostnames only; URL paths/query strings are rejected by the API.</p></div>{mayManage && <button className="ghost-button" onClick={() => editDomain()}>New domain</button>}</div>
        {mayManage && <form className="form-grid compact-form" onSubmit={saveDomain}>
          <label><span>Hostname</span><input required maxLength={253} placeholder="jira.example.com" value={domainHost} onChange={event => setDomainHost(event.target.value)} /></label>
          <label><span>Display name</span><input required maxLength={160} value={domainName} onChange={event => setDomainName(event.target.value)} /></label>
          <label className="check-field"><input type="checkbox" checked={domainSubdomains} onChange={event => setDomainSubdomains(event.target.checked)} /><span>Include subdomains</span></label>
          <label className="check-field"><input type="checkbox" checked={domainActive} onChange={event => setDomainActive(event.target.checked)} /><span>Active</span></label>
          <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{domainEditing ? 'Update domain' : 'Create domain'}</button>{domainEditing && <button type="button" className="ghost-button" onClick={() => editDomain()}>Cancel</button>}</div>
        </form>}
        <div className="table-wrap"><table><thead><tr><th>Name</th><th>Hostname</th><th>Subdomains</th><th>Status</th>{mayManage && <th>Action</th>}</tr></thead><tbody>
          {domains.map(item => <tr key={item.id}><td><strong>{item.displayName}</strong></td><td>{item.domain}</td><td>{item.includeSubdomains ? 'Included' : 'Exact only'}</td><td><Status active={item.isActive} /></td>{mayManage && <td><button className="text-button" onClick={() => editDomain(item)}>Edit</button></td>}</tr>)}
          {!domains.length && <tr><td colSpan={mayManage ? 5 : 4} className="empty-cell">No additional business-domain rules. Company website assignments are still self-approved for the assigned employee.</td></tr>}
        </tbody></table></div>
      </article>
    </section>

    <article className="panel table-panel">
      <div className="panel-heading"><div><h2>Recent approved activity</h2><p>Segments are coalesced server-side and bounded by retention policy.</p></div><form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); }}><select value={kind} onChange={event => setKind(event.target.value)}><option value="">All types</option><option value="Application">Applications</option><option value="BusinessDomain">Business domains</option></select><input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Employee, app, title, domain" /><button className="ghost-button">Filter</button></form></div>
      {loading && !activity ? <div className="loading-block">Loading monitoring data…</div> : <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Type</th><th>Approved activity</th><th>Window title</th><th>Started</th><th>Last seen</th><th>Approx.</th></tr></thead><tbody>
        {(activity?.items || []).map(item => <tr key={item.id}><td><strong>{item.employeeName}</strong><small>{item.employeeCode}{item.departmentName ? ` · ${item.departmentName}` : ''}</small></td><td>{item.kind === 'Application' ? 'Application' : 'Business domain'}</td><td>{item.kind === 'Application' ? <><strong>{item.applicationName || item.processName}</strong><small>{item.processName}</small></> : <strong>{item.domain}</strong>}</td><td>{item.windowTitle || '—'}</td><td>{dateTime(item.startedAtUtc)}</td><td>{dateTime(item.lastObservedAtUtc)}</td><td>{duration(item.approximateDurationSeconds)}<small>{item.sampleCount} sample(s)</small></td></tr>)}
        {!activity?.items.length && <tr><td colSpan={7} className="empty-cell">No approved monitoring activity found for this filter.</td></tr>}
      </tbody></table></div>}
    </article>
  </>;
}
