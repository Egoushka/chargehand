import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta } from './common';

/** Why an item waits: `waiting_for_slot`, `rate_limit`, `policy_ask`, `test_integrity`, `partial_draft`; null when nothing blocks it. */
export type FleetBlocker = string | null;

export interface FleetRun {
  id: string;
  kind: 'run' | 'batch';
  actor: string;
  state: string;
  since: string;
  age_seconds: number;
  blocked_by: FleetBlocker;
}

export interface FleetClaim {
  repository: string;
  issue: number;
  holder: string;
  state: string;
  expires_at?: string | null;
  since: string;
  age_seconds: number;
  blocked_by: FleetBlocker;
}

export interface FleetPull {
  url: string;
  number: number;
  repository: string;
  actor: string;
  state: string;
  since: string;
  age_seconds: number;
  blocked_by: FleetBlocker;
}

/** `fleet/v1`: what this process holds now, each item with its state, age and blocker. */
export interface FleetBody extends ContractMeta {
  contract_version: 'fleet/v1';
  runs: FleetRun[];
  claims: FleetClaim[];
  pull_requests: FleetPull[];
}

export const fleetQuery = () =>
  queryOptions({
    queryKey: ['fleet'],
    queryFn: ({ signal }) => getJson<FleetBody>('/v1/fleet', signal),
    refetchInterval: 10_000,
  });

export const useFleet = () => useQuery(fleetQuery());

const BLOCKERS: Record<string, string> = {
  waiting_for_slot: 'slot',
  rate_limit: 'rate limit',
  policy_ask: 'policy asks',
  test_integrity: 'test integrity',
  partial_draft: 'partial draft',
};

/** A blocker as a short label; one this console does not know shows as sent, with the underscores spaced. */
export const blockerLabel = (b: FleetBlocker): string =>
  b ? (BLOCKERS[b] ?? b.replaceAll('_', ' ')) : '-';
