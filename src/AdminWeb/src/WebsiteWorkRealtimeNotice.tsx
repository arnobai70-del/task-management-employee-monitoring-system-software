import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useCallback, useEffect, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch, apiUrl, getValidAccessToken } from './api';
import { useAuth } from './auth';

export interface WebsiteWorkCompletion {
  taskId: string;
  projectId: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  taskTitle: string;
  completedAtUtc: string;
  message: string;
}

interface WebsiteWorkSubmission {
  taskId: string;
  projectId: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  taskTitle: string;
  submittedAtUtc: string;
  message: string;
}

type WebsiteWorkFollowUpRealtimeAction = 'Assigned' | 'Updated' | 'Removed' | 'Resolved';
type FollowUpState = 'Pending' | 'Overdue' | 'Resolved';

interface WebsiteWorkFollowUpRealtime {
  action: WebsiteWorkFollowUpRealtimeAction;
  taskId: string;
  projectId: string;
  projectName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  title: string;
  ownerUserId: string;
  ownerEmail: string;
  ownerName: string | null;
  dueAtUtc: string;
  occurredAtUtc: string;
  message: string;
}

interface FollowUpItem {
  taskId: string;
  projectName: string;
  employeeName: string;
  title: string;
  state: FollowUpState;
  dueAtUtc: string;
}

interface FollowUpInboxResponse {
  generatedAtUtc: string;
  pending: number;
  overdue: number;
  resolved: number;
  totalCount: number;
  items: FollowUpItem[];
}

export interface WebsiteWorkFollowUpSummary {
  pending: number;
  overdue: number;
}

interface PendingWebsiteWork {
  id: string;
  projectName: string;
  employeeCode: string | null;
  employeeName: string | null;
  title: string;
  status: string;
  updatedAtUtc: string;
}

interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

interface Notice {
  title: string;
  message: string;
  detail: string;
}

export const websiteWorkCompletedEvent = 'taskmonitoring:website-work-completed';
export const websiteWorkFollowUpChangedEvent = 'taskmonitoring:website-work-follow-up-changed';
export const websiteWorkFollowUpSummaryEvent = 'taskmonitoring:website-work-follow-up-summary';

function followUpNoticeTitle(action: WebsiteWorkFollowUpRealtimeAction): string {
  if (action === 'Assigned') return 'New follow-up assigned';
  if (action === 'Updated') return 'Follow-up updated';
  if (action === 'Resolved') return 'Follow-up resolved';
  return 'Follow-up changed';
}

export default function WebsiteWorkRealtimeNotice() {
  const { can } = useAuth();
  const mayManage = can('tasks.manage');
  const allowed = can('tasks.read') || mayManage;
  const [latest, setLatest] = useState<Notice | null>(null);
  const [pending, setPending] = useState<PendingWebsiteWork[]>([]);
  const [followUpSummary, setFollowUpSummary] = useState<WebsiteWorkFollowUpSummary>({ pending: 0, overdue: 0 });
  const [reviewError, setReviewError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const dismissTimer = useRef<number | null>(null);
  const overdueFollowUpIds = useRef<Set<string> | null>(null);

  const showNotice = useCallback((notice: Notice) => {
    setLatest(notice);
    if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
    dismissTimer.current = window.setTimeout(() => setLatest(null), 12_000);
  }, []);

  const loadPending = useCallback(async () => {
    if (!mayManage || !can('tasks.read')) {
      setPending([]);
      return;
    }

    try {
      const response = await apiFetch<PagedResponse<PendingWebsiteWork>>('/api/website-work?status=Blocked&page=1&pageSize=20');
      setPending(response.items);
      setReviewError('');
    } catch (caught) {
      setReviewError(caught instanceof Error ? caught.message : 'Unable to load pending website work reviews.');
    }
  }, [can, mayManage]);

  const publishFollowUpSummary = useCallback((summary: WebsiteWorkFollowUpSummary) => {
    setFollowUpSummary(summary);
    window.dispatchEvent(new CustomEvent<WebsiteWorkFollowUpSummary>(websiteWorkFollowUpSummaryEvent, { detail: summary }));
  }, []);

  const loadFollowUps = useCallback(async (announceOverdue: boolean) => {
    if (!mayManage) {
      overdueFollowUpIds.current = null;
      publishFollowUpSummary({ pending: 0, overdue: 0 });
      return;
    }

    try {
      const response = await apiFetch<FollowUpInboxResponse>('/api/website-work/follow-ups/mine?includeResolved=false');
      publishFollowUpSummary({ pending: response.pending, overdue: response.overdue });

      const nextOverdue = new Set(response.items.filter(item => item.state === 'Overdue').map(item => item.taskId));
      if (announceOverdue && overdueFollowUpIds.current !== null) {
        const newlyOverdue = response.items.filter(item => item.state === 'Overdue' && !overdueFollowUpIds.current!.has(item.taskId));
        if (newlyOverdue.length > 0) {
          const first = newlyOverdue[0];
          showNotice({
            title: 'Follow-up overdue',
            message: `${first.employeeName}: ${first.title}`,
            detail: newlyOverdue.length > 1
              ? `${first.projectName} · ${newlyOverdue.length} follow-ups are now overdue`
              : `${first.projectName} · due ${new Date(first.dueAtUtc).toLocaleString()}`
          });
        }
      }
      overdueFollowUpIds.current = nextOverdue;
    } catch {
      // The durable follow-up inbox stays authoritative; polling will retry without replacing other Admin Web errors.
    }
  }, [mayManage, publishFollowUpSummary, showNotice]);

  useEffect(() => {
    if (!mayManage) return;
    void loadPending();
    const timer = window.setInterval(() => void loadPending(), 5_000);
    return () => window.clearInterval(timer);
  }, [loadPending, mayManage]);

  useEffect(() => {
    if (!mayManage) return;
    void loadFollowUps(false);
    const timer = window.setInterval(() => void loadFollowUps(true), 15_000);
    return () => window.clearInterval(timer);
  }, [loadFollowUps, mayManage]);

  useEffect(() => {
    if (!allowed) return;

    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/realtime'), { accessTokenFactory: getValidAccessToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('websiteWorkSubmitted', (submission: WebsiteWorkSubmission) => {
      showNotice({
        title: 'Work submitted for review',
        message: submission.message,
        detail: `${submission.projectName} · ${submission.employeeCode}`
      });
      void loadPending();
    });

    connection.on('websiteWorkCompleted', (completion: WebsiteWorkCompletion) => {
      showNotice({
        title: 'Work completed',
        message: completion.message,
        detail: `${completion.projectName} · ${completion.employeeCode}`
      });
      window.dispatchEvent(new CustomEvent<WebsiteWorkCompletion>(websiteWorkCompletedEvent, { detail: completion }));
    });

    connection.on('websiteWorkFollowUpChanged', (followUp: WebsiteWorkFollowUpRealtime) => {
      showNotice({
        title: followUpNoticeTitle(followUp.action),
        message: followUp.message,
        detail: `${followUp.projectName} · due ${new Date(followUp.dueAtUtc).toLocaleString()}`
      });
      window.dispatchEvent(new CustomEvent<WebsiteWorkFollowUpRealtime>(websiteWorkFollowUpChangedEvent, { detail: followUp }));
      void loadFollowUps(false);
    });

    void connection.start().catch(() => {
      // Pending review/follow-up polling and durable server data remain available if realtime reconnects later.
    });

    return () => {
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      void connection.stop();
    };
  }, [allowed, loadFollowUps, loadPending, showNotice]);

  async function approve(item: PendingWebsiteWork) {
    if (!mayManage || !window.confirm(`Approve completion for "${item.title}" by ${item.employeeName || 'this worker'}?`)) return;
    setBusyId(item.id);
    setReviewError('');
    try {
      await apiFetch(`/api/website-work/${item.id}/approve`, {
        method: 'POST',
        body: JSON.stringify({ comment: null })
      });
      showNotice({
        title: 'Work approved',
        message: `${item.employeeName || item.employeeCode || 'Worker'} approved: ${item.title}`,
        detail: item.projectName
      });
      await loadPending();
    } catch (caught) {
      setReviewError(caught instanceof Error ? caught.message : 'Unable to approve website work.');
    } finally {
      setBusyId(null);
    }
  }

  async function reopen(item: PendingWebsiteWork) {
    if (!mayManage) return;
    const comment = window.prompt(`What correction is needed for "${item.title}"?`);
    if (comment === null) return;
    const normalized = comment.trim();
    if (normalized.length < 2) {
      setReviewError('Enter at least 2 characters explaining the required correction.');
      return;
    }

    setBusyId(item.id);
    setReviewError('');
    try {
      await apiFetch(`/api/website-work/${item.id}/reopen`, {
        method: 'POST',
        body: JSON.stringify({ comment: normalized })
      });
      showNotice({
        title: 'Correction requested',
        message: `${item.employeeName || item.employeeCode || 'Worker'}: ${item.title}`,
        detail: normalized
      });
      await loadPending();
    } catch (caught) {
      setReviewError(caught instanceof Error ? caught.message : 'Unable to reopen website work.');
    } finally {
      setBusyId(null);
    }
  }

  const followUpCount = followUpSummary.pending + followUpSummary.overdue;

  return (
    <>
      {latest && (
        <aside className="realtime-toast" role="status" aria-live="polite">
          <div>
            <strong>{latest.title}</strong>
            <span>{latest.message}</span>
            <small>{latest.detail}</small>
          </div>
          <button type="button" aria-label="Dismiss notification" onClick={() => setLatest(null)}>×</button>
        </aside>
      )}

      {mayManage && followUpCount > 0 && (
        <NavLink
          to="/follow-ups"
          className={`status-badge status-${followUpSummary.overdue > 0 ? 'overdue' : 'active'}`}
          aria-label={`${followUpCount} follow-ups, ${followUpSummary.overdue} overdue`}
          style={{
            position: 'fixed',
            right: 20,
            top: 76,
            zIndex: 30,
            textDecoration: 'none',
            padding: '8px 12px',
            boxShadow: '0 8px 24px rgba(0,0,0,.16)'
          }}
        >
          Follow-ups {followUpCount}{followUpSummary.overdue > 0 ? ` · ${followUpSummary.overdue} overdue` : ''}
        </NavLink>
      )}

      {mayManage && (pending.length > 0 || reviewError) && (
        <aside
          aria-label="Pending website work reviews"
          style={{
            position: 'fixed',
            right: 20,
            bottom: 20,
            width: 'min(430px, calc(100vw - 40px))',
            maxHeight: '55vh',
            overflow: 'auto',
            zIndex: 35,
            background: 'var(--panel-bg, #fff)',
            border: '1px solid rgba(127,127,127,.25)',
            borderRadius: 14,
            boxShadow: '0 18px 50px rgba(0,0,0,.18)',
            padding: 14
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, marginBottom: 10 }}>
            <div>
              <strong>Pending Website Work review</strong>
              <div style={{ opacity: .7, fontSize: 12 }}>{pending.length} waiting for approval</div>
            </div>
            <button type="button" className="ghost-button" onClick={() => void loadPending()}>Refresh</button>
          </div>

          {reviewError && <div className="error-banner" style={{ marginBottom: 10 }}>{reviewError}</div>}

          <div style={{ display: 'grid', gap: 10 }}>
            {pending.map(item => (
              <article key={item.id} style={{ border: '1px solid rgba(127,127,127,.2)', borderRadius: 10, padding: 10 }}>
                <strong>{item.title}</strong>
                <div style={{ fontSize: 13, marginTop: 4 }}>{item.employeeName || 'Unknown worker'} <span style={{ opacity: .65 }}>({item.employeeCode || '—'})</span></div>
                <div style={{ fontSize: 12, opacity: .7, marginTop: 2 }}>{item.projectName} · submitted {new Date(item.updatedAtUtc).toLocaleString()}</div>
                <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
                  <button type="button" className="primary-button" disabled={busyId === item.id} onClick={() => void approve(item)}>Approve</button>
                  <button type="button" className="ghost-button" disabled={busyId === item.id} onClick={() => void reopen(item)}>Needs correction</button>
                </div>
              </article>
            ))}
          </div>
        </aside>
      )}
    </>
  );
}
