import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useSyncExternalStore } from 'react';
import { getKeyState, subscribeKey } from '../api/key';
import { TooltipProvider } from '../ui';
import { Shell } from './Shell';
import { SignIn } from './SignIn';
import { ToastProvider } from './toasts';

/** Without a key the sign-in screen stands in for every route, so a link to a run opens it after signing in. */
export function Root() {
  const { key, notice } = useSyncExternalStore(subscribeKey, getKeyState);
  const queryClient = useQueryClient();
  // What was fetched belongs to the key that fetched it.
  useEffect(() => {
    if (key === null) queryClient.clear();
  }, [key, queryClient]);
  return (
    <TooltipProvider>
      <ToastProvider>{key === null ? <SignIn notice={notice} /> : <Shell />}</ToastProvider>
    </TooltipProvider>
  );
}
