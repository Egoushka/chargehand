import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { probeIdentity } from '../api/client';
import { getKeyState, setIdentity, signedIn, subscribeKey } from '../api/key';
import type { Me } from '../api/types';
import { TooltipProvider } from '../ui';
import { Shell } from './Shell';
import { SignIn } from './SignIn';
import { ToastProvider } from './toasts';

/** Without a key the sign-in screen stands in for every route, so a link to a run opens it after signing in. Behind Cloudflare Access the
 * identity signs in first, once per page load (ADR 0071); the form shows only when the server does not take it. */
export function Root() {
  const state = useSyncExternalStore(subscribeKey, getKeyState);
  const queryClient = useQueryClient();
  const tried = useRef(false);
  const [probing, setProbing] = useState(!signedIn(state));
  // What was fetched belongs to the key that fetched it.
  useEffect(() => {
    if (!signedIn(state)) queryClient.clear();
  }, [state, queryClient]);
  useEffect(() => {
    if (tried.current || signedIn(state)) return;
    tried.current = true;
    void probeIdentity<Me>().then((me) => {
      if (me) {
        queryClient.setQueryData(['me'], me);
        setIdentity();
      }
      setProbing(false);
    });
  }, [state, queryClient]);
  if (probing && !signedIn(state)) return null;
  return (
    <TooltipProvider>
      <ToastProvider>
        {signedIn(state) ? <Shell /> : <SignIn notice={state.notice} />}
      </ToastProvider>
    </TooltipProvider>
  );
}
