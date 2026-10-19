// The API key lives in sessionStorage: it ends with the tab and is sent only to this origin, as the bearer header (ADR 0054, 0056).
// Behind Cloudflare Access there is no key: the server takes the identity Cloudflare checked (ADR 0071), and `identity` says so.
const STORAGE_KEY = 'chargehand.key';

export interface KeyState {
  key: string | null;
  /** Signed in by the Cloudflare Access identity, with no key. */
  identity: boolean;
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

let state: KeyState = { key: read(), identity: false, notice: null };
const listeners = new Set<() => void>();

function publish(next: KeyState): void {
  state = next;
  for (const listener of listeners) listener();
}

export const getKeyState = (): KeyState => state;
export const getKey = (): string | null => state.key;
export const signedIn = (s: KeyState): boolean => s.key !== null || s.identity;

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
  publish({ key, identity: false, notice: null });
}

export function clearKey(notice: string | null = null): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // nothing to remove
  }
  publish({ key: null, identity: false, notice });
}

export function setIdentity(): void {
  publish({ key: null, identity: true, notice: null });
}

/** Sign out on purpose. An identity sign-in also ends the Cloudflare Access session, or the next page load would sign in again. */
export function signOut(): void {
  const identity = state.identity;
  clearKey();
  if (identity) window.location.assign('/cdn-cgi/access/logout');
}
