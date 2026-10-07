import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { readStorage, storageKey, writeStorage } from '../lib/storage';
import {
  ThemeContext,
  type ResolvedTheme,
  type ThemeContextValue,
  type ThemePreference,
} from './themeContext';

function readStored(key: string, legacyKey: string): ThemePreference {
  const value = readStorage(key, legacyKey);
  return value === 'light' || value === 'dark' || value === 'system' ? value : 'system';
}

function systemTheme(): ResolvedTheme {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/**
 * Applies <html data-theme>. The preference (light / dark / system) is a per-viewer convenience stored under the
 * project-namespaced key `velmorb2b.commercial-platform.<surface>.theme`; without a stored choice the OS setting
 * (prefers-color-scheme) is used.
 */
export function ThemeProvider({ surface, children }: { surface: 'admin' | 'b2b'; children: ReactNode }) {
  const key = storageKey(surface, 'theme');
  const [preference, setPreferenceState] = useState<ThemePreference>(() =>
    readStored(key, `commercial.${surface}.theme`),
  );
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
      writeStorage(key, next);
    },
    [key],
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
