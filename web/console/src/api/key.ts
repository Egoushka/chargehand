// The API key lives in sessionStorage: it ends with the tab and is sent only to this origin, as the bearer header (ADR 0054, 0056).
const STORAGE_KEY = 'chargehand.key';

export interface KeyState {
  key: string | null;
  /** Why the key is gone, when the server refused it; shown on the sign-in screen. */
  notice: string | null;
}

function read(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null; // storage blocked: the key then lives in memory until the page reloads
  }
}

let state: KeyState = { key: read(), notice: null };
const listeners = new Set<() => void>();

function publish(next: KeyState): void {
  state = next;
  for (const listener of listeners) listener();
}

export const getKeyState = (): KeyState => state;
export const getKey = (): string | null => state.key;

export function subscribeKey(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function setKey(key: string): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, key);
  } catch {
    // kept in memory only
  }
  publish({ key, notice: null });
}

export function clearKey(notice: string | null = null): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // nothing to remove
  }
  publish({ key: null, notice });
}
