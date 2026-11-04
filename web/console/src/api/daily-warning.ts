import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';

/** `daily-warning/v1`: the soft daily dollar limit and today's billed spend against it. Owner only. `limit_usd` is null when the warning is off. */
export interface DailyWarningBody {
  contract_version: 'daily-warning/v1';
  limit_usd: number | null;
  default_usd: number;
  spent_usd: number;
  exceeded: boolean;
  resets_at: string;
}

export const dailyWarningQuery = (enabled = true) =>
  queryOptions({
    queryKey: ['daily-warning'],
    queryFn: ({ signal }) => getJson<DailyWarningBody>('/v1/limits/daily-warning', signal),
    enabled,
    refetchInterval: 60_000,
  });

export const useDailyWarning = (enabled = true) => useQuery(dailyWarningQuery(enabled));

/** `PUT /v1/limits/daily-warning`; a number above 0, or null to switch the warning off. The answer replaces the cached body. */
export function useSetDailyWarning() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (usd: number | null) => {
      const reply = await call<DailyWarningBody>('/v1/limits/daily-warning', {
        method: 'PUT',
        body: { usd },
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
    onSuccess: (body) => queryClient.setQueryData(dailyWarningQuery().queryKey, body),
  });
}
