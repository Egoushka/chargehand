import type { RunFilters, SavedView } from './model';
import { parseViews } from './model';

// Saved views of the runs list: this browser's, not secret, so localStorage. A browser that refuses storage (a private window, site data
// blocked) still shows the built-in views; saving then lasts until the page is closed.
const KEY = 'chargehand.console.runs.views';

export function readViews(): SavedView[] {
  try {
    return parseViews(localStorage.getItem(KEY));
  } catch {
    return [];
  }
}

export function writeViews(views: SavedView[]): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(views));
  } catch {
    // Not kept: the views in memory still work for this page.
  }
}

export function newViewId(): string {
  return `v-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

export type { RunFilters };
