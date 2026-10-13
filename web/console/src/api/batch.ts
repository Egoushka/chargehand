import { queryOptions, useQuery } from '@tanstack/react-query';
import type { GovernorCount } from './analytics';
import { getJson } from './client';
import type {
  AnswerHint,
  BatchRepository,
  ErrorBody,
  PullRequestBody,
  RateLimitBody,
} from './common';
import type { RunStatus, RunSummary } from './types';

/** The states a batch task is in (`batch/v1`). */
export type TaskState =
  | 'pending'
  | 'running'
  | 'completed'
  | 'failed'
  | 'needs_input'
  | 'not_started'
  | 'cancelled';

/** The batch's own limits as the request asked for them; a cap left out is no cap. */
export interface BatchLimitsBody {
  max_parallel?: number | null;
  max_tokens_total?: number | null;
  max_usd_total?: number | null;
  plan?: boolean | null;
}

/** A task's budget in the governor's dollar equivalent, and where it came from (ADR 0045). */
export interface BudgetBody {
  usd?: number | null;
  source: 'task' | 'flat' | 'history' | 'none';
}

/** `usd_estimate` is the dollar equivalent of the tokens, also on a subscription; `usd` is the priced cost and is absent where the credential has no price. */
export interface TaskSpendBody {
  usd?: number | null;
  usd_estimate?: number | null;
  usd_estimate_partial: boolean;
  input_tokens: number;
  output_tokens: number;
  cache_read_tokens: number;
  cache_write_tokens: number;
  turns?: number | null;
  continuations: number;
}

export interface GovernorBody {
  mode?: string | null;
  stop_reason?: string | null;
  decisions: GovernorCount[];
}

/** Where a running task's session stands, from its latest `session_progress` event. */
export interface TaskLiveBody {
  at: string;
  stage?: 'research' | 'write' | 'test' | 'review' | null;
  turns?: number | null;
  tokens?: number | null;
  usd_estimate?: number | null;
  usd_estimate_partial: boolean;
  cache_read?: number | null;
  cache_write?: number | null;
}

export interface TaskSessionBody {
  run_id: string;
  role: string;
}

export interface TaskLinkBody {
  run_id: string;
  task_id: string;
}

export interface BatchTaskBody {
  id: string;
  ref?: string | null;
  /** Cut to a preview in the batch view; whole in the task view. */
  goal?: string | null;
  goal_truncated: boolean;
  size?: 'S' | 'M' | 'L' | null;
  size_from?: string | null;
  depends_on: string[];
  split_from?: string | null;
  continues?: TaskLinkBody | null;
  state: TaskState;
  phase?: string | null;
  started_at?: string | null;
  finished_at?: string | null;
  run_id?: string | null;
  container?: string | null;
  branch?: string | null;
  pr_url?: string | null;
  error?: ErrorBody | null;
  detail?: string | null;
  budget?: BudgetBody | null;
  spend?: TaskSpendBody | null;
  governor?: GovernorBody | null;
  outcome?: string | null;
  test_integrity_flagged: boolean;
  live?: TaskLiveBody | null;
  sessions: TaskSessionBody[];
  questions: string[];
  answered?: boolean | null;
  answered_by: TaskLinkBody[];
  answer?: AnswerHint | null;
  pull_request?: PullRequestBody | null;
}

export interface ArtifactRef {
  kind: string;
  media_type: string;
  sha256: string;
  uri?: string | null;
}

export interface BatchResultBody {
  status: string;
  summary: string;
  error?: ErrorBody | null;
  input_tokens: number;
  output_tokens: number;
  cache_read_tokens: number;
  cache_write_tokens: number;
  usd?: number | null;
}

/** The events of a batch as run-status/v1. `available` is false for a run this process never held or a restart cut off. */
export interface TimelineBody {
  available: boolean;
  total: number;
  events: RunStatus[];
}

/** `batch/v1`: one driven batch. */
export interface BatchBody {
  contract_version: 'batch/v1';
  run: RunSummary;
  actor?: string | null;
  repository?: BatchRepository | null;
  limits: BatchLimitsBody;
  credential_delivery?: string | null;
  result?: BatchResultBody | null;
  plan?: unknown;
  retro?: unknown;
  tasks: BatchTaskBody[];
  timeline: TimelineBody;
  pull_requests: PullRequestBody[];
  artifacts: ArtifactRef[];
  rate_limits: RateLimitBody[];
  not_recorded: string[];
}

/** `events` is how many timeline events to return (0 to 2000, default 500); a list that only counts tasks asks for none. */
export const batchQuery = (id: string, events = 0) =>
  queryOptions({
    queryKey: ['batch', id, events],
    queryFn: ({ signal }) =>
      getJson<BatchBody>(`/v1/batches/${encodeURIComponent(id)}?events=${events}`, signal),
    refetchInterval: (query) => (query.state.data?.run.status === 'running' ? 5_000 : false),
  });

export const useBatch = (id: string, events = 0) => useQuery(batchQuery(id, events));
