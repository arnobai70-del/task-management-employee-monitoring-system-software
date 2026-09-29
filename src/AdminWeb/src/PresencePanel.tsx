import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useEffect, useMemo, useState } from 'react';
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

function formatLastSeen(value: string | null): string {
  if (!value) return 'Never';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function statusClass(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

export default function PresencePanel() {
  const { can } = useAuth();
  const presenceAllowed = can('presence.read');
  const monitoringAllowed = can('monitoring.read');
  const [items, setItems] = useState<PresenceItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [live, setLive] = useState(false);

  const onlineCount = useMemo(() => items.filter(item => item.isOnline).length, [items]);
  const workingCount = useMemo(() => items.filter(item => item.workState === 'Working').length, [items]);
  const breakCount = useMemo(() => items.filter(item => item.workState === 'OnBreak').length, [items]);

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

  if (!presenceAllowed && !monitoringAllowed) return null;

  return (
    <>
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
