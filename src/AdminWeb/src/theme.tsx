import { useEffect, useState } from 'react';

// Theme preference is a per-device convenience (DESIGN_SYSTEM.md §3): Light, Dark or follow the system.
// public/theme-init.js applies the same rule before the first paint so the page does not flash.
export type ThemePreference = 'system' | 'light' | 'dark';
const storageKey = 'taskmonitoring-admin-theme';
const darkQuery = '(prefers-color-scheme: dark)';

function readPreference(): ThemePreference {
  try {
    const value = window.localStorage.getItem(storageKey);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}

function apply(preference: ThemePreference) {
  const dark = preference === 'dark' || (preference === 'system' && window.matchMedia(darkQuery).matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

export function useThemePreference(): [ThemePreference, (next: ThemePreference) => void] {
  const [preference, setPreference] = useState<ThemePreference>(readPreference);

  useEffect(() => {
    apply(preference);
    try {
      if (preference === 'system') window.localStorage.removeItem(storageKey);
      else window.localStorage.setItem(storageKey, preference);
    } catch {
      // Storage can be unavailable (private mode); the choice then lasts for this page only.
    }
    if (preference !== 'system') return;
    const media = window.matchMedia(darkQuery);
    const onChange = () => apply('system');
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, [preference]);

  return [preference, setPreference];
}

export function ThemeSelect() {
  const [preference, setPreference] = useThemePreference();
  return (
    <select
      className="theme-select"
      aria-label="Colour theme"
      value={preference}
      onChange={event => setPreference(event.target.value as ThemePreference)}
    >
      <option value="system">System theme</option>
      <option value="light">Light</option>
      <option value="dark">Dark</option>
    </select>
  );
}
