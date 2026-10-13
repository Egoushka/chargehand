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
