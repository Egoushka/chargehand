import { keepPreviousData, queryOptions, useQueries, useQuery } from '@tanstack/react-query';
import { type BatchBody, batchQuery } from './batch';
import { getJson } from './client';
import type { RunList, RunSummary } from './types';

/** How many of the newest finished batches the Analytics and Learning screens read to see budgets in force and retros. */
export const RECENT_BATCHES = 10;

const finishedStates = new Set(['completed', 'failed', 'needs_input', 'denied']);

/** The newest top-level batch runs, any state. Read once and kept: a list of what finished does not need polling. */
export const recentRunsQuery = () =>
  queryOptions({
    queryKey: ['runs', 'recent-batches'],
    queryFn: ({ signal }) => getJson<RunList>('/v1/runs?kind=batch&limit=50', signal),
    select: (list) => list.runs,
    staleTime: 60_000,
  });

/** Finished batch runs with a start and an end, newest first: the durations of `GET /v1/runs?kind=batch`. */
export const batchDurationsQuery = () =>
  queryOptions({
    queryKey: ['runs', 'batch-durations'],
    queryFn: ({ signal }) => getJson<RunList>('/v1/runs?kind=batch&limit=500', signal),
    select: (list) => list.runs.filter((r) => finishedStates.has(r.status) && r.finished_at),
    staleTime: 60_000,
    placeholderData: keepPreviousData,
  });

export interface RecentBatches {
  runs: RunSummary[];
  /** The batches that have loaded, newest first. */
  batches: BatchBody[];
  /** The list or a batch is still loading. */
  pending: boolean;
  /** Some batch could not be read (an account may not read another's; a run may have been cleaned). */
  failed: number;
}

/** The newest finished batches in full: their task budgets and retros. One request per batch, cached for five minutes. */
export function useRecentBatches(count = RECENT_BATCHES): RecentBatches {
  const list = useQuery(recentRunsQuery());
  const runs = (list.data ?? []).filter((r) => finishedStates.has(r.status)).slice(0, count);
  const results = useQueries({
    queries: runs.map((r) => ({ ...batchQuery(r.run_id, 0), staleTime: 5 * 60_000 })),
  });
  return {
    runs,
    batches: results.flatMap((r) => (r.data ? [r.data] : [])),
    pending: list.isPending || results.some((r) => r.isPending),
    failed: results.filter((r) => r.isError).length,
  };
}
