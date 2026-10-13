import { useEffect, useState } from 'react';
import { streamEvents } from './sse';
import type { RunStatus } from './types';

export interface RunEvents {
  /** `id` counts events in arrival order, for a stable list key. */
  events: { id: number; data: RunStatus }[];
  /** True once the server closed the stream: the run finished, or another process holds it. */
  closed: boolean;
  error: string | null;
}

/** The live events of a run (GET /v1/runs/{id}/events), oldest first. */
export function useRunEvents(runId: string): RunEvents {
  const [state, setState] = useState<RunEvents>({ events: [], closed: false, error: null });
  useEffect(() => {
    const abort = new AbortController();
    setState({ events: [], closed: false, error: null });
    streamEvents<RunStatus>(
      `/v1/runs/${encodeURIComponent(runId)}/events`,
      ({ data }) =>
        setState((s) => ({ ...s, events: [...s.events, { id: s.events.length, data }] })),
      abort.signal,
    ).then(
      () => setState((s) => ({ ...s, closed: !abort.signal.aborted })),
      (error: unknown) =>
        setState((s) => ({
          ...s,
          closed: true,
          error: error instanceof Error ? error.message : String(error),
        })),
    );
    return () => abort.abort();
  }, [runId]);
  return state;
}
