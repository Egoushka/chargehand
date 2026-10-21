import { useSyncExternalStore } from 'react';

/** Below this width the shell is the phone's: a tab bar for the rail, a sheet for a menu, touch-sized targets. */
export const PHONE_QUERY = '(max-width: 767px)';

/** From the phone's width to this one the rail shows icons only, whatever the saved preference, so the screen keeps its width. */
export const TABLET_QUERY = '(min-width: 768px) and (max-width: 1099px)';

function useMedia(query: string): boolean {
  return useSyncExternalStore(
    (listener) => {
      const list = window.matchMedia?.(query);
      list?.addEventListener('change', listener);
      return () => list?.removeEventListener('change', listener);
    },
    () => window.matchMedia?.(query).matches ?? false,
    () => false,
  );
}

export const useIsPhone = (): boolean => useMedia(PHONE_QUERY);

export const useIsTablet = (): boolean => useMedia(TABLET_QUERY);
