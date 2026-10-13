import { useSyncExternalStore } from 'react';

// What this browser remembers about how the console looks and what it tells you. Nothing secret: the key lives in
// sessionStorage (api/key.ts), this is localStorage, so it outlives the tab and stays on this browser.
const STORAGE_KEY = 'chargehand.console';

export type Theme = 'dark' | 'light';
export type Density = 'compact' | 'default' | 'comfortable';

/** The events that raise a notice; each can be switched off. */
export interface NotifyKinds {
  question: boolean;
  pull_request: boolean;
  batch: boolean;
  failure: boolean;
}

export interface Preferences {
  theme: Theme;
  density: Density;
  railCollapsed: boolean;
  /** The person turned browser notifications on (permission is asked once, from that click). */
  browserNotifications: boolean;
  /** The "get a browser notification" offer was dismissed. */
  offerDismissed: boolean;
  notify: NotifyKinds;
}

export const DENSITIES: readonly Density[] = ['compact', 'default', 'comfortable'];

const DEFAULT_NOTIFY: NotifyKinds = {
  question: true,
  pull_request: true,
  batch: true,
  failure: true,
};

/** Dark is the default; a browser that asks for light gets light until the person chooses. */
function systemTheme(): Theme {
  try {
    return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

function read(): Preferences {
  const base: Preferences = {
    theme: systemTheme(),
    density: 'default',
    railCollapsed: false,
    browserNotifications: false,
    offerDismissed: false,
    notify: DEFAULT_NOTIFY,
  };
  try {
    const stored = JSON.parse(
      localStorage.getItem(STORAGE_KEY) ?? 'null',
    ) as Partial<Preferences> | null;
    if (!stored || typeof stored !== 'object') return base;
    return {
      theme: stored.theme === 'light' || stored.theme === 'dark' ? stored.theme : base.theme,
      density: DENSITIES.includes(stored.density as Density)
        ? (stored.density as Density)
        : base.density,
      railCollapsed: stored.railCollapsed === true,
      browserNotifications: stored.browserNotifications === true,
      offerDismissed: stored.offerDismissed === true,
      notify: { ...DEFAULT_NOTIFY, ...(stored.notify ?? {}) },
    };
  } catch {
    return base; // storage blocked or not JSON: defaults, kept in memory
  }
}

let state: Preferences = read();
const listeners = new Set<() => void>();

/** Theme goes on <html> (the token file keys on it), density on <body> (`.ch-root` carries it, and portals sit in the body). */
export function applyPreferences(prefs: Preferences = state): void {
  document.documentElement.dataset.theme = prefs.theme;
  document.body.dataset.density = prefs.density;
}

export const getPreferences = (): Preferences => state;

export function subscribePreferences(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function updatePreferences(patch: Partial<Preferences>): void {
  state = { ...state, ...patch };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // kept in memory only
  }
  applyPreferences();
  for (const listener of listeners) listener();
}

/** Re-reads storage: for tests, which clear it between cases. */
export function resetPreferences(): void {
  state = read();
  applyPreferences();
  for (const listener of listeners) listener();
}

export const usePreferences = (): Preferences =>
  useSyncExternalStore(subscribePreferences, getPreferences);

export const toggleTheme = () =>
  updatePreferences({ theme: state.theme === 'dark' ? 'light' : 'dark' });

export const cycleDensity = () =>
  updatePreferences({
    density: DENSITIES[(DENSITIES.indexOf(state.density) + 1) % DENSITIES.length] ?? 'default',
  });

export const toggleRail = () => updatePreferences({ railCollapsed: !state.railCollapsed });
