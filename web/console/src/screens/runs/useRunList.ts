import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { runsQuery } from '../../api/queries';
import {
  isActive,
  MAX_WINDOW,
  matchesFilters,
  narrowsInBrowser,
  PAGE,
  type RunFilters,
  toRunFilter,
} from './model';

/** Below this many matches, a filter that only the browser applies asks for more rows by itself. */
const AUTOFILL = 25;
const NONE: never[] = [];

/**
 * The runs the filters ask for. The server takes one window of the newest runs (100, then 100 more as the list is scrolled, at most 500:
 * there is no cursor), narrowed by the filters it knows; the browser narrows what is left by search, preset and the state words.
 */
export function useRunList(filters: RunFilters) {
  const [window, setWindow] = useState(PAGE);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = globalThis.setInterval(() => setNow(Date.now()), 60_000);
    return () => globalThis.clearInterval(timer);
  }, []);

  const query = useQuery({
    ...runsQuery(toRunFilter(filters, now, window)),
    placeholderData: keepPreviousData,
    // A list with nothing in flight changes slowly.
    refetchInterval: (q) => (q.state.data?.runs.some(isActive) ? 5_000 : 30_000),
  });
  const loaded = query.data ?? NONE;
  const rows = useMemo(
    () => loaded.filter((run) => matchesFilters(run, filters, now)),
    [loaded, filters, now],
  );

  // The server gave a full window, so there may be older runs behind it.
  const canLoadMore = loaded.length >= window && window < MAX_WINDOW;
  const atCap = loaded.length >= MAX_WINDOW;
  const loadMore = useCallback(() => setWindow((w) => Math.min(MAX_WINDOW, w + PAGE)), []);
  const reset = useCallback(() => setWindow(PAGE), []);

  const settled = !query.isFetching && !query.isPlaceholderData;
  useEffect(() => {
    if (settled && canLoadMore && narrowsInBrowser(filters) && rows.length < AUTOFILL) loadMore();
  }, [settled, canLoadMore, filters, rows.length, loadMore]);

  return { query, loaded, rows, canLoadMore, atCap, loadMore, reset, now };
}
