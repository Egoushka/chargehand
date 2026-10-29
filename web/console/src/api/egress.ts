import { queryOptions, useQuery } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';
import { meQuery } from './queries';

/** A host the sessions of one repository were refused, which listing it would have let through. */
export interface LearnedHostBody {
  host: string;
  /** Refused connections, over every batch. */
  refusals: number;
  /** Batches that had at least one. */
  batches: number;
  first: string;
  last: string;
  last_batch: string;
  /** When the owner approved it; left out when not approved. */
  approved?: string | null;
}

/** `GET /v1/egress/learned` (owner only, ADR 0078 decision 4): the refused hosts per repository, the most recently refused first. */
export interface LearnedHostsBody {
  repositories: { repository: string; hosts: LearnedHostBody[] }[];
}

export const learnedHostsQuery = () =>
  queryOptions({
    queryKey: ['egress-learned'],
    queryFn: ({ signal }) => getJson<LearnedHostsBody>('/v1/egress/learned', signal),
  });

/** Asked for the owner only (an account would get 403); a server with no driven batches answers 409, and the view shows nothing. */
export const useLearnedHosts = () => {
  const me = useQuery(meQuery());
  return useQuery({ ...learnedHostsQuery(), enabled: me.data?.owner === true });
};

export interface LearnedHostChange {
  repository: string;
  host: string;
  approved: boolean;
  changed: boolean;
}

/** `POST /v1/egress/learned/approve`, or `/revoke` to take the approval back. */
export async function approveHost(
  repository: string,
  host: string,
  revoke = false,
): Promise<LearnedHostChange> {
  const reply = await call<LearnedHostChange>(
    revoke ? '/v1/egress/learned/revoke' : '/v1/egress/learned/approve',
    { method: 'POST', body: { repository, host } },
  );
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}
