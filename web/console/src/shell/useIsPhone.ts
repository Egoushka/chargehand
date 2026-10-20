import { useSyncExternalStore } from 'react';

/** Below this width the shell is the phone's: a tab bar for the rail, a sheet for a menu, touch-sized targets. */
export const PHONE_QUERY = '(max-width: 767px)';

function subscribe(listener: () => void): () => void {
  const query = window.matchMedia?.(PHONE_QUERY);
  query?.addEventListener('change', listener);
  return () => query?.removeEventListener('change', listener);
}

const snapshot = (): boolean => window.matchMedia?.(PHONE_QUERY).matches ?? false;

export const useIsPhone = (): boolean => useSyncExternalStore(subscribe, snapshot, () => false);

/** Up to this width the rail shows icons only, whatever the owner chose: the labels would take a third of the screen. */
export const NARROW_QUERY = '(max-width: 1023px)';

function subscribeNarrow(listener: () => void): () => void {
  const query = window.matchMedia?.(NARROW_QUERY);
  query?.addEventListener('change', listener);
  return () => query?.removeEventListener('change', listener);
}

const narrowSnapshot = (): boolean => window.matchMedia?.(NARROW_QUERY).matches ?? false;

export const useIsNarrow = (): boolean =>
  useSyncExternalStore(subscribeNarrow, narrowSnapshot, () => false);
