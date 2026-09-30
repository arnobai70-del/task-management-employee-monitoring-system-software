import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import './management.css';

type Grouping = 'Day' | 'Week';

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
              <div><h2>Employee productivity</h2><p>{formatDate(report.from)} – {formatDate(report.to)} · {report.employees.length} worker(s) with activity</p></div>
              <input value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter employee or department" aria-label="Filter employee productivity" />
            </div>
            <div className="table-wrap"><table>
              <thead><tr><th>Employee</th><th>Department</th><th>Assigned</th><th>Started</th><th>Working</th><th>Submitted</th><th>Approved</th><th>Corrections</th><th>Overdue</th><th>Completion</th></tr></thead>
              <tbody>
                {visibleEmployees.map(item => (
                  <tr key={item.employeeId}>
                    <td><strong>{item.fullName}</strong><small>{item.employeeCode}</small></td>
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
            <p className="muted">Working time runs from Start/Open (or a manager correction/reopen) until the employee submits completion. Submitted work is not counted as approved until a manager approves it. The completion rate follows the cohort assigned inside the selected period. CSV export contains the employee table only and never includes external website page content, credentials, balances or form data.</p>
          </div>
        </>
      )}
    </>
  );
}
