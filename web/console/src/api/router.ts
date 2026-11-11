import { queryOptions } from '@tanstack/react-query';
import { getJson } from './client';

/** One pool of the budget router (ADR 0095), in drain order. Money is dollars at API price; an absent figure is not metered. */
export interface RouterPool {
  name: string;
  kind: 'free' | 'fixed' | 'monthly' | 'weekly';
  models: string[];
  period_start?: string | null;
  refill?: string | null;
  remaining_usd?: number | null;
  allowance_usd?: number | null;
  /** What today may still spend. */
  room_usd?: number | null;
  spent_period_usd: number;
  spent_today_usd: number;
  has_room: boolean;
  /** Why the pool may not take a call: expired, not started or exhausted. */
  why?: string | null;
  /** A configured limit the pool is over; it only warns. */
  warning?: string | null;
}

/** A route key and the tier it resolves to. */
export interface RouterRoute {
  key: string;
  tier: string;
  models: string[];
}

/** `router/v1`; `enabled` is false, with nothing listed, when the profile has no pools block. Owner only. */
export interface RouterBody {
  generated_at: string;
  contract_version: 'router/v1';
  enabled: boolean;
  time_zone?: string | null;
  default_tier?: string | null;
  pools: RouterPool[];
  routes: RouterRoute[];
}

export const routerQuery = () =>
  queryOptions({
    queryKey: ['analytics', 'router'],
    queryFn: ({ signal }) => getJson<RouterBody>('/v1/router', signal),
  });
