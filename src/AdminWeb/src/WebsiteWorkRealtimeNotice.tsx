import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useCallback, useEffect, useRef, useState } from 'react';
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

export default function WebsiteWorkRealtimeNotice() {
  const { can } = useAuth();
  const allowed = can('tasks.read');
  const mayManage = can('tasks.manage');
  const [latest, setLatest] = useState<Notice | null>(null);
  const [pending, setPending] = useState<PendingWebsiteWork[]>([]);
  const [reviewError, setReviewError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const dismissTimer = useRef<number | null>(null);

  const showNotice = useCallback((notice: Notice) => {
    setLatest(notice);
    if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
    dismissTimer.current = window.setTimeout(() => setLatest(null), 12_000);
  }, []);

  const loadPending = useCallback(async () => {
    if (!mayManage) {
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
  }, [mayManage]);

  useEffect(() => {
    if (!mayManage) return;
    void loadPending();
    const timer = window.setInterval(() => void loadPending(), 5_000);
    return () => window.clearInterval(timer);
  }, [loadPending, mayManage]);

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

    void connection.start().catch(() => {
      // Pending review polling and durable completion history remain available if realtime reconnects later.
    });

    return () => {
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      void connection.stop();
    };
  }, [allowed, loadPending, showNotice]);

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
