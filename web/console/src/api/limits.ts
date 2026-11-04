import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';

/** One cap: the profile's value, the owner's override (null: none), and the one in force. */
export interface Limit {
  profile: number;
  override: number | null;
  effective: number;
}

/** `intake-limits/v1`: what intake runs on, and the pause switch. */
export interface IntakeLimitsBody {
  contract_version: string;
  configured: boolean;
  max_concurrent: Limit;
  max_review_rounds: Limit;
  max_usd_total: Limit;
  paused: boolean;
}

/** What `PUT /v1/intake/limits` takes: a null cap clears its override. */
export interface LimitsChange {
  max_concurrent: number | null;
  max_review_rounds: number | null;
  max_usd_total: number | null;
  paused: boolean;
}

export const intakeLimitsQuery = () =>
  queryOptions({
    queryKey: ['intake-limits'],
    queryFn: ({ signal }) => getJson<IntakeLimitsBody>('/v1/intake/limits', signal),
  });

/** Sets the overrides; the answer replaces the cached limits. */
export function useSetLimits() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (change: LimitsChange) => {
      const reply = await call<IntakeLimitsBody>('/v1/intake/limits', {
        method: 'PUT',
        body: change,
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
    onSuccess: (body) => queryClient.setQueryData(intakeLimitsQuery().queryKey, body),
  });
}
