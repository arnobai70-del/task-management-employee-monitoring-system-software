import { useCallback, useEffect, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch } from './api';
import { adminNotificationChangedEvent } from './WebsiteWorkRealtimeNotice';

type AdminNotificationKind =
  | 'FollowUpAssigned'
  | 'FollowUpUpdated'
  | 'FollowUpRemoved'
  | 'FollowUpResolved'
  | 'FollowUpDueSoon'
  | 'FollowUpOverdue';

interface AdminNotification {
  id: string;
  kind: AdminNotificationKind;
  title: string;
  message: string;
  taskId: string;
  actionUrl: string;
  createdAtUtc: string;
  readAtUtc: string | null;
}

interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

interface NotificationSummary {
  unread: number;
  total: number;
}

interface MarkAllResponse {
  markedRead: number;
}

function formatDateTime(value: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function kindLabel(kind: AdminNotificationKind): string {
  if (kind === 'FollowUpDueSoon') return 'Due soon';
  if (kind === 'FollowUpOverdue') return 'Overdue';
  if (kind === 'FollowUpAssigned') return 'Assigned';
  if (kind === 'FollowUpUpdated') return 'Updated';
  if (kind === 'FollowUpResolved') return 'Resolved';
  return 'Changed';
}

export default function AdminNotificationsPage() {
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<PagedResponse<AdminNotification> | null>(null);
  const [summary, setSummary] = useState<NotificationSummary>({ unread: 0, total: 0 });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async (showLoading: boolean) => {
    if (showLoading) setLoading(true);
    try {
      const [notifications, nextSummary] = await Promise.all([
        apiFetch<PagedResponse<AdminNotification>>(
          `/api/admin-notifications?unreadOnly=${unreadOnly}&page=${page}&pageSize=50`
        ),
        apiFetch<NotificationSummary>('/api/admin-notifications/summary')
      ]);
      setData(notifications);
      setSummary(nextSummary);
      setError('');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to load notifications.');
    } finally {
      if (showLoading) setLoading(false);
    }
  }, [page, unreadOnly]);

  useEffect(() => {
    void load(true);
    const timer = window.setInterval(() => void load(false), 30_000);
    const refresh = () => void load(false);
    window.addEventListener(adminNotificationChangedEvent, refresh);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener(adminNotificationChangedEvent, refresh);
    };
  }, [load]);

  async function markRead(notification: AdminNotification) {
    if (notification.readAtUtc) return;
    setBusyId(notification.id);
    setError('');
    try {
      await apiFetch(`/api/admin-notifications/${notification.id}/read`, { method: 'POST' });
      window.dispatchEvent(new Event(adminNotificationChangedEvent));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to mark notification read.');
    } finally {
      setBusyId(null);
    }
  }

  async function markAllRead() {
    if (summary.unread === 0) return;
    setBusyId('all');
    setError('');
    try {
      await apiFetch<MarkAllResponse>('/api/admin-notifications/read-all', { method: 'POST' });
      window.dispatchEvent(new Event(adminNotificationChangedEvent));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to mark notifications read.');
    } finally {
      setBusyId(null);
    }
  }

  const pageCount = Math.max(1, Math.ceil((data?.totalCount ?? 0) / 50));

  return (
    <>
      <div className="page-header">
        <div>
          <p className="eyebrow">Administration</p>
          <h1>Notifications</h1>
          <p className="muted">Durable manager alerts stay here even when you were offline when the realtime event occurred.</p>
        </div>
        <div className="header-actions" style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <label style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
            <input
              type="checkbox"
              checked={unreadOnly}
              onChange={event => { setUnreadOnly(event.target.checked); setPage(1); }}
            /> Unread only
          </label>
          <button className="ghost-button" type="button" onClick={() => void markAllRead()} disabled={busyId === 'all' || summary.unread === 0}>
            {busyId === 'all' ? 'Updating…' : 'Mark all read'}
          </button>
          <button className="ghost-button" type="button" onClick={() => void load(true)}>Refresh</button>
        </div>
      </div>

      <section className="metric-grid">
        <article className="metric-card"><span>Unread</span><strong>{summary.unread}</strong><small>Needs your review</small></article>
        <article className="metric-card"><span>Total</span><strong>{summary.total}</strong><small>Durable notification history</small></article>
      </section>

      {error && <div className="error-banner" role="alert">{error}</div>}

      <article className="panel table-panel">
        <div className="panel-heading">
          <div><h2>{unreadOnly ? 'Unread notifications' : 'Notification history'}</h2><p>Follow-up assignment, due and overdue alerts</p></div>
          <span>Page {page} of {pageCount}</span>
        </div>
        {loading && !data ? <div className="loading-block">Loading notifications…</div> : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Status</th><th>Notification</th><th>When</th><th>Action</th></tr></thead>
              <tbody>
                {(data?.items || []).map(item => (
                  <tr key={item.id} style={{ opacity: item.readAtUtc ? .72 : 1 }}>
                    <td>
                      <span className={`status-badge status-${item.kind === 'FollowUpOverdue' ? 'overdue' : item.readAtUtc ? 'resolved' : 'active'}`}>
                        {item.readAtUtc ? 'Read' : kindLabel(item.kind)}
                      </span>
                    </td>
                    <td>
                      <strong>{item.title}</strong>
                      <small>{item.message}</small>
                    </td>
                    <td>
                      <strong>{formatDateTime(item.createdAtUtc)}</strong>
                      {item.readAtUtc && <small>Read {formatDateTime(item.readAtUtc)}</small>}
                    </td>
                    <td>
                      <NavLink className="text-link" to={item.actionUrl}>Open follow-up →</NavLink>
                      {!item.readAtUtc && (
                        <button
                          className="ghost-button"
                          type="button"
                          disabled={busyId === item.id}
                          onClick={() => void markRead(item)}
                          style={{ marginTop: 8 }}
                        >
                          {busyId === item.id ? 'Updating…' : 'Mark read'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {!data?.items.length && <tr><td colSpan={4} className="empty-cell">No notifications found.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
        {data && data.totalCount > 50 && (
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', paddingTop: 12 }}>
            <button className="ghost-button" type="button" disabled={page <= 1} onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</button>
            <button className="ghost-button" type="button" disabled={page >= pageCount} onClick={() => setPage(value => Math.min(pageCount, value + 1))}>Next</button>
          </div>
        )}
      </article>
    </>
  );
}
