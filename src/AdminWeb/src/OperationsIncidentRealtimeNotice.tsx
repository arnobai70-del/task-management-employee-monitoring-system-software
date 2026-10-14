import { useCallback, useEffect, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { apiFetch } from './api';
import { useAuth } from './auth';
import { subscribeRealtime } from './realtime';
import { StatusRailItem } from './StatusRail';
import { startPolling } from './polling';

interface IncidentSummary {
  open: number;
  openCritical: number;
  openWarning: number;
  acknowledged: number;
  assigned: number;
  resolvedToday: number;
}

interface IncidentChanged {
  id: string;
  kind: string;
  severity: 'Info' | 'Warning' | 'Critical';
  status: 'Open' | 'Acknowledged' | 'Resolved';
  title: string;
  message: string;
  action: string;
  occurredAtUtc: string;
}

export const operationsIncidentChangedEvent = 'taskmonitoring:operations-incident-changed';

export default function OperationsIncidentRealtimeNotice() {
  const { can } = useAuth();
  const allowed = can('reports.read');
  const [summary, setSummary] = useState<IncidentSummary | null>(null);
  const [latest, setLatest] = useState<IncidentChanged | null>(null);
  const dismissTimer = useRef<number | null>(null);

  const loadSummary = useCallback(async () => {
    if (!allowed) {
      setSummary(null);
      return;
    }
    try {
      setSummary(await apiFetch<IncidentSummary>('/api/operations/incidents/summary'));
    } catch {
      // Polling retries; the Operations page remains the authoritative view.
    }
  }, [allowed]);

  const show = useCallback((incident: IncidentChanged) => {
    setLatest(incident);
    if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
    dismissTimer.current = window.setTimeout(() => setLatest(null), 12_000);
  }, []);

  useEffect(() => {
    if (!allowed) return;
    void loadSummary();
    const stopPolling = startPolling(() => void loadSummary(), 30_000);
    return () => stopPolling();
  }, [allowed, loadSummary]);

  useEffect(() => {
    if (!allowed) return;
    // Summary polling and the durable incident event store remain available if realtime is unavailable.
    const unsubscribe = subscribeRealtime({
      operationsIncidentChanged: (incident: IncidentChanged) => {
        window.dispatchEvent(new CustomEvent<IncidentChanged>(operationsIncidentChangedEvent, { detail: incident }));
        void loadSummary();
        if (incident.action !== 'Updated' || incident.severity === 'Critical') {
          show(incident);
        }
      }
    });

    return () => {
      if (dismissTimer.current !== null) window.clearTimeout(dismissTimer.current);
      unsubscribe();
    };
  }, [allowed, loadSummary, show]);

  const activeCount = (summary?.open ?? 0) + (summary?.acknowledged ?? 0);

  return (
    <>
      {latest && (
        <aside className="realtime-toast" role="status" aria-live="polite" style={{ bottom: 24 }}>
          <div>
            <strong>{latest.severity} incident · {latest.action}</strong>
            <span>{latest.title}</span>
            <small>{latest.message}</small>
          </div>
          <button type="button" aria-label="Dismiss incident notification" onClick={() => setLatest(null)}>×</button>
        </aside>
      )}

      {allowed && activeCount > 0 && (
        <StatusRailItem>
          <NavLink
            to="/operations"
            className={`status-badge rail-pill status-${(summary?.openCritical ?? 0) > 0 ? 'urgent' : 'warning'}`}
            aria-label={`${activeCount} active operations incidents, ${summary?.openCritical ?? 0} critical`}
          >
            Incidents {activeCount}{(summary?.openCritical ?? 0) > 0 ? ` · ${summary!.openCritical} critical` : ''}
          </NavLink>
        </StatusRailItem>
      )}
    </>
  );
}
