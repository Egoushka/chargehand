import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';

/** One repository's line of the last sweep. */
export interface SweepRepositoryBody {
  repository: string;
  ready: number;
  needs_you: number;
  blocked: number;
  epic: number;
  skipped: number;
  enqueued: number;
  deferred: number;
  /** One line: what stops the sweep in this repository, with the fix. */
  problem?: string;
}

export interface SweepRunBody {
  id: string;
  trigger: string;
  started: string;
}

/** `sweep/v1`: the last sweep, the one running now, and when the next starts. */
export interface SweepBody {
  contract_version: string;
  configured: boolean;
  schedule_on: boolean;
  time_zone: string;
  running?: SweepRunBody;
  last?: SweepRunBody & { finished: string; repositories: SweepRepositoryBody[] };
  next?: string;
  pool_holds: Array<{ repository: string; text: string; until: string }>;
}

export interface Setting<T> {
  profile: T;
  override?: T;
  effective: T;
}

/** `sweep-settings/v1`: each setting as the profile's value, the owner's override and the one in force. */
export interface SweepSettingsBody {
  contract_version: string;
  configured: boolean;
  time_zone: string;
  enabled: Setting<boolean>;
  at: Setting<string[]>;
  days: Setting<string[]>;
  repositories: Record<string, Setting<boolean>>;
  subscription_hold_percent: Setting<number>;
}

/** What `PUT /v1/sweep/settings` takes: it replaces every override, and a field left out clears its own. */
export interface SweepSettingsChange {
  enabled?: boolean;
  at?: string[];
  days?: string[];
  repositories?: Record<string, boolean>;
  subscription_hold_percent?: number;
}

export const sweepQuery = () =>
  queryOptions({
    queryKey: ['sweep'],
    queryFn: ({ signal }) => getJson<SweepBody>('/v1/sweep', signal),
    // A running sweep is watched until it ends.
    refetchInterval: (query) => (query.state.data?.running ? 3000 : false),
  });

export const sweepSettingsQuery = () =>
  queryOptions({
    queryKey: ['sweep-settings'],
    queryFn: ({ signal }) => getJson<SweepSettingsBody>('/v1/sweep/settings', signal),
  });

/** Starts a sweep now; during one the server answers the running id. */
export function useScan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const reply = await call<{ id: string }>('/v1/sweep', { method: 'POST' });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: sweepQuery().queryKey }),
  });
}

/** Sets the overrides; the answer replaces the cached settings, and the screen's next/schedule line is read again. */
export function useSetSweepSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (change: SweepSettingsChange) => {
      const reply = await call<SweepSettingsBody>('/v1/sweep/settings', {
        method: 'PUT',
        body: change,
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
    onSuccess: (body) => {
      queryClient.setQueryData(sweepSettingsQuery().queryKey, body);
      return queryClient.invalidateQueries({ queryKey: sweepQuery().queryKey });
    },
  });
}
