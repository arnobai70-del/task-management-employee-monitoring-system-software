import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import OperationsIncidentsPage from './OperationsIncidents';
import OperationsIncidentRealtimeNotice from './OperationsIncidentRealtimeNotice';
import './management.css';

interface ServerHealth {
  apiHealthy: boolean;
  databaseHealthy: boolean;
  databaseLatencyMilliseconds: number;
  apiStartedAtUtc: string;
  apiVersion: string;
}

interface BackupHealth {
  isKnown: boolean;
  lastSuccessfulAtUtc: string | null;
  ageMinutes: number | null;
  isStale: boolean;
  label: string | null;
  archiveFile: string | null;
  sizeBytes: number | null;
}

interface ReleaseHealth {
  isKnown: boolean;
  channel: string;
  latestVersion: string | null;
  publishedAtUtc: string | null;
}

interface AgentSummary {
  activeEmployees: number;
  online: number;
  offline: number;
  healthy: number;
  needsAttention: number;
  detailedReports: number;
  outdated: number;
  serviceStopped: number;
  rolledBack: number;
}

interface AgentHealth {
  employeeId: string;
  employeeCode: string;
  fullName: string;
  departmentId: string | null;
  departmentName: string | null;
  isOnline: boolean;
  lastSeenAtUtc: string | null;
  detailedHealthAtUtc: string | null;
  machineName: string | null;
  desktopVersion: string | null;
  serviceVersion: string | null;
  updaterVersion: string | null;
  serviceRunning: boolean | null;
  installedVersion: string | null;
  updateChannel: string | null;
  lastSuccessfulUpdateAtUtc: string | null;
  rolledBackAtUtc: string | null;
  isOutdated: boolean;
  health: 'Healthy' | 'Warning' | 'Critical' | 'Offline' | string;
  issues: string[];
}

interface OperationsOverview {
  generatedAtUtc: string;
  server: ServerHealth;
  backup: BackupHealth;
  release: ReleaseHealth;
  agentsSummary: AgentSummary;
  agents: AgentHealth[];
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function formatAge(minutes: number | null): string {
  if (minutes === null) return 'Unknown';
  if (minutes < 1) return '<1 min';
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const remaining = minutes % 60;
  if (hours < 24) return `${hours}h ${remaining}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

function formatBytes(value: number | null): string {
  if (value === null) return '—';
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(1)} MB`;
  return `${(value / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

function healthClass(value: string): string {
  const normalized = value.toLowerCase();
  if (normalized === 'healthy') return 'status-active';
  if (normalized === 'critical' || normalized === 'offline') return 'status-urgent';
  return 'status-warning';
}

export default function OperationsDashboardPage() {
  const [overview, setOverview] = useState<OperationsOverview | null>(null);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [health, setHealth] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);

  const path = useMemo(() => {
    const params = new URLSearchParams({ limit: '300' });
    if (search) params.set('search', search);
    if (health) params.set('health', health);
    return `/api/operations/overview?${params.toString()}`;
  }, [search, health]);

  useEffect(() => {
    let cancelled = false;
    const load = async (initial: boolean) => {
      if (initial) setLoading(true);
      try {
        const data = await apiFetch<OperationsOverview>(path);
        if (!cancelled) {
          setOverview(data);
          setError('');
        }
      } catch (caught) {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load operations health.');
      } finally {
        if (!cancelled && initial) setLoading(false);
      }
    };

    void load(true);
    const timer = window.setInterval(() => void load(false), 15_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [path, version]);

  function applySearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSearch(searchDraft.trim());
  }

  const serverState = overview?.server.databaseHealthy ? 'Healthy' : 'Database issue';
  const backupState = !overview?.backup.isKnown ? 'Unknown' : overview.backup.isStale ? 'Stale' : 'Healthy';

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Production operations</p>
          <h1>Operations & Agent Health</h1>
          <p className="muted">Server readiness, database/backup health, stable employee release state, and operational health for active employee PCs.</p>
        </div>
        <div className="header-actions">
          <button className="ghost-button" type="button" onClick={() => setVersion(value => value + 1)}>Refresh now</button>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}
      <div className="panel" style={{ marginBottom: 16 }}>
        <strong>Operational scope</strong>
        <p className="muted" style={{ marginBottom: 0 }}>
          Online/last-seen state is database-backed. Desktop/service/updater detail is live operational telemetry and repopulates after an API restart on the next signed-in desktop health report. No screen content, passwords, cookies, form data, balances, earnings or browsing history are collected here.
        </p>
      </div>

      {loading && !overview ? <div className="panel loading-block">Loading production operations health…</div> : overview && (
        <>
          <section className="metric-grid compact-metrics">
            <article className="metric-card"><span>API / DB</span><strong>{serverState}</strong><small>DB probe {overview.server.databaseLatencyMilliseconds} ms · API {overview.server.apiVersion}</small></article>
            <article className="metric-card"><span>PostgreSQL backup</span><strong>{backupState}</strong><small>{overview.backup.isKnown ? `${formatAge(overview.backup.ageMinutes)} old · ${formatBytes(overview.backup.sizeBytes)}` : 'No successful backup status reported to this API instance'}</small></article>
            <article className="metric-card"><span>Stable release</span><strong>{overview.release.latestVersion || 'Unknown'}</strong><small>{overview.release.isKnown ? `${overview.release.channel} · ${formatDateTime(overview.release.publishedAtUtc)}` : 'No stable manifest mounted/readable'}</small></article>
            <article className="metric-card"><span>Agents online</span><strong>{overview.agentsSummary.online}/{overview.agentsSummary.activeEmployees}</strong><small>{overview.agentsSummary.offline} offline/stale</small></article>
            <article className="metric-card"><span>Needs attention</span><strong>{overview.agentsSummary.needsAttention}</strong><small>{overview.agentsSummary.healthy} healthy</small></article>
            <article className="metric-card"><span>Outdated runtime</span><strong>{overview.agentsSummary.outdated}</strong><small>Compared with stable release</small></article>
            <article className="metric-card"><span>Service stopped</span><strong>{overview.agentsSummary.serviceStopped}</strong><small>Fresh detailed reports only</small></article>
            <article className="metric-card"><span>Rollback state</span><strong>{overview.agentsSummary.rolledBack}</strong><small>{overview.agentsSummary.detailedReports} fresh detailed report(s)</small></article>
          </section>

          <section className="dashboard-grid">
            <article className="panel">
              <div className="panel-heading"><div><h2>Server readiness</h2><p>Current API and database checks</p></div></div>
              <div className="progress-list">
                <div className="progress-row"><span>API process</span><strong>{overview.server.apiHealthy ? 'Healthy' : 'Unhealthy'}</strong></div>
                <div className="progress-row"><span>PostgreSQL connection</span><strong>{overview.server.databaseHealthy ? 'Healthy' : 'Unhealthy'}</strong></div>
                <div className="progress-row"><span>DB probe latency</span><strong>{overview.server.databaseLatencyMilliseconds} ms</strong></div>
                <div className="progress-row"><span>API started</span><strong>{formatDateTime(overview.server.apiStartedAtUtc)}</strong></div>
              </div>
            </article>
            <article className="panel">
              <div className="panel-heading"><div><h2>Recovery posture</h2><p>Latest known backup and employee release</p></div></div>
              <div className="progress-list">
                <div className="progress-row"><span>Last successful backup</span><strong>{formatDateTime(overview.backup.lastSuccessfulAtUtc)}</strong></div>
                <div className="progress-row"><span>Backup age</span><strong>{formatAge(overview.backup.ageMinutes)}</strong></div>
                <div className="progress-row"><span>Backup label</span><strong>{overview.backup.label || '—'}</strong></div>
                <div className="progress-row"><span>Stable employee release</span><strong>{overview.release.latestVersion || '—'}</strong></div>
              </div>
            </article>
          </section>

          <article className="panel table-panel">
            <div className="panel-heading">
              <div><h2>Employee agent health</h2><p>Auto-refreshes every 15 seconds · generated {formatDateTime(overview.generatedAtUtc)}</p></div>
              <div className="header-actions">
                <form className="inline-search" onSubmit={applySearch}>
                  <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Employee, department, machine" aria-label="Search agent health" />
                  <button className="ghost-button" type="submit">Search</button>
                </form>
                <select value={health} onChange={event => setHealth(event.target.value)} aria-label="Filter health">
                  <option value="">All health</option>
                  <option value="Healthy">Healthy</option>
                  <option value="Warning">Warning</option>
                  <option value="Critical">Critical</option>
                  <option value="Offline">Offline</option>
                </select>
              </div>
            </div>
            <div className="table-wrap"><table>
              <thead><tr><th>Employee</th><th>Health</th><th>Last seen</th><th>Machine</th><th>Versions</th><th>Service</th><th>Update state</th><th>Issues</th></tr></thead>
              <tbody>
                {overview.agents.map(agent => (
                  <tr key={agent.employeeId}>
                    <td><strong>{agent.fullName}</strong><small>{agent.employeeCode} · {agent.departmentName || 'No department'}</small></td>
                    <td><span className={`status-badge ${healthClass(agent.health)}`}>{agent.health}</span></td>
                    <td>{formatDateTime(agent.lastSeenAtUtc)}<small>Detail: {formatDateTime(agent.detailedHealthAtUtc)}</small></td>
                    <td>{agent.machineName || '—'}</td>
                    <td>
                      <strong>Desktop {agent.desktopVersion || '—'}</strong>
                      <small>Service {agent.serviceVersion || '—'} · Updater {agent.updaterVersion || '—'}</small>
                    </td>
                    <td>{agent.serviceRunning === null ? 'Unknown' : agent.serviceRunning ? 'Running' : 'Stopped'}</td>
                    <td>
                      <strong>{agent.installedVersion || '—'}{agent.isOutdated ? ' · Outdated' : ''}</strong>
                      <small>{agent.updateChannel || '—'} · last update {formatDateTime(agent.lastSuccessfulUpdateAtUtc)}{agent.rolledBackAtUtc ? ` · rollback ${formatDateTime(agent.rolledBackAtUtc)}` : ''}</small>
                    </td>
                    <td>{agent.issues.length ? agent.issues.map(issue => <small key={issue} style={{ display: 'block' }}>{issue}</small>) : '—'}</td>
                  </tr>
                ))}
                {!overview.agents.length && <tr><td className="empty-cell" colSpan={8}>No active employee agents match this filter.</td></tr>}
              </tbody>
            </table></div>
          </article>
        </>
      )}

      <div style={{ marginTop: 32 }}>
        <OperationsIncidentsPage />
      </div>
      <OperationsIncidentRealtimeNotice />
    </>
  );
}
