import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { AccountBody, AccountStanding, ContractMeta, HostBody, RateLimitBody } from './common';
import type { InboxCounts } from './inbox';

export interface OverviewServer {
  version: string;
  halted: boolean;
  /** Unfinished top-level runs this process holds; for an account, its own. */
  runs_held: number;
  runs_queued: number;
  runs_running: number;
  max_unfinished: number;
  max_concurrent_runs?: number | null;
}

export interface OverviewSessions {
  running: number;
  /** `driven.max_parallel_total`; the owner's body only. */
  max_parallel_total?: number | null;
  batches: number;
}

/** Whether a provider usage or rate limit holds new starts, from the last task that hit one. */
export interface OverviewRateLimit {
  paused: boolean;
  resets_at?: string | null;
  pauses: RateLimitBody[];
}

export interface OverviewRunner {
  configured: boolean;
  reachable?: boolean | null;
  error?: string | null;
  elapsed_ms?: number | null;
}

export interface OverviewDriven {
  enabled: boolean;
  /** The owner's body only. */
  runner?: OverviewRunner | null;
  host?: HostBody | null;
  sessions: OverviewSessions;
  rate_limit: OverviewRateLimit;
}

export interface OverviewPoolCeiling {
  worker_tokens: number;
  max_open_decisions: number;
}

export interface OverviewPoolQueued {
  key: string;
  repository: string;
  area: string;
  title: string;
  priority: number;
  pull_request?: number | null;
  enqueued_at: string;
  /** The check (`decisions`, ...) or `starter`, `lost`, `ceiling` that last held the item; its detail text is not stored. */
  reason?: string | null;
}

export interface OverviewPoolWorker {
  id: string;
  repository: string;
  area: string;
  tokens: number;
  started_at: string;
  /** The items the worker holds now. */
  items: number;
}

export interface OverviewPoolDone {
  key: string;
  repository: string;
  area: string;
  title: string;
  done_at: string;
}

/** The task pool; lists are capped at 50, oldest first, and never carry an item's body. */
export interface OverviewPool {
  ceiling: OverviewPoolCeiling;
  queued: OverviewPoolQueued[];
  workers: OverviewPoolWorker[];
  /** Items done since `?since=` (default the last 24 hours). */
  done: OverviewPoolDone[];
}

/** `overview/v1`: the console's first screen. An account's body holds its own runs, spend and inbox, and no host, runner or other account. */
export interface OverviewBody extends ContractMeta {
  contract_version: 'overview/v1';
  scope: 'owner' | 'account';
  account: string;
  server: OverviewServer;
  driven: OverviewDriven;
  standing: AccountStanding;
  accounts?: AccountBody[] | null;
  inbox: InboxCounts;
  /** The owner's task pool; absent for an account and when the profile has no `http.pool`. */
  pool?: OverviewPool | null;
}

export const overviewQuery = () =>
  queryOptions({
    queryKey: ['overview'],
    queryFn: ({ signal }) => getJson<OverviewBody>('/v1/overview', signal),
    refetchInterval: 10_000,
  });

export const useOverview = () => useQuery(overviewQuery());
