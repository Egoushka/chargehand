import { useEffect, useState } from 'react';
import { streamEvents } from './sse';
import type { RunStatus } from './types';

export interface RunEvents {
  /** `id` counts events in arrival order, for a stable list key. */
  events: { id: number; data: RunStatus }[];
  /** True once the server closed the stream: the run finished, or another process holds it. */
  closed: boolean;
  error: string | null;
  /** Waiting to ask again after a stream that broke. */
  reconnecting: boolean;
}

/** An event is the same event when it says the same thing at the same time: a stream asked for again may replay what it sent. */
const keyOf = (e: RunStatus) =>
  `${e.at}|${e.event ?? e.status}|${e.task_id ?? ''}|${e.detail ?? ''}|${e.turns ?? ''}|${e.tokens ?? ''}`;

/**
 * The live events of a run (GET /v1/runs/{id}/events), oldest first. A stream that breaks is asked for again after `retryMs` (set `retryMs`
 * to 0 to stop at the first failure); a stream the server closes is finished.
 */
export function useRunEvents(runId: string, options: { retryMs?: number } = {}): RunEvents {
  const { retryMs = 0 } = options;
  const [state, setState] = useState<RunEvents>({
    events: [],
    closed: false,
    error: null,
    reconnecting: false,
  });
  useEffect(() => {
    const abort = new AbortController();
    const seen = new Set<string>();
    let timer: number | undefined;
    setState({ events: [], closed: false, error: null, reconnecting: false });
    const connect = () => {
      streamEvents<RunStatus>(
        `/v1/runs/${encodeURIComponent(runId)}/events`,
        ({ data }) => {
          const key = keyOf(data);
          if (seen.has(key)) return;
          seen.add(key);
          setState((s) => ({
            ...s,
            reconnecting: false,
            events: [...s.events, { id: s.events.length, data }],
          }));
        },
        abort.signal,
      ).then(
        () => setState((s) => ({ ...s, closed: !abort.signal.aborted, reconnecting: false })),
        (error: unknown) => {
          const message = error instanceof Error ? error.message : String(error);
          if (retryMs > 0 && !abort.signal.aborted) {
            setState((s) => ({ ...s, error: message, reconnecting: true }));
            timer = window.setTimeout(connect, retryMs);
          } else {
            setState((s) => ({ ...s, closed: true, error: message, reconnecting: false }));
          }
        },
      );
    };
    connect();
    return () => {
      abort.abort();
      window.clearTimeout(timer);
    };
  }, [runId, retryMs]);
  return state;
}
