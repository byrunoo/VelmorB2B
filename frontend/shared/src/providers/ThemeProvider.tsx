import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  ThemeContext,
  type ResolvedTheme,
  type ThemeContextValue,
  type ThemePreference,
} from './themeContext';

function readStored(key: string): ThemePreference {
  try {
    const value = window.localStorage.getItem(key);
    return value === 'light' || value === 'dark' || value === 'system' ? value : 'system';
  } catch {
    return 'system';
  }
}

function systemTheme(): ResolvedTheme {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/** Applies <html data-theme>. Preference is a per-viewer convenience stored in localStorage. */
export function ThemeProvider({ storageKey, children }: { storageKey: string; children: ReactNode }) {
  const [preference, setPreferenceState] = useState<ThemePreference>(() => readStored(storageKey));
  const [system, setSystem] = useState<ResolvedTheme>(systemTheme);

  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    if (!media) return;
    const onChange = () => setSystem(media.matches ? 'dark' : 'light');
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, []);

  const resolved: ResolvedTheme = preference === 'system' ? system : preference;

  useEffect(() => {
    document.documentElement.dataset.theme = resolved;
  }, [resolved]);

  const setPreference = useCallback(
    (next: ThemePreference) => {
      setPreferenceState(next);
      try {
        window.localStorage.setItem(storageKey, next);
      } catch {
        // storage unavailable (private mode) — preference stays in memory
      }
    },
    [storageKey],
  );

  const value = useMemo<ThemeContextValue>(
    () => ({
      preference,
      resolved,
      setPreference,
      toggle: () => setPreference(resolved === 'dark' ? 'light' : 'dark'),
    }),
    [preference, resolved, setPreference],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}
