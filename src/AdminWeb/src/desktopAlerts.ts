import { useCallback, useEffect, useState } from 'react';

// Desktop alerts put new security alerts and operations incidents on the admin's PC as operating-system
// notifications, so they are seen while the console is in a background tab or another window. They are
// opt-in per browser (a per-device convenience like the theme), need the browser's notification
// permission, and are skipped while the console window has focus, because the in-page toast shows then.
const storageKey = 'taskmonitoring-admin-desktop-alerts';
const changedEvent = 'taskmonitoring:desktop-alerts-changed';

export type DesktopAlertState = 'unsupported' | 'blocked' | 'off' | 'on';

function supported(): boolean {
  return typeof window !== 'undefined' && 'Notification' in window && window.isSecureContext;
}

// Fallback when storage is unavailable (private mode): the choice then lasts for this page only.
let enabledForPage = false;

function readEnabled(): boolean {
  try {
    return window.localStorage.getItem(storageKey) === 'on';
  } catch {
    return enabledForPage;
  }
}

function writeEnabled(enabled: boolean) {
  enabledForPage = enabled;
  try {
    if (enabled) window.localStorage.setItem(storageKey, 'on');
    else window.localStorage.removeItem(storageKey);
  } catch {
    // Kept in enabledForPage only.
  }
  window.dispatchEvent(new Event(changedEvent));
}

function currentState(): DesktopAlertState {
  if (!supported()) return 'unsupported';
  if (Notification.permission === 'denied') return 'blocked';
  return readEnabled() && Notification.permission === 'granted' ? 'on' : 'off';
}

export function useDesktopAlerts(): [DesktopAlertState, () => Promise<void>] {
  const [state, setState] = useState<DesktopAlertState>(currentState);

  useEffect(() => {
    const refresh = () => setState(currentState());
    window.addEventListener(changedEvent, refresh);
    window.addEventListener('storage', refresh);
    return () => {
      window.removeEventListener(changedEvent, refresh);
      window.removeEventListener('storage', refresh);
    };
  }, []);

  const toggle = useCallback(async () => {
    const now = currentState();
    if (now === 'unsupported' || now === 'blocked') return;
    if (now === 'on') {
      writeEnabled(false);
      return;
    }
    const permission = Notification.permission === 'granted' ? 'granted' : await Notification.requestPermission();
    writeEnabled(permission === 'granted');
  }, []);

  return [state, toggle];
}

export interface DesktopAlert {
  /** Same tag for the same alert, so several open tabs or repeated events replace one notification. */
  tag: string;
  title: string;
  body: string;
  onClick?: () => void;
}

export function notifyDesktop(alert: DesktopAlert): boolean {
  if (currentState() !== 'on') return false;
  if (document.visibilityState === 'visible' && document.hasFocus()) return false;
  try {
    const notification = new Notification(alert.title, { body: alert.body, tag: alert.tag });
    notification.onclick = () => {
      window.focus();
      alert.onClick?.();
      notification.close();
    };
    return true;
  } catch {
    // Some browsers only allow notifications from a service worker; the in-page toast still shows.
    return false;
  }
}

const labels: Record<DesktopAlertState, string> = {
  unsupported: 'Desktop alerts unavailable',
  blocked: 'Desktop alerts blocked',
  off: 'Desktop alerts off',
  on: 'Desktop alerts on'
};

const hints: Record<DesktopAlertState, string> = {
  unsupported: 'This browser or connection cannot show desktop notifications (they need HTTPS).',
  blocked: 'Notifications are blocked for this site. Allow them in the browser\'s site settings to turn desktop alerts on.',
  off: 'Show new security alerts and operations incidents as desktop notifications on this PC.',
  on: 'New security alerts and operations incidents appear as desktop notifications on this PC while the console is in the background. Click to turn off.'
};

export function desktopAlertLabel(state: DesktopAlertState) { return labels[state]; }
export function desktopAlertHint(state: DesktopAlertState) { return hints[state]; }
