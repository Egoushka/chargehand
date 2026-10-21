import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta, PullRequestBody, PullRequestState } from './common';

/** `pulls/v1`: the pull requests chargehand opened for the tasks the caller may see, newest first. */
export interface PullsBody extends ContractMeta {
  contract_version: 'pulls/v1';
  pulls: PullRequestBody[];
}

export interface PullsParams {
  /** `owner/name`, exactly as the learning record names it. */
  repository?: string;
  state?: PullRequestState;
  /** Only the pull requests that wait for a person. */
  awaiting?: boolean;
  /** `1d`, `14d` or an ISO 8601 time. */
  since?: string;
  limit?: number;
}

export const pullsPath = (params: PullsParams = {}) => {
  const query = new URLSearchParams();
  if (params.repository) query.set('repository', params.repository);
  if (params.state) query.set('state', params.state);
  if (params.awaiting !== undefined) query.set('awaiting', String(params.awaiting));
  if (params.since) query.set('since', params.since);
  if (params.limit) query.set('limit', String(params.limit));
  const text = query.toString();
  return text ? `/v1/pulls?${text}` : '/v1/pulls';
};

export const pullsQuery = (params: PullsParams = {}) =>
  queryOptions({
    queryKey: ['pulls', params],
    queryFn: ({ signal }) => getJson<PullsBody>(pullsPath(params), signal),
    refetchInterval: 30_000,
  });

export const usePulls = (params: PullsParams = {}) => useQuery(pullsQuery(params));

/** A pull request that was merged, by any of the outcomes the server reads back. */
export const isMerged = (pr: PullRequestBody): boolean => pr.state.startsWith('merged');

/** An open pull request of the conflict radar. `files_listed` is false when its result kept fewer paths than the task changed, so an overlap may be missed. */
export interface ConflictPullBody {
  url: string;
  number?: number | null;
  batch_run_id: string;
  task_id: string;
  actor?: string | null;
  opened_at?: string | null;
  files_total: number;
  files_listed: boolean;
}

/** Two open pull requests of one repository whose tasks changed some of the same paths; `files` holds up to 50 of them. */
export interface ConflictBody {
  repository: string;
  pulls: ConflictPullBody[];
  files_shared: number;
  files: string[];
}

/** `conflicts/v1` (CHARGEHAND-216): open pull requests likely to conflict, most shared paths first. `unread` have no recorded changes and are not compared. */
export interface ConflictsBody extends ContractMeta {
  contract_version: 'conflicts/v1';
  open_pulls: number;
  conflicts: ConflictBody[];
  unread: ConflictPullBody[];
}

export const conflictsQuery = () =>
  queryOptions({
    queryKey: ['pulls', 'conflicts'],
    queryFn: ({ signal }) => getJson<ConflictsBody>('/v1/pulls/conflicts', signal),
    refetchInterval: 60_000,
  });
