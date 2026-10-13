import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta, IntegratorMode, PullRequestBody } from './common';

export interface EcosystemBody {
  name: string;
  markers: string[];
}

export interface VerifyBody {
  command: string[];
  source: string;
}

export interface RepositoryBody {
  name: string;
  path: string;
  root: string;
  /** The checkout's `origin` with any credential in the URL removed. */
  remote?: string | null;
  /** The forge name (`owner/name`, `host/owner/name` for a host other than the default) when the remote parses. */
  repository?: string | null;
  default_branch?: string | null;
  head?: string | null;
  error?: string | null;
  ecosystems: EcosystemBody[];
  /** The command `chargehand init` would propose for `context.verify`: a guess from marker files, never run. */
  verify?: VerifyBody | null;
  verify_candidates: VerifyBody[];
  integrator_mode: IntegratorMode;
  watched_by_intake: boolean;
  open_pull_requests: PullRequestBody[];
}

export interface RootBody {
  path: string;
  exists: boolean;
}

/** `repositories/v1`: the checkouts under `repository_roots`, what is detected in them, and the open pull requests chargehand knows of. */
export interface RepositoriesBody extends ContractMeta {
  contract_version: 'repositories/v1';
  roots: RootBody[];
  repositories: RepositoryBody[];
}

export const repositoriesQuery = () =>
  queryOptions({
    queryKey: ['repositories'],
    queryFn: ({ signal }) => getJson<RepositoriesBody>('/v1/repositories', signal),
  });

export const useRepositories = () => useQuery(repositoriesQuery());
