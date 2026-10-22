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

/** One input of the readiness score: `completion`, `verify`, `first_ci`, `human_fix` or `ci`. */
export interface ReadinessSignalBody {
  name: string;
  /** 0 to 1, higher is readier; left out when there is no data, so it does not count. */
  value?: number | null;
  weight: number;
  detail: string;
}

/** How ready a repository is for driven work: a weighted mean of the signals that have data, 0 to 100. */
export interface ReadinessBody {
  score?: number | null;
  threshold: number;
  /** Under the threshold: feature work there is warned about, not refused. */
  below: boolean;
  signals: ReadinessSignalBody[];
  /** What to fix, one line per weak signal. */
  gaps: string[];
  /** A request/v1 for one driven task that fixes the gaps; left out when there is none. */
  proposal?: Record<string, unknown> | null;
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
  readiness?: ReadinessBody | null;
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
