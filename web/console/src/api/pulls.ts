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
