import type { HubConnection } from '@microsoft/signalr';
import { apiUrl, getValidAccessToken, parseJwtClaims, readSession } from './api';

// One SignalR connection per tab, shared by every realtime component. The server adds the
// connection to groups from the token's permissions (RealtimeHub.OnConnectedAsync), so one
// connection receives exactly what the separate per-component connections used to receive.
// The client library is loaded on first use so it stays out of the initial bundle.

export type RealtimeState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';
type RealtimeHandler = (payload: never) => void;
type StateListener = (state: RealtimeState, error?: string) => void;

const handlers = new Map<string, Set<RealtimeHandler>>();
const stateListeners = new Set<StateListener>();
const registeredEvents = new Set<string>();
let connection: HubConnection | null = null;
let connectionSubject: string | undefined;
let creating = false;
let subscribers = 0;
let state: RealtimeState = 'disconnected';
let stopScheduled = false;

function currentSubject(): string | undefined {
  const session = readSession();
  return session ? parseJwtClaims(session.accessToken).sub : undefined;
}

function setState(next: RealtimeState, error?: string) {
  state = next;
  stateListeners.forEach(listener => listener(next, error));
}

function registerDispatchers() {
  if (!connection) return;
  for (const name of handlers.keys()) {
    if (registeredEvents.has(name)) continue;
    registeredEvents.add(name);
    connection.on(name, (payload: unknown) => {
      handlers.get(name)?.forEach(handler => (handler as (value: unknown) => void)(payload));
    });
  }
}

function stopConnection() {
  const current = connection;
  connection = null;
  connectionSubject = undefined;
  registeredEvents.clear();
  if (current) {
    setState('disconnected');
    void current.stop();
  }
}

function ensureConnection() {
  // A different signed-in user must never reuse a connection authorized for the previous one.
  if (connection && connectionSubject !== currentSubject()) stopConnection();
  if (connection) {
    registerDispatchers();
    return;
  }
  if (creating) return;
  creating = true;
  setState('connecting');
  import('@microsoft/signalr')
    .then(({ HubConnectionBuilder, LogLevel }) => {
      creating = false;
      if (subscribers === 0) {
        setState('disconnected');
        return;
      }
      const current = new HubConnectionBuilder()
        .withUrl(apiUrl('/hubs/realtime'), { accessTokenFactory: getValidAccessToken })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(LogLevel.Warning)
        .build();
      connection = current;
      connectionSubject = currentSubject();
      registeredEvents.clear();
      registerDispatchers();
      current.onreconnecting(() => { if (connection === current) setState('reconnecting'); });
      current.onreconnected(() => { if (connection === current) setState('connected'); });
      current.onclose(() => {
        // Forget a closed connection so the next subscriber starts a fresh one.
        if (connection !== current) return;
        connection = null;
        registeredEvents.clear();
        setState('disconnected');
      });
      current.start()
        .then(() => { if (connection === current) setState('connected'); })
        .catch(caught => {
          if (connection !== current) return;
          connection = null;
          registeredEvents.clear();
          setState('disconnected', caught instanceof Error ? caught.message : 'Realtime connection failed.');
        });
    })
    .catch(caught => {
      creating = false;
      setState('disconnected', caught instanceof Error ? caught.message : 'Realtime client failed to load.');
    });
}

/**
 * Subscribes to hub events and, optionally, connection state. Returns the cleanup for useEffect.
 * The shared connection stops once the last subscriber leaves; the stop is deferred by a microtask
 * so a page change (which unmounts and remounts the shell in one commit) keeps the connection.
 */
export function subscribeRealtime(events: Record<string, RealtimeHandler>, onState?: StateListener): () => void {
  for (const [name, handler] of Object.entries(events)) {
    if (!handlers.has(name)) handlers.set(name, new Set());
    handlers.get(name)!.add(handler);
  }
  if (onState) {
    stateListeners.add(onState);
    onState(state);
  }
  subscribers += 1;
  ensureConnection();

  return () => {
    for (const [name, handler] of Object.entries(events)) handlers.get(name)?.delete(handler);
    if (onState) stateListeners.delete(onState);
    subscribers -= 1;
    if (subscribers === 0 && !stopScheduled) {
      stopScheduled = true;
      queueMicrotask(() => {
        stopScheduled = false;
        if (subscribers === 0) stopConnection();
      });
    }
  };
}
