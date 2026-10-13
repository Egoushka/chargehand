import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, postJson, problems } from './client';
import type { LearnReport, Me, RunFilter, RunList, RunReply, RunResult, RunStatus } from './types';

const enc = encodeURIComponent;

export const meQuery = () =>
  queryOptions({
    queryKey: ['me'],
    queryFn: ({ signal }) => getJson<Me>('/v1/accounts/me', signal),
    refetchInterval: 30_000,
  });

export function runsPath(filter: RunFilter = {}): string {
  const query = new URLSearchParams({ limit: String(filter.limit ?? 100) });
  for (const key of ['status', 'kind', 'repository', 'actor', 'since'] as const) {
    const value = filter[key];
    if (value) query.set(key, value);
  }
  if (filter.runId) query.set('run_id', filter.runId);
  return `/v1/runs?${query}`;
}

export const runsQuery = (filter: RunFilter = {}) =>
  queryOptions({
    queryKey: ['runs', filter],
    queryFn: ({ signal }) => getJson<RunList>(runsPath(filter), signal),
    select: (list) => list.runs,
    refetchInterval: 5_000,
  });

/** GET /v1/runs/{id}: 200 is the result, 202 a run that is running, 410 one that was lost. */
export const runQuery = (id: string) =>
  queryOptions({
    queryKey: ['run', id],
    queryFn: async ({ signal }): Promise<RunReply> => {
      const reply = await call<RunResult | RunStatus>(`/v1/runs/${enc(id)}`, { signal });
      if (reply.status === 200) return { state: 'finished', result: reply.data as RunResult };
      if (reply.status === 202) return { state: 'running', status: reply.data as RunStatus };
      if (reply.status === 410) return { state: 'lost', status: reply.data as RunStatus };
      throw new ApiError(reply.status, problems(reply));
    },
    refetchInterval: (query) => (query.state.data?.state === 'running' ? 3_000 : false),
  });

/** One row of GET /v1/runs for an exact run id, or null when the caller sees no such run: it says whether the run is a batch or a single run. */
export const runSummaryQuery = (id: string) =>
  queryOptions({
    queryKey: ['run-summary', id],
    queryFn: ({ signal }) => getJson<RunList>(runsPath({ runId: id, limit: 1 }), signal),
    select: (list) => list.runs.find((run) => run.run_id === id) ?? null,
    refetchInterval: (query) => {
      const run = query.state.data?.runs[0];
      return run && (run.status === 'running' || run.status === 'queued') ? 5_000 : false;
    },
  });

/** What cancelling one run came to: the server starts it (`cancelling`), or the run had ended or is another process's (`finished`), is unknown (`missing`), or the call failed. */
export interface CancelOutcome {
  runId: string;
  result: 'cancelling' | 'finished' | 'missing' | 'failed';
  message?: string;
}

/** POST /v1/runs/{id}/cancel: 202 when this server holds the run and starts cancelling it, 404 for an unknown id, 409 when the run has finished or another process runs it. */
export async function cancelRun(runId: string): Promise<CancelOutcome> {
  const reply = await call(`/v1/runs/${enc(runId)}/cancel`, { method: 'POST' });
  if (reply.status === 202) return { runId, result: 'cancelling' };
  if (reply.status === 409)
    return { runId, result: 'finished', message: problems(reply).join('; ') };
  if (reply.status === 404)
    return { runId, result: 'missing', message: problems(reply).join('; ') };
  return { runId, result: 'failed', message: problems(reply).join('; ') };
}

/** Cancels runs one request each, and says what each came to. A cancelled run ends `failed` with error code `cancelled`. */
export function useCancelRuns() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (runIds: string[]) => Promise.all(runIds.map((id) => cancelRun(id))),
    onSettled: () =>
      Promise.all(
        ['runs', 'run-summary', 'run', 'batch', 'overview', 'inbox'].map((key) =>
          queryClient.invalidateQueries({ queryKey: [key] }),
        ),
      ),
  });
}

export const learnQuery = (since = '30d') =>
  queryOptions({
    queryKey: ['learn', since],
    queryFn: ({ signal }) =>
      getJson<LearnReport>(`/v1/learn${since ? `?since=${enc(since)}` : ''}`, signal),
  });

/** Halt (every unfinished run is cancelled, new runs are refused) or resume the server. The owner's key only. */
export function useHaltToggle() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (halt: boolean) => postJson<{ halted: boolean }>(halt ? '/v1/halt' : '/v1/resume'),
    onSuccess: () =>
      Promise.all(
        ['me', 'overview', 'runs'].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
      ),
  });
}
