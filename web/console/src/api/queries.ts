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
  for (const key of ['status', 'kind', 'repository', 'actor'] as const) {
    const value = filter[key];
    if (value) query.set(key, value);
  }
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
