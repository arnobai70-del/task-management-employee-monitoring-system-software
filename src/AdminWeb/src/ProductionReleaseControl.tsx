import { useEffect, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import './management.css';

type ReleaseStatus = 'Candidate' | 'Approved' | 'PromotionAuthorized' | 'Deployed' | 'RollbackRequested' | 'RolledBack' | 'Superseded' | 'Withdrawn';

interface ReleaseGate {
  code: string;
  label: string;
  passed: boolean;
  detail: string;
}

interface ReleaseReadiness {
  ready: boolean;
  observedStableVersion: string | null;
  observedStablePublishedAtUtc: string | null;
  activeAgentRollouts: number;
  openCriticalOperationsIncidents: number;
  openCriticalSecurityAlerts: number;
  gates: ReleaseGate[];
}

interface ReleaseRecord {
  id: string;
  revision: number;
  version: string;
  channel: string;
  status: ReleaseStatus;
  commitSha: string;
  manifestSha256: string;
  packageFile: string;
  packageSha256: string;
  packageSizeBytes: number;
  publisherCertificateSha256: string;
  minimumUpdaterVersion: string | null;
  sourceWorkflowReference: string | null;
  signedArtifactVerified: boolean;
  publisherFingerprintVerified: boolean;
  createdAtUtc: string;
  createdByEmail: string;
  approvedAtUtc: string | null;
  approvedByEmail: string | null;
  promotionAuthorizedAtUtc: string | null;
  promotionAuthorizedByEmail: string | null;
  deployedAtUtc: string | null;
  deployedByEmail: string | null;
  rollbackTargetVersion: string | null;
  rollbackRequestedAtUtc: string | null;
  rolledBackAtUtc: string | null;
  lastNote: string | null;
}

interface ReleaseDashboard {
  generatedAtUtc: string;
  observedStableVersion: string | null;
  observedStablePublishedAtUtc: string | null;
  summary: {
    candidates: number;
    approved: number;
    promotionAuthorized: number;
    deployed: number;
    rollbackRequested: number;
    historical: number;
  };
  readiness: ReleaseReadiness | null;
  enrolledAgentDevices: number;
  boundAgentDevices: number;
  releases: ReleaseRecord[];
}

const emptyDraft = {
  version: '',
  commitSha: '',
  manifestSha256: '',
  packageFile: '',
  packageSha256: '',
  packageSizeBytes: '',
  publisherCertificateSha256: '',
  minimumUpdaterVersion: '',
  sourceWorkflowReference: '',
  signedArtifactVerified: false,
  publisherFingerprintVerified: false,
  note: ''
};

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function shortHash(value: string): string {
  return value.length <= 14 ? value : `${value.slice(0, 8)}…${value.slice(-6)}`;
}

function statusClass(status: string): string {
  if (status === 'Deployed' || status === 'Approved') return 'status-active';
  if (status === 'RollbackRequested' || status === 'RolledBack') return 'status-urgent';
  if (status === 'Withdrawn' || status === 'Superseded') return 'status-inactive';
  return 'status-warning';
}

export default function ProductionReleaseControl() {
  const { can } = useAuth();
  const canManage = can('production-releases.manage');
  const [dashboard, setDashboard] = useState<ReleaseDashboard | null>(null);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState('');
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [draft, setDraft] = useState(emptyDraft);

  async function load(initial = false) {
    if (initial) setLoading(true);
    try {
      setDashboard(await apiFetch<ReleaseDashboard>('/api/production-releases/dashboard'));
      setError('');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load production release control.');
    } finally {
      if (initial) setLoading(false);
    }
  }

  useEffect(() => {
    let cancelled = false;
    const run = async (initial: boolean) => {
      if (cancelled) return;
      await load(initial);
    };
    void run(true);
    const timer = window.setInterval(() => void run(false), 30_000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, []);

  async function register(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyId('register');
    setError('');
    setMessage('');
    try {
      await apiFetch('/api/production-releases', {
        method: 'POST',
        body: JSON.stringify({
          ...draft,
          packageSizeBytes: Number(draft.packageSizeBytes),
          minimumUpdaterVersion: draft.minimumUpdaterVersion.trim() || null,
          sourceWorkflowReference: draft.sourceWorkflowReference.trim() || null,
          note: draft.note.trim() || null
        })
      });
      setDraft(emptyDraft);
      setMessage('Release candidate registered. Metadata is now immutable for this version.');
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to register release candidate.');
    } finally {
      setBusyId('');
    }
  }

  async function action(release: ReleaseRecord, actionName: string, label: string) {
    const note = window.prompt(`${label} note (optional):`, '') ?? '';
    setBusyId(`${release.id}:${actionName}`);
    setError('');
    setMessage('');
    try {
      await apiFetch(`/api/production-releases/${release.id}/${actionName}`, {
        method: 'POST',
        body: JSON.stringify({ note: note.trim() || null })
      });
      setMessage(`${label} completed for ${release.version}.`);
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : `${label} failed.`);
    } finally {
      setBusyId('');
    }
  }

  const readiness = dashboard?.readiness;

  return (
    <section>
      <div className="page-header" style={{ marginTop: 32 }}>
        <div>
          <p className="eyebrow">Controlled production delivery</p>
          <h1>Production Release Control Center</h1>
          <p className="muted">Immutable candidate metadata, explicit approval, health gates, live artifact verification, release history and rollback decisions. The API never executes arbitrary shell commands and the update host remains read-only to the API.</p>
        </div>
        <button className="ghost-button" type="button" onClick={() => void load()}>Refresh</button>
      </div>

      {error && <div className="error-banner">{error}</div>}
      {message && <div className="success-banner">{message}</div>}

      {loading && !dashboard ? <div className="panel loading-block">Loading release control state…</div> : dashboard && (
        <>
          <section className="metric-grid compact-metrics">
            <article className="metric-card"><span>Observed stable</span><strong>{dashboard.observedStableVersion || 'Unknown'}</strong><small>{formatDateTime(dashboard.observedStablePublishedAtUtc)}</small></article>
            <article className="metric-card"><span>Candidates</span><strong>{dashboard.summary.candidates}</strong><small>{dashboard.summary.approved} approved</small></article>
            <article className="metric-card"><span>Promotion authorized</span><strong>{dashboard.summary.promotionAuthorized}</strong><small>Awaiting operator publish / verification</small></article>
            <article className="metric-card"><span>Deployed</span><strong>{dashboard.summary.deployed}</strong><small>{dashboard.summary.rollbackRequested} rollback request(s)</small></article>
            <article className="metric-card"><span>Managed devices</span><strong>{dashboard.boundAgentDevices}/{dashboard.enrolledAgentDevices}</strong><small>Bound / enrolled</small></article>
          </section>

          {readiness && (
            <article className="panel table-panel" style={{ marginBottom: 24 }}>
              <div className="panel-heading"><div><h2>Promotion readiness</h2><p>{readiness.ready ? 'All automatic gates are clear.' : 'Promotion is blocked until all gates pass.'}</p></div><span className={`status-badge ${readiness.ready ? 'status-active' : 'status-urgent'}`}>{readiness.ready ? 'Ready' : 'Blocked'}</span></div>
              <div className="table-wrap"><table><thead><tr><th>Gate</th><th>Status</th><th>Detail</th></tr></thead><tbody>
                {readiness.gates.map(gate => <tr key={gate.code}><td><strong>{gate.label}</strong></td><td><span className={`status-badge ${gate.passed ? 'status-active' : 'status-urgent'}`}>{gate.passed ? 'Pass' : 'Fail'}</span></td><td>{gate.detail}</td></tr>)}
              </tbody></table></div>
            </article>
          )}

          {canManage && (
            <article className="panel" style={{ marginBottom: 24 }}>
              <div className="panel-heading"><div><h2>Register signed release candidate</h2><p>Copy values from the signed release workflow/bundle after independent verification. A version cannot be reused.</p></div></div>
              <form className="stack-lg" onSubmit={register}>
                <div className="form-grid">
                  <label><span>Version</span><input value={draft.version} onChange={e => setDraft({ ...draft, version: e.target.value })} placeholder="1.2.3" required /></label>
                  <label><span>Full commit SHA</span><input value={draft.commitSha} onChange={e => setDraft({ ...draft, commitSha: e.target.value })} minLength={40} maxLength={40} required /></label>
                  <label><span>release.json SHA-256</span><input value={draft.manifestSha256} onChange={e => setDraft({ ...draft, manifestSha256: e.target.value })} minLength={64} maxLength={64} required /></label>
                  <label><span>Runtime package file</span><input value={draft.packageFile} onChange={e => setDraft({ ...draft, packageFile: e.target.value })} placeholder="TaskMonitoring.EmployeeRuntime-1.2.3-win-x64.zip" required /></label>
                  <label><span>Runtime package SHA-256</span><input value={draft.packageSha256} onChange={e => setDraft({ ...draft, packageSha256: e.target.value })} minLength={64} maxLength={64} required /></label>
                  <label><span>Package size (bytes)</span><input type="number" min="1" value={draft.packageSizeBytes} onChange={e => setDraft({ ...draft, packageSizeBytes: e.target.value })} required /></label>
                  <label><span>Publisher certificate SHA-256</span><input value={draft.publisherCertificateSha256} onChange={e => setDraft({ ...draft, publisherCertificateSha256: e.target.value })} minLength={64} maxLength={64} required /></label>
                  <label><span>Minimum updater version</span><input value={draft.minimumUpdaterVersion} onChange={e => setDraft({ ...draft, minimumUpdaterVersion: e.target.value })} placeholder="1.0.0" /></label>
                  <label><span>Signed workflow reference</span><input value={draft.sourceWorkflowReference} onChange={e => setDraft({ ...draft, sourceWorkflowReference: e.target.value })} placeholder="GitHub Actions run / artifact reference" /></label>
                </div>
                <label><span>Release note</span><textarea value={draft.note} onChange={e => setDraft({ ...draft, note: e.target.value })} maxLength={1000} /></label>
                <label className="checkbox-row"><input type="checkbox" checked={draft.signedArtifactVerified} onChange={e => setDraft({ ...draft, signedArtifactVerified: e.target.checked })} /><span>I verified the signed release artifact and its hashes.</span></label>
                <label className="checkbox-row"><input type="checkbox" checked={draft.publisherFingerprintVerified} onChange={e => setDraft({ ...draft, publisherFingerprintVerified: e.target.checked })} /><span>I independently verified the publisher certificate fingerprint.</span></label>
                <button className="primary-button" disabled={busyId === 'register'}>{busyId === 'register' ? 'Registering…' : 'Register candidate'}</button>
              </form>
            </article>
          )}

          <article className="panel table-panel">
            <div className="panel-heading"><div><h2>Release history</h2><p>Audit-backed state; newest candidates first.</p></div><span>{dashboard.releases.length} release record(s)</span></div>
            <div className="table-wrap"><table><thead><tr><th>Release</th><th>Status</th><th>Integrity</th><th>Workflow</th><th>Timeline</th><th>Actions</th></tr></thead><tbody>
              {dashboard.releases.map(release => {
                const isBusy = busyId.startsWith(`${release.id}:`);
                return <tr key={release.id}>
                  <td><strong>{release.version}</strong><small>{release.channel} · commit {shortHash(release.commitSha)}</small></td>
                  <td><span className={`status-badge ${statusClass(release.status)}`}>{release.status}</span>{release.rollbackTargetVersion && <small>Target: {release.rollbackTargetVersion}</small>}</td>
                  <td><strong>{shortHash(release.packageSha256)}</strong><small>Manifest {shortHash(release.manifestSha256)} · publisher {shortHash(release.publisherCertificateSha256)}</small></td>
                  <td>{release.sourceWorkflowReference || '—'}<small>{release.signedArtifactVerified && release.publisherFingerprintVerified ? 'Signed artifact + publisher attested' : 'Verification incomplete'}</small></td>
                  <td><strong>Created {formatDateTime(release.createdAtUtc)}</strong><small>Approved {formatDateTime(release.approvedAtUtc)} · deployed {formatDateTime(release.deployedAtUtc)}</small></td>
                  <td>
                    {!canManage ? 'View only' : <div className="header-actions">
                      {release.status === 'Candidate' && <><button className="ghost-button" disabled={isBusy} onClick={() => void action(release, 'approve', 'Approve release')}>Approve</button><button className="ghost-button" disabled={isBusy} onClick={() => void action(release, 'withdraw', 'Withdraw release')}>Withdraw</button></>}
                      {release.status === 'Approved' && <><button className="primary-button" disabled={isBusy || !readiness?.ready} onClick={() => void action(release, 'authorize-promotion', 'Authorize production promotion')}>Authorize promotion</button><button className="ghost-button" disabled={isBusy} onClick={() => void action(release, 'withdraw', 'Withdraw release')}>Withdraw</button></>}
                      {release.status === 'PromotionAuthorized' && <button className="primary-button" disabled={isBusy} onClick={() => void action(release, 'verify-deployment', 'Verify published deployment')}>Verify deployment</button>}
                      {release.status === 'Deployed' && <button className="ghost-button" disabled={isBusy} onClick={() => void action(release, 'request-rollback', 'Request rollback')}>Request rollback</button>}
                      {release.status === 'RollbackRequested' && <button className="primary-button" disabled={isBusy} onClick={() => void action(release, 'verify-rollback', 'Verify rollback')}>Verify rollback</button>}
                    </div>}
                  </td>
                </tr>;
              })}
              {!dashboard.releases.length && <tr><td colSpan={6} className="empty-cell">No production release candidates have been registered yet.</td></tr>}
            </tbody></table></div>
          </article>

          <div className="panel" style={{ marginTop: 16 }}>
            <strong>Operator trust boundary</strong>
            <p className="muted" style={{ marginBottom: 0 }}>After promotion authorization, publish the already verified signed bundle with the existing atomic production publish script. Then use “Verify deployment”. Verification reads the mounted release.json, package bytes/size/SHA-256 and archived publisher fingerprint. Rollback uses the same operator-owned atomic publish path; this web console records the decision and verifies the resulting stable state rather than executing remote shell commands.</p>
          </div>
        </>
      )}
    </section>
  );
}
