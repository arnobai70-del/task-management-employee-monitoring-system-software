import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useCallback, useEffect, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch, apiUrl, getValidAccessToken } from './api';
import { useAuth } from './auth';

interface SecurityAlertSummary {
  open: number;
  acknowledged: number;
  escalated: number;
  criticalActive: number;
  assigned: number;
  resolvedToday: number;
}

interface SecurityAlertChanged {
  id: string;
  kind: string;
  severity: 'Warning' | 'Critical';
  status: 'Open' | 'Acknowledged' | 'Escalated' | 'Resolved';
  title: string;
  message: string;
  action: string;
  occurredAtUtc: string;
}

export const securityAlertChangedEvent = 'taskmonitoring:security-alert-changed';

export default function SecurityAlertRealtimeNotice() {
  const { can } = useAuth();
  const allowed = can('audit.read');
  const [summary, setSummary] = useState<SecurityAlertSummary | null>(null);
  const [latest, setLatest] = useState<SecurityAlertChanged | null>(null);
  const dismissTimer = useRef<number | null>(null);

  const loadSummary = useCallback(async () => {
    if (!allowed) {
      setSummary(null);
      return;
    }
    try {
      setSummary(await apiFetch<SecurityAlertSummary>('/api/security-alerts/summary'));
    } catch {
      // Polling retries; the durable alert event store remains authoritative.
    }
  }, [allowed]);

  const show = useCallback((alert: SecurityAlertChanged) => {
    setLatest(alert);
    if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
    dismissTimer.current = window.setTimeout(() => setLatest(null), 15_000);
  }, []);

  useEffect(() => {
    if (!allowed) return;
    void loadSummary();
    const timer = window.setInterval(() => void loadSummary(), 30_000);
    return () => window.clearInterval(timer);
  }, [allowed, loadSummary]);

  useEffect(() => {
    if (!allowed) return;
    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/realtime'), { accessTokenFactory: getValidAccessToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('securityAlertChanged', (alert: SecurityAlertChanged) => {
      window.dispatchEvent(new CustomEvent<SecurityAlertChanged>(securityAlertChangedEvent, { detail: alert }));
      void loadSummary();
      if (alert.action === 'Detected' || alert.action === 'Reopened' || alert.action === 'Escalated' || alert.severity === 'Critical') {
        show(alert);
      }
    });

    void connection.start().catch(() => {
      // Summary polling still surfaces durable alerts if realtime is unavailable.
    });

    return () => {
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      void connection.stop();
    };
  }, [allowed, loadSummary, show]);

  const activeCount = (summary?.open ?? 0) + (summary?.acknowledged ?? 0) + (summary?.escalated ?? 0);

  return (
    <>
      {latest && (
        <aside className="realtime-toast" role="alert" aria-live="assertive" style={{ bottom: 150 }}>
          <div>
            <strong>{latest.severity} security alert · {latest.action}</strong>
            <span>{latest.title}</span>
            <small>{latest.message}</small>
          </div>
          <button type="button" aria-label="Dismiss security alert" onClick={() => setLatest(null)}>×</button>
        </aside>
      )}

      {allowed && activeCount > 0 && (
        <NavLink
          to="/security-alerts"
          className={`status-badge status-${(summary?.criticalActive ?? 0) > 0 ? 'urgent' : 'warning'}`}
          aria-label={`${activeCount} active security alerts, ${summary?.criticalActive ?? 0} critical`}
          style={{
            position: 'fixed',
            right: 24,
            top: 76,
            zIndex: 30,
            textDecoration: 'none',
            padding: '8px 12px',
            boxShadow: '0 8px 24px rgba(0,0,0,.16)'
          }}
        >
          Security {activeCount}{(summary?.criticalActive ?? 0) > 0 ? ` · ${summary!.criticalActive} critical` : ''}
        </NavLink>
      )}
    </>
  );
}
