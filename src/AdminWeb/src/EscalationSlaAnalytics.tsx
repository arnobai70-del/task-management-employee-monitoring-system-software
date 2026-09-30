import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import './management.css';

type Grouping = 'Day' | 'Week';

interface SlaMetrics {
  due: number;
  resolved: number;
  slaMet: number;
  slaBreached: number;
  escalated: number;
  openOverdue: number;
  averageResolutionMinutes: number;
  averageOverdueMinutes: number;
  slaMetPercent: number;
}

interface ManagerSla {
  ownerUserId: string;
  ownerEmail: string;
  ownerName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  metrics: SlaMetrics;
  repeatedEscalation: boolean;
}

interface DepartmentSla {
  departmentId: string | null;
  departmentName: string;
  managers: number;
  repeatedEscalationManagers: number;
  metrics: SlaMetrics;
}

interface SlaPeriod {
  from: string;
  to: string;
  metrics: SlaMetrics;
}

interface SlaReport {
  generatedAtUtc: string;
  from: string;
  to: string;
  utcOffsetMinutes: number;
  grouping: Grouping;
  escalationAfterMinutes: number;
  repeatedEscalationManagers: number;
  summary: SlaMetrics;
  managers: ManagerSla[];
  departments: DepartmentSla[];
  periods: SlaPeriod[];
}

interface ReportFilter {
  from: string;
  to: string;
  grouping: Grouping;
}

function isoLocalDate(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function initialFilter(): ReportFilter {
  const to = new Date();
  const from = new Date(to);
  from.setDate(from.getDate() - 29);
  return { from: isoLocalDate(from), to: isoLocalDate(to), grouping: 'Week' };
}

function formatDate(value: string): string {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(parsed);
}

function formatMinutes(value: number): string {
  const total = Math.max(0, Math.round(value));
  if (total < 60) return `${total}m`;
  const hours = Math.floor(total / 60);
  const minutes = total % 60;
  return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
}

function csvCell(value: string | number | boolean): string {
  const text = String(value);
  return /[",\n\r]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export default function EscalationSlaAnalyticsPage() {
  const defaults = useMemo(initialFilter, []);
  const [draft, setDraft] = useState<ReportFilter>(defaults);
  const [filter, setFilter] = useState<ReportFilter>(defaults);
  const [report, setReport] = useState<SlaReport | null>(null);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const utcOffsetMinutes = -new Date().getTimezoneOffset();
    setLoading(true);
    setError('');
    apiFetch<SlaReport>(
      `/api/reports/website-work/follow-up-sla?from=${filter.from}&to=${filter.to}&utcOffsetMinutes=${utcOffsetMinutes}&grouping=${filter.grouping}`
    )
      .then(value => { if (!cancelled) setReport(value); })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load follow-up SLA analytics.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [filter, version]);

  const visibleManagers = useMemo(() => {
    const normalized = search.trim().toLocaleLowerCase();
    if (!normalized) return report?.managers ?? [];
    return (report?.managers ?? []).filter(item =>
      (item.ownerName || '').toLocaleLowerCase().includes(normalized) ||
      item.ownerEmail.toLocaleLowerCase().includes(normalized) ||
      (item.departmentName || '').toLocaleLowerCase().includes(normalized));
  }, [report, search]);

  function applyFilter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (draft.from > draft.to) {
      setError('Start date cannot be after end date.');
      return;
    }
    setFilter(draft);
  }

  function usePreset(days: number, grouping: Grouping) {
    const to = new Date();
    const from = new Date(to);
    from.setDate(from.getDate() - (days - 1));
    const next = { from: isoLocalDate(from), to: isoLocalDate(to), grouping };
    setDraft(next);
    setFilter(next);
  }

  function exportCsv() {
    if (!report) return;
    const header = [
      'Manager', 'Email', 'Current Department', 'Due Follow-ups', 'Resolved', 'SLA Met', 'SLA Breached',
      'Escalated', 'Open Overdue', 'Average Resolution Minutes', 'Average Overdue Minutes', 'SLA Met %', 'Repeated Escalation'
    ];
    const rows = report.managers.map(item => [
      item.ownerName || '',
      item.ownerEmail,
      item.departmentName || '',
      item.metrics.due,
      item.metrics.resolved,
      item.metrics.slaMet,
      item.metrics.slaBreached,
      item.metrics.escalated,
      item.metrics.openOverdue,
      item.metrics.averageResolutionMinutes.toFixed(1),
      item.metrics.averageOverdueMinutes.toFixed(1),
      item.metrics.slaMetPercent.toFixed(2),
      item.repeatedEscalation
    ]);
    const csv = [header, ...rows].map(row => row.map(csvCell).join(',')).join('\r\n');
    const blob = new Blob([`\uFEFF${csv}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `follow-up-sla-${report.from}-to-${report.to}.csv`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Reporting</p>
          <h1>Escalation & SLA Analytics</h1>
          <p className="muted">Manager follow-up SLA health, overdue resolution, escalation frequency and current department hotspots from server-recorded workflow activity.</p>
        </div>
        <div className="header-actions">
          <button className="ghost-button" type="button" onClick={() => setVersion(value => value + 1)}>Refresh</button>
          <button className="primary-button" type="button" disabled={!report} onClick={exportCsv}>Export CSV</button>
        </div>
      </div>

      <article className="panel management-form-panel">
        <div className="panel-heading">
          <div><h2>SLA report period</h2><p>Deadline dates use this browser's local timezone. Only follow-up deadlines reached by the report snapshot are counted.</p></div>
        </div>
        <form className="form-grid" onSubmit={applyFilter}>
          <label><span>From</span><input type="date" required value={draft.from} onChange={event => setDraft(value => ({ ...value, from: event.target.value }))} /></label>
          <label><span>To</span><input type="date" required value={draft.to} onChange={event => setDraft(value => ({ ...value, to: event.target.value }))} /></label>
          <label><span>Trend grouping</span><select value={draft.grouping} onChange={event => setDraft(value => ({ ...value, grouping: event.target.value as Grouping }))}><option value="Day">Daily</option><option value="Week">Weekly</option></select></label>
          <div className="form-actions">
            <button className="primary-button" type="submit">Apply</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(7, 'Day')}>7 days</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(30, 'Week')}>30 days</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(90, 'Week')}>90 days</button>
          </div>
        </form>
      </article>

      {error && <div className="error-banner" role="alert">{error}</div>}
      {loading && !report ? <div className="panel loading-block">Loading SLA analytics…</div> : report && (
        <>
          <section className="metric-grid compact-metrics">
            <article className="metric-card"><span>Due follow-ups</span><strong>{report.summary.due}</strong><small>Deadlines reached in period</small></article>
            <article className="metric-card"><span>SLA met</span><strong>{report.summary.slaMetPercent.toFixed(1)}%</strong><small>{report.summary.slaMet} resolved by due time</small></article>
            <article className="metric-card"><span>SLA breached</span><strong>{report.summary.slaBreached}</strong><small>Late resolution or unresolved past due</small></article>
            <article className="metric-card"><span>Escalated</span><strong>{report.summary.escalated}</strong><small>After {report.escalationAfterMinutes}m escalation grace</small></article>
            <article className="metric-card"><span>Open overdue</span><strong>{report.summary.openOverdue}</strong><small>Still current and unresolved</small></article>
            <article className="metric-card"><span>Avg resolution</span><strong>{formatMinutes(report.summary.averageResolutionMinutes)}</strong><small>Assignment to manager resolution</small></article>
            <article className="metric-card"><span>Avg overdue</span><strong>{formatMinutes(report.summary.averageOverdueMinutes)}</strong><small>Late resolved items only</small></article>
            <article className="metric-card"><span>Repeated escalation</span><strong>{report.repeatedEscalationManagers}</strong><small>Managers with 2+ escalated follow-ups</small></article>
          </section>

          <article className="panel table-panel">
            <div className="panel-heading">
              <div><h2>Manager SLA</h2><p>{formatDate(report.from)} – {formatDate(report.to)} · sorted by escalation and SLA breach</p></div>
              <input value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter manager or department" aria-label="Filter manager SLA" />
            </div>
            <div className="table-wrap"><table>
              <thead><tr><th>Manager</th><th>Current department</th><th>Due</th><th>Resolved</th><th>SLA met</th><th>Breached</th><th>Escalated</th><th>Open overdue</th><th>Avg resolution</th><th>SLA</th></tr></thead>
              <tbody>
                {visibleManagers.map(item => (
                  <tr key={item.ownerUserId}>
                    <td>
                      <strong>{item.ownerName || item.ownerEmail}</strong>
                      <small>{item.ownerName ? item.ownerEmail : item.repeatedEscalation ? 'Repeated escalation' : 'Manager account'}</small>
                    </td>
                    <td>{item.departmentName || 'No department'}</td>
                    <td>{item.metrics.due}</td>
                    <td>{item.metrics.resolved}</td>
                    <td>{item.metrics.slaMet}</td>
                    <td><strong>{item.metrics.slaBreached}</strong></td>
                    <td><strong>{item.metrics.escalated}</strong>{item.repeatedEscalation && <small>Repeated</small>}</td>
                    <td>{item.metrics.openOverdue}</td>
                    <td>{formatMinutes(item.metrics.averageResolutionMinutes)}</td>
                    <td><strong>{item.metrics.slaMetPercent.toFixed(1)}%</strong></td>
                  </tr>
                ))}
                {!visibleManagers.length && <tr><td colSpan={10} className="empty-cell">No manager follow-up deadlines match this report.</td></tr>}
              </tbody>
            </table></div>
          </article>

          <section className="dashboard-grid">
            <article className="panel table-panel">
              <div className="panel-heading"><div><h2>Department hotspots</h2><p>Uses each manager's current employee department.</p></div></div>
              <div className="table-wrap"><table>
                <thead><tr><th>Department</th><th>Managers</th><th>Due</th><th>Breached</th><th>Escalated</th><th>Repeated managers</th><th>SLA</th></tr></thead>
                <tbody>
                  {report.departments.map(item => (
                    <tr key={item.departmentId || item.departmentName}>
                      <td><strong>{item.departmentName}</strong></td>
                      <td>{item.managers}</td>
                      <td>{item.metrics.due}</td>
                      <td><strong>{item.metrics.slaBreached}</strong></td>
                      <td>{item.metrics.escalated}</td>
                      <td>{item.repeatedEscalationManagers}</td>
                      <td><strong>{item.metrics.slaMetPercent.toFixed(1)}%</strong></td>
                    </tr>
                  ))}
                  {!report.departments.length && <tr><td colSpan={7} className="empty-cell">No department SLA data for this period.</td></tr>}
                </tbody>
              </table></div>
            </article>

            <article className="panel table-panel">
              <div className="panel-heading"><div><h2>SLA trend</h2><p>{report.grouping === 'Day' ? 'Daily' : 'Weekly'} deadline cohorts</p></div></div>
              <div className="table-wrap"><table>
                <thead><tr><th>Period</th><th>Due</th><th>Met</th><th>Breached</th><th>Escalated</th><th>SLA</th></tr></thead>
                <tbody>
                  {report.periods.map(item => (
                    <tr key={`${item.from}-${item.to}`}>
                      <td><strong>{item.from === item.to ? formatDate(item.from) : `${formatDate(item.from)} – ${formatDate(item.to)}`}</strong></td>
                      <td>{item.metrics.due}</td>
                      <td>{item.metrics.slaMet}</td>
                      <td>{item.metrics.slaBreached}</td>
                      <td>{item.metrics.escalated}</td>
                      <td><strong>{item.metrics.slaMetPercent.toFixed(1)}%</strong></td>
                    </tr>
                  ))}
                  {!report.periods.length && <tr><td colSpan={6} className="empty-cell">No trend periods.</td></tr>}
                </tbody>
              </table></div>
            </article>
          </section>

          <p className="muted" style={{ marginTop: 12 }}>
            SLA analytics measure the internal follow-up workflow only. They do not infer employee productivity from external website activity and do not collect external page content, credentials, cookies, balances, earnings or browsing history.
          </p>
        </>
      )}
    </>
  );
}
