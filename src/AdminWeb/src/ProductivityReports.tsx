import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import './management.css';

type Grouping = 'Day' | 'Week';
type TimelineEventType = 'Assigned' | 'Started' | 'Submitted' | 'CorrectionRequested' | 'Approved' | 'Completed';

interface ProductivityMetrics {
  assigned: number;
  started: number;
  workingSeconds: number;
  submitted: number;
  approved: number;
  reopened: number;
  overdue: number;
  completionPercent: number;
}

interface EmployeeProductivity {
  employeeId: string;
  employeeCode: string;
  fullName: string;
  departmentId: string | null;
  departmentName: string | null;
  metrics: ProductivityMetrics;
}

interface ProductivityPeriod {
  from: string;
  to: string;
  metrics: ProductivityMetrics;
}

interface ProductivityReport {
  generatedAtUtc: string;
  from: string;
  to: string;
  utcOffsetMinutes: number;
  grouping: Grouping;
  summary: ProductivityMetrics;
  employees: EmployeeProductivity[];
  periods: ProductivityPeriod[];
}

interface TimelineEvent {
  type: TimelineEventType;
  atUtc: string;
  title: string;
  detail: string | null;
  actorEmail: string | null;
}

interface TimelineItem {
  taskId: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  title: string;
  status: string;
  dueDate: string | null;
  assignedAtUtc: string;
  firstStartedAtUtc: string | null;
  lastSubmittedAtUtc: string | null;
  approvedAtUtc: string | null;
  correctionCount: number;
  workingSecondsInPeriod: number;
  totalWorkingSeconds: number;
  overdueAtPeriodEnd: boolean;
  events: TimelineEvent[];
}

interface EmployeeTimeline {
  generatedAtUtc: string;
  from: string;
  to: string;
  utcOffsetMinutes: number;
  employeeId: string;
  employeeCode: string;
  fullName: string;
  departmentId: string | null;
  departmentName: string | null;
  items: TimelineItem[];
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
  from.setDate(from.getDate() - 6);
  return { from: isoLocalDate(from), to: isoLocalDate(to), grouping: 'Day' };
}

function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

function formatDate(value: string): string {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(parsed);
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function workStatusLabel(status: string): string {
  if (status === 'ToDo') return 'Ready';
  if (status === 'InProgress') return 'Working';
  if (status === 'Blocked') return 'Pending Review';
  if (status === 'Done') return 'Approved';
  return status;
}

function timelineEventLabel(type: TimelineEventType): string {
  if (type === 'CorrectionRequested') return 'Correction requested';
  return type;
}

function csvCell(value: string | number): string {
  const text = String(value);
  return /[",\n\r]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export default function ProductivityReportsPage() {
  const defaults = useMemo(initialFilter, []);
  const [draft, setDraft] = useState<ReportFilter>(defaults);
  const [filter, setFilter] = useState<ReportFilter>(defaults);
  const [report, setReport] = useState<ProductivityReport | null>(null);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);
  const [selectedEmployeeId, setSelectedEmployeeId] = useState<string | null>(null);
  const [timeline, setTimeline] = useState<EmployeeTimeline | null>(null);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [timelineError, setTimelineError] = useState('');

  useEffect(() => {
    let cancelled = false;
    const utcOffsetMinutes = -new Date().getTimezoneOffset();
    setLoading(true);
    setError('');
    apiFetch<ProductivityReport>(
      `/api/reports/website-work/productivity?from=${filter.from}&to=${filter.to}&utcOffsetMinutes=${utcOffsetMinutes}&grouping=${filter.grouping}`
    )
      .then(value => { if (!cancelled) setReport(value); })
      .catch(caught => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load productivity report.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [filter, version]);

  useEffect(() => {
    if (!selectedEmployeeId) {
      setTimeline(null);
      setTimelineError('');
      setTimelineLoading(false);
      return;
    }

    let cancelled = false;
    const utcOffsetMinutes = -new Date().getTimezoneOffset();
    setTimelineLoading(true);
    setTimelineError('');
    apiFetch<EmployeeTimeline>(
      `/api/reports/website-work/productivity/${selectedEmployeeId}/timeline?from=${filter.from}&to=${filter.to}&utcOffsetMinutes=${utcOffsetMinutes}`
    )
      .then(value => { if (!cancelled) setTimeline(value); })
      .catch(caught => {
        if (!cancelled) {
          setTimeline(null);
          setTimelineError(caught instanceof Error ? caught.message : 'Unable to load worker timeline.');
        }
      })
      .finally(() => { if (!cancelled) setTimelineLoading(false); });

    return () => { cancelled = true; };
  }, [selectedEmployeeId, filter, version]);

  const visibleEmployees = useMemo(() => {
    const normalized = search.trim().toLocaleLowerCase();
    if (!normalized) return report?.employees ?? [];
    return (report?.employees ?? []).filter(item =>
      item.fullName.toLocaleLowerCase().includes(normalized) ||
      item.employeeCode.toLocaleLowerCase().includes(normalized) ||
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
      'Employee Code', 'Employee', 'Department', 'Assigned', 'Started', 'Working Hours',
      'Submitted', 'Approved', 'Corrections / Reopen', 'Overdue', 'Completion %'
    ];
    const rows = report.employees.map(item => [
      item.employeeCode,
      item.fullName,
      item.departmentName || '',
      item.metrics.assigned,
      item.metrics.started,
      (item.metrics.workingSeconds / 3600).toFixed(2),
      item.metrics.submitted,
      item.metrics.approved,
      item.metrics.reopened,
      item.metrics.overdue,
      item.metrics.completionPercent.toFixed(2)
    ]);
    const csv = [header, ...rows].map(row => row.map(csvCell).join(',')).join('\r\n');
    const blob = new Blob([`\uFEFF${csv}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `website-work-productivity-${report.from}-to-${report.to}.csv`;
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
          <h1>Employee Productivity</h1>
          <p className="muted">Daily or weekly Website Work productivity using assignment, start, review and approval activity recorded by the server.</p>
        </div>
        <div className="header-actions">
          <button className="ghost-button" type="button" onClick={() => setVersion(value => value + 1)}>Refresh</button>
          <button className="primary-button" type="button" disabled={!report} onClick={exportCsv}>Export CSV</button>
        </div>
      </div>

      <article className="panel management-form-panel">
        <div className="panel-heading"><div><h2>Report period</h2><p>Dates follow this browser's local timezone.</p></div></div>
        <form className="form-grid" onSubmit={applyFilter}>
          <label><span>From</span><input type="date" required value={draft.from} onChange={event => setDraft(value => ({ ...value, from: event.target.value }))} /></label>
          <label><span>To</span><input type="date" required value={draft.to} onChange={event => setDraft(value => ({ ...value, to: event.target.value }))} /></label>
          <label><span>Trend grouping</span><select value={draft.grouping} onChange={event => setDraft(value => ({ ...value, grouping: event.target.value as Grouping }))}><option value="Day">Daily</option><option value="Week">Weekly</option></select></label>
          <div className="form-actions">
            <button className="primary-button" type="submit">Apply</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(1, 'Day')}>Today</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(7, 'Day')}>7 days</button>
            <button className="ghost-button" type="button" onClick={() => usePreset(30, 'Week')}>30 days</button>
          </div>
        </form>
      </article>

      {error && <div className="error-banner">{error}</div>}
      {loading && !report ? <div className="panel loading-block">Loading productivity report…</div> : report && (
        <>
          <div className="metric-grid compact-metrics">
            <article className="metric-card"><span>Assigned</span><strong>{report.summary.assigned}</strong><small>New Website Work targets</small></article>
            <article className="metric-card"><span>Started</span><strong>{report.summary.started}</strong><small>Targets first opened</small></article>
            <article className="metric-card"><span>Working time</span><strong>{formatDuration(report.summary.workingSeconds)}</strong><small>Start/reopen to submission</small></article>
            <article className="metric-card"><span>Submitted</span><strong>{report.summary.submitted}</strong><small>Completion review submissions</small></article>
            <article className="metric-card"><span>Approved</span><strong>{report.summary.approved}</strong><small>Final manager-approved completions</small></article>
            <article className="metric-card"><span>Corrections</span><strong>{report.summary.reopened}</strong><small>Needs-correction / reopen actions</small></article>
            <article className="metric-card"><span>Overdue</span><strong>{report.summary.overdue}</strong><small>Unresolved at period end</small></article>
            <article className="metric-card"><span>Completion rate</span><strong>{report.summary.completionPercent.toFixed(1)}%</strong><small>Assignments created in period and approved by period end</small></article>
          </div>

          <article className="panel table-panel">
            <div className="panel-heading">
              <div><h2>Employee productivity</h2><p>{formatDate(report.from)} – {formatDate(report.to)} · {report.employees.length} worker(s) with activity · click a worker name for timeline</p></div>
              <input value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter employee or department" aria-label="Filter employee productivity" />
            </div>
            <div className="table-wrap"><table>
              <thead><tr><th>Employee</th><th>Department</th><th>Assigned</th><th>Started</th><th>Working</th><th>Submitted</th><th>Approved</th><th>Corrections</th><th>Overdue</th><th>Completion</th></tr></thead>
              <tbody>
                {visibleEmployees.map(item => (
                  <tr key={item.employeeId}>
                    <td>
                      <button
                        type="button"
                        className="ghost-button"
                        onClick={() => setSelectedEmployeeId(item.employeeId)}
                        aria-label={`Open productivity timeline for ${item.fullName}`}
                      >
                        <strong>{item.fullName}</strong>
                      </button>
                      <small>{item.employeeCode}</small>
                    </td>
                    <td>{item.departmentName || '—'}</td>
                    <td>{item.metrics.assigned}</td>
                    <td>{item.metrics.started}</td>
                    <td><strong>{formatDuration(item.metrics.workingSeconds)}</strong></td>
                    <td>{item.metrics.submitted}</td>
                    <td><strong>{item.metrics.approved}</strong></td>
                    <td>{item.metrics.reopened}</td>
                    <td>{item.metrics.overdue}</td>
                    <td><strong>{item.metrics.completionPercent.toFixed(1)}%</strong></td>
                  </tr>
                ))}
                {!visibleEmployees.length && <tr><td colSpan={10} className="empty-cell">No Website Work activity matches this report.</td></tr>}
              </tbody>
            </table></div>
          </article>

          {selectedEmployeeId && (
            <article className="panel">
              <div className="panel-heading">
                <div>
                  <h2>{timeline?.fullName || 'Worker'} · detailed work timeline</h2>
                  <p>{timeline ? `${timeline.employeeCode} · ${timeline.departmentName || 'No department'} · ${formatDate(timeline.from)} – ${formatDate(timeline.to)}` : 'Loading the selected report period…'}</p>
                </div>
                <button className="ghost-button" type="button" onClick={() => setSelectedEmployeeId(null)}>Close</button>
              </div>

              {timelineError && <div className="error-banner">{timelineError}</div>}
              {timelineLoading && !timeline ? <div className="loading-block">Loading worker timeline…</div> : timeline && (
                <div style={{ display: 'grid', gap: 12 }}>
                  {timeline.items.map(item => (
                    <article key={item.taskId} style={{ border: '1px solid rgba(127,127,127,.22)', borderRadius: 12, padding: 14 }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'flex-start' }}>
                        <div>
                          <strong>{item.title}</strong>
                          <div className="muted" style={{ marginTop: 3 }}>{item.projectName} · {item.projectCode}</div>
                        </div>
                        <div style={{ textAlign: 'right' }}>
                          <strong>{workStatusLabel(item.status)}</strong>
                          {item.overdueAtPeriodEnd && <div><small>Overdue at period end</small></div>}
                        </div>
                      </div>

                      <div style={{ display: 'flex', gap: 18, flexWrap: 'wrap', marginTop: 12 }}>
                        <span><small>Assigned</small><br /><strong>{formatDateTime(item.assignedAtUtc)}</strong></span>
                        <span><small>First started</small><br /><strong>{formatDateTime(item.firstStartedAtUtc)}</strong></span>
                        <span><small>Working in period</small><br /><strong>{formatDuration(item.workingSecondsInPeriod)}</strong></span>
                        <span><small>Total working</small><br /><strong>{formatDuration(item.totalWorkingSeconds)}</strong></span>
                        <span><small>Corrections</small><br /><strong>{item.correctionCount}</strong></span>
                        <span><small>Last submitted</small><br /><strong>{formatDateTime(item.lastSubmittedAtUtc)}</strong></span>
                        <span><small>Approved</small><br /><strong>{formatDateTime(item.approvedAtUtc)}</strong></span>
                        <span><small>Due</small><br /><strong>{item.dueDate ? formatDate(item.dueDate) : '—'}</strong></span>
                      </div>

                      <div style={{ display: 'grid', gap: 8, marginTop: 14 }}>
                        {item.events.map((event, index) => (
                          <div key={`${event.type}-${event.atUtc}-${index}`} style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'baseline', borderTop: index === 0 ? undefined : '1px solid rgba(127,127,127,.14)', paddingTop: index === 0 ? 0 : 8 }}>
                            <strong style={{ minWidth: 145 }}>{timelineEventLabel(event.type)}</strong>
                            <span style={{ minWidth: 190 }}>{formatDateTime(event.atUtc)}</span>
                            <span className="muted">{event.actorEmail || 'System / recorded actor unavailable'}</span>
                            {event.detail && <span style={{ width: '100%', paddingLeft: 157 }}>{event.detail}</span>}
                          </div>
                        ))}
                      </div>
                    </article>
                  ))}
                  {!timeline.items.length && <div className="empty-cell">No Website Work lifecycle activity is relevant to this worker in the selected period.</div>}
                </div>
              )}
            </article>
          )}

          <article className="panel table-panel">
            <div className="panel-heading"><div><h2>{report.grouping === 'Week' ? 'Weekly' : 'Daily'} trend</h2><p>Working time is split across period boundaries; approval counts only final manager-approved completions.</p></div></div>
            <div className="table-wrap"><table>
              <thead><tr><th>Period</th><th>Assigned</th><th>Started</th><th>Working</th><th>Submitted</th><th>Approved</th><th>Corrections</th><th>Overdue at end</th><th>Completion</th></tr></thead>
              <tbody>
                {report.periods.map(period => (
                  <tr key={`${period.from}-${period.to}`}>
                    <td><strong>{formatDate(period.from)}</strong>{period.from !== period.to && <small>to {formatDate(period.to)}</small>}</td>
                    <td>{period.metrics.assigned}</td>
                    <td>{period.metrics.started}</td>
                    <td>{formatDuration(period.metrics.workingSeconds)}</td>
                    <td>{period.metrics.submitted}</td>
                    <td><strong>{period.metrics.approved}</strong></td>
                    <td>{period.metrics.reopened}</td>
                    <td>{period.metrics.overdue}</td>
                    <td>{period.metrics.completionPercent.toFixed(1)}%</td>
                  </tr>
                ))}
                {!report.periods.length && <tr><td colSpan={9} className="empty-cell">No periods in the selected range.</td></tr>}
              </tbody>
            </table></div>
          </article>

          <div className="panel">
            <strong>How the report is calculated</strong>
            <p className="muted">Working time runs from Start/Open (or a manager correction/reopen) until the employee submits completion. Submitted work is not counted as approved until a manager approves it. The completion rate follows the cohort assigned inside the selected period. Click a worker name to inspect target-by-target lifecycle history. CSV export contains the employee table only and never includes external website page content, credentials, balances or form data.</p>
          </div>
        </>
      )}
    </>
  );
}
