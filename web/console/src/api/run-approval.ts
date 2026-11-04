import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';

/** `GET /v1/limits/run-approval` (ADR 0092): the per-run spend above which a run waits for the owner. */
export interface RunApprovalBody {
  contract_version: 'run-approval/v1';
  /** Null asks about no run. */
  limit_usd: number | null;
  default_usd: number;
  /** How many runs wait for the owner now. */
  pending: number;
}

export const runApprovalQuery = (enabled = true) =>
  queryOptions({
    queryKey: ['run-approval'],
    queryFn: ({ signal }) => getJson<RunApprovalBody>('/v1/limits/run-approval', signal),
    enabled,
  });

export const useRunApproval = (enabled = true) => useQuery(runApprovalQuery(enabled));

/** `PUT /v1/limits/run-approval` with `{usd}`: a number above 0, or null to ask about no run. */
export function useSetRunApproval() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (usd: number | null) => {
      const reply = await call<RunApprovalBody>('/v1/limits/run-approval', {
        method: 'PUT',
        body: { usd },
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
    onSuccess: (body) => queryClient.setQueryData(runApprovalQuery().queryKey, body),
  });
}
