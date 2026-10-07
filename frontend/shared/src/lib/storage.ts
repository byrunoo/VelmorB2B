/**
 * Browser-storage keys of this product. Every key is namespaced with the project so other apps served from the same
 * origin (e.g. another project on localhost:5173) can never read or overwrite our values.
 *
 *   velmorb2b.commercial-platform.<scope>.<name>   e.g. velmorb2b.commercial-platform.admin.theme
 *
 * Storage holds per-viewer conveniences only (theme, collapsed sidebar) — never tokens or business data.
 */
export const STORAGE_NAMESPACE = 'velmorb2b.commercial-platform';

export type StorageScope = 'admin' | 'b2b' | 'shell';

export function storageKey(scope: StorageScope, name: string): string {
  return `${STORAGE_NAMESPACE}.${scope}.${name}`;
}

/** Reads a key; on first use copies a value saved under a pre-namespace legacy key, then removes the legacy key. */
export function readStorage(key: string, legacyKey?: string): string | null {
  try {
    const value = window.localStorage.getItem(key);
    if (value !== null || !legacyKey) return value;
    const legacy = window.localStorage.getItem(legacyKey);
    if (legacy !== null) {
      window.localStorage.setItem(key, legacy);
      window.localStorage.removeItem(legacyKey);
    }
    return legacy;
  } catch {
    return null; // storage unavailable (private mode, blocked) — callers fall back to defaults
  }
}

export function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // storage unavailable — the value stays in memory for this session
  }
}
