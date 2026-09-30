import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useEffect, useMemo, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch, apiUrl, getValidAccessToken } from './api';
import { useAuth } from './auth';
import MonitoringManagementPage from './MonitoringManagement';

interface PresenceItem {
  employeeId: string;
  employeeCode: string;
  fullName: string;
  departmentName: string | null;
  isOnline: boolean;
  workState: string;
  lastSeenAtUtc: string | null;
  clientKind: string | null;
  clientVersion: string | null;
  workSessionStartedAtUtc: string | null;
}

interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

type AttentionSeverity = 'Medium' | 'High' | 'Critical';

interface AttentionReason {
  type: 'Overdue' | 'LongWorking' | 'RepeatedCorrection' | 'PendingReview';
  severity: AttentionSeverity;
  message: string;
}

interface AttentionItem {
  taskId: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  title: string;
  status: string;
  dueDate: string | null;
  workingStartedAtUtc: string | null;
  currentWorkingSeconds: number;
  submittedAtUtc: string | null;
  pendingReviewSeconds: number;
  correctionCount: number;
  severity: AttentionSeverity;
  reasons: AttentionReason[];
}

interface AttentionResponse {
  generatedAtUtc: string;
  localDate: string;
  utcOffsetMinutes: number;
  thresholds: {
    longWorkingMinutes: number;
    pendingReviewMinutes: number;
    repeatedCorrectionCount: number;
  };
  total: number;
  critical: number;
  high: number;
  medium: number;
  items: AttentionItem[];
}

function formatLastSeen(value: string | null): string {
  if (!value) return 'Never';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function statusClass(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

function workStatusLabel(value: string): string {
  if (value === 'ToDo') return 'Ready';
  if (value === 'InProgress') return 'Working';
  if (value === 'Blocked') return 'Pending Review';
  if (value === 'Done') return 'Approved';
  return value;
}

function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

export default function PresencePanel() {
  const { can } = useAuth();
  const presenceAllowed = can('presence.read');
  const monitoringAllowed = can('monitoring.read');
  const [items, setItems] = useState<PresenceItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [live, setLive] = useState(false);
  const [attention, setAttention] = useState<AttentionResponse | null>(null);
  const [attentionLoading, setAttentionLoading] = useState(true);
  const [attentionError, setAttentionError] = useState('');
  const [attentionVersion, setAttentionVersion] = useState(0);

  const onlineCount = useMemo(() => items.filter(item => item.isOnline).length, [items]);
  const workingCount = useMemo(() => items.filter(item => item.workState === 'Working').length, [items]);
  const breakCount = useMemo(() => items.filter(item => item.workState === 'OnBreak').length, [items]);

  useEffect(() => {
    const utcOffsetMinutes = -new Date().getTimezoneOffset();
    let cancelled = false;

    const loadAttention = async (showLoading: boolean) => {
      if (showLoading) setAttentionLoading(true);
      try {
        const result = await apiFetch<AttentionResponse>(`/api/reports/website-work/attention?utcOffsetMinutes=${utcOffsetMinutes}&limit=20`);
        if (!cancelled) {
          setAttention(result);
          setAttentionError('');
        }
      } catch (caught) {
        if (!cancelled) setAttentionError(caught instanceof Error ? caught.message : 'Unable to load Website Work attention signals.');
      } finally {
        if (!cancelled && showLoading) setAttentionLoading(false);
      }
    };

    void loadAttention(true);
    const timer = window.setInterval(() => void loadAttention(false), 15_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [attentionVersion]);

  useEffect(() => {
    if (!presenceAllowed) return;
    let cancelled = false;

    const loadSnapshot = async (showLoading: boolean) => {
      if (showLoading) setLoading(true);
      try {
        const result = await apiFetch<PagedResponse<PresenceItem>>('/api/presence?page=1&pageSize=100');
        if (!cancelled) {
          setItems(result.items);
          setError('');
        }
      } catch (caught) {
        if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load workforce presence.');
      } finally {
        if (!cancelled && showLoading) setLoading(false);
      }
    };

    void loadSnapshot(true);
    const timer = window.setInterval(() => void loadSnapshot(false), 30_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [presenceAllowed]);

  useEffect(() => {
    if (!presenceAllowed) return;
    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/realtime'), { accessTokenFactory: getValidAccessToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('presenceChanged', (presence: PresenceItem) => {
      setItems(current => {
        const next = current.some(item => item.employeeId === presence.employeeId)
          ? current.map(item => item.employeeId === presence.employeeId ? presence : item)
          : [...current, presence];
        return next.sort((a, b) => a.fullName.localeCompare(b.fullName));
      });
    });
    connection.onreconnecting(() => setLive(false));
    connection.onreconnected(() => setLive(true));
    connection.onclose(() => setLive(false));

    void connection.start()
      .then(() => setLive(true))
      .catch(caught => setError(caught instanceof Error ? caught.message : 'Realtime connection failed.'));

    return () => { void connection.stop(); };
  }, [presenceAllowed]);

  return (
    <>
      <article className="panel table-panel">
        <div className="panel-heading">
          <div>
            <h2>Needs Attention</h2>
            <p>Website Work risk signals · auto-refresh every 15 seconds</p>
          </div>
          <div className="header-actions">
            {attention && <span>{attention.critical} critical · {attention.high} high · {attention.medium} medium</span>}
            <button className="ghost-button" type="button" onClick={() => setAttentionVersion(value => value + 1)}>Refresh</button>
          </div>
        </div>
        {attentionError && <div className="error-banner">{attentionError}</div>}
        {attentionLoading && !attention ? <div className="loading-block">Checking Website Work signals…</div> : attention && (
          <>
            <div className="progress-list" style={{ margin: '0 0 12px' }}>
              <div className="progress-row"><span>Long working threshold</span><strong>{attention.thresholds.longWorkingMinutes} min</strong></div>
              <div className="progress-row"><span>Pending review threshold</span><strong>{attention.thresholds.pendingReviewMinutes} min</strong></div>
              <div className="progress-row"><span>Repeated correction threshold</span><strong>{attention.thresholds.repeatedCorrectionCount}×</strong></div>
            </div>
            <div className="table-wrap">
              <table>
                <thead><tr><th>Priority</th><th>Worker</th><th>Target</th><th>State</th><th>Why it needs attention</th><th>Action</th></tr></thead>
                <tbody>
                  {attention.items.map(item => (
                    <tr key={item.taskId}>
                      <td><span className={`status-badge status-${statusClass(item.severity)}`}>{item.severity}</span></td>
                      <td><strong>{item.employeeName}</strong><small>{item.employeeCode}</small></td>
                      <td><strong>{item.title}</strong><small>{item.projectName} · {item.projectCode}</small></td>
                      <td>
                        <span className={`status-badge status-${statusClass(workStatusLabel(item.status))}`}>{workStatusLabel(item.status)}</span>
                        {item.status === 'InProgress' && item.currentWorkingSeconds > 0 && <small>{formatDuration(item.currentWorkingSeconds)} current session</small>}
                        {item.status === 'Blocked' && item.pendingReviewSeconds > 0 && <small>{formatDuration(item.pendingReviewSeconds)} waiting</small>}
                      </td>
                      <td>{item.reasons.map(reason => <small key={reason.type}><strong>{reason.type.replace(/([A-Z])/g, ' $1').trim()}:</strong> {reason.message}</small>)}</td>
                      <td><NavLink className="text-link" to={item.status === 'Blocked' ? '/website-work' : '/productivity'}>{item.status === 'Blocked' ? 'Review work →' : 'Inspect →'}</NavLink></td>
                    </tr>
                  ))}
                  {!attention.items.length && <tr><td colSpan={6} className="empty-cell">No Website Work currently crosses an attention threshold.</td></tr>}
                </tbody>
              </table>
            </div>
          </>
        )}
      </article>

      {presenceAllowed && <article className="panel table-panel">
        <div className="panel-heading">
          <div><h2>Live workforce</h2><p>{live ? 'Realtime connected' : 'Snapshot / reconnecting'}</p></div>
          <span>{onlineCount} online · {workingCount} working · {breakCount} on break</span>
        </div>
        {loading && !items.length ? <div className="loading-block">Loading presence…</div> : error && !items.length ? <div className="error-banner">{error}</div> : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Employee</th><th>Department</th><th>Presence</th><th>Work state</th><th>Last seen</th><th>Client</th></tr></thead>
              <tbody>
                {items.map(item => (
                  <tr key={item.employeeId}>
                    <td><strong>{item.fullName}</strong><small>{item.employeeCode}</small></td>
                    <td>{item.departmentName || '—'}</td>
                    <td><span className={`status-badge status-${item.isOnline ? 'active' : 'inactive'}`}>{item.isOnline ? 'Online' : 'Offline'}</span></td>
                    <td><span className={`status-badge status-${statusClass(item.workState)}`}>{item.workState}</span></td>
                    <td>{formatLastSeen(item.lastSeenAtUtc)}</td>
                    <td>{item.clientKind ? `${item.clientKind}${item.clientVersion ? ` ${item.clientVersion}` : ''}` : '—'}</td>
                  </tr>
                ))}
                {!items.length && <tr><td colSpan={6} className="empty-cell">No active employees found.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
      </article>}
      {monitoringAllowed && <MonitoringManagementPage />}
    </>
  );
}
