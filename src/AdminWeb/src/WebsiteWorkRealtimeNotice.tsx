import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useEffect, useRef, useState } from 'react';
import { apiUrl, getValidAccessToken } from './api';
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

export const websiteWorkCompletedEvent = 'taskmonitoring:website-work-completed';

export default function WebsiteWorkRealtimeNotice() {
  const { can } = useAuth();
  const allowed = can('tasks.read');
  const [latest, setLatest] = useState<WebsiteWorkCompletion | null>(null);
  const dismissTimer = useRef<number | null>(null);

  useEffect(() => {
    if (!allowed) return;

    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/realtime'), { accessTokenFactory: getValidAccessToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('websiteWorkCompleted', (completion: WebsiteWorkCompletion) => {
      setLatest(completion);
      window.dispatchEvent(new CustomEvent<WebsiteWorkCompletion>(websiteWorkCompletedEvent, { detail: completion }));
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      dismissTimer.current = window.setTimeout(() => setLatest(null), 12_000);
    });

    void connection.start().catch(() => {
      // Durable completion submission history remains available on the Website Work page.
    });

    return () => {
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      void connection.stop();
    };
  }, [allowed]);

  if (!latest) return null;

  return (
    <aside className="realtime-toast" role="status" aria-live="polite">
      <div>
        <strong>Completion submitted</strong>
        <span>{latest.message}</span>
        <small>{latest.projectName} · {latest.employeeCode}</small>
      </div>
      <button type="button" aria-label="Dismiss notification" onClick={() => setLatest(null)}>×</button>
    </aside>
  );
}
