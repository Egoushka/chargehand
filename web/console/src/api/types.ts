// The server's JSON, as far as the console reads it. Field names follow schemas/*/v1 (snake_case, as sent).

export type RunState =
  | 'queued'
  | 'running'
  | 'lost'
  | 'completed'
  | 'needs_input'
  | 'failed'
  | 'denied';

export const RUN_STATES: readonly RunState[] = [
  'queued',
  'running',
  'lost',
  'completed',
  'needs_input',
  'failed',
  'denied',
];

/** run-summary/v1: one row of GET /v1/runs. */
export interface RunSummary {
  contract_version: 'run-summary/v1';
  run_id: string;
  parent_run_id?: string | null;
  task_ref?: string | null;
  preset: string;
  status: RunState;
  started_at: string;
  finished_at?: string | null;
  usd?: number | null;
  branch?: string | null;
  pr_url?: string | null;
  error_code?: string | null;
  actor?: string | null;
  kind?: 'single' | 'batch';
  repository?: string | null;
  commit?: string | null;
  tokens?: number | null;
}

export interface RunList {
  runs: RunSummary[];
}

export interface RunFilter {
  status?: RunState;
  kind?: 'single' | 'batch';
  repository?: string;
  actor?: string;
  /** An ISO 8601 time: only runs started at or after it. */
  since?: string;
  /** An exact run id. */
  runId?: string;
  limit?: number;
}

/** run-status/v1: a run that has not finished (HTTP 202 and 410), and the data of each event on /v1/runs/{id}/events. */
export interface RunStatus {
  contract_version?: 'run-status/v1';
  run_id: string;
  status: RunState;
  event?: string;
  at?: string;
  detail?: string;
  task_id?: string;
  node_id?: string;
  node_status?: string;
  branch?: string;
  pr_url?: string;
  actor?: string;
  action?: string;
  executed_action?: string;
  runner?: string;
  usd?: number;
  usd_total?: number;
  /** Driven sessions: assistant messages so far, and input plus output tokens (session_progress, task_finished). */
  turns?: number;
  tokens?: number;
  stage?: 'research' | 'write' | 'test' | 'review';
  usd_estimate?: number;
  usd_estimate_partial?: boolean;
  cache_read?: number;
  cache_write?: number;
  memory_peak_mb?: number;
  [field: string]: unknown;
}

/** A piece of evidence a claim cites: `locator` is `path:line` or `path:start-end` for a `file`. */
export interface Evidence {
  id: string;
  kind: 'file' | 'diff' | 'url' | 'session_message' | 'commit' | 'input';
  locator: string;
  /** The commit the locator is read at. */
  commit?: string;
  sha256?: string;
}

/** A statement of a result with the evidence ids it cites. `support` is a second model's judgement, absent when no check ran. */
export interface Claim {
  text: string;
  evidence: string[];
  confidence: number;
  support?: 'supported' | 'partial' | 'unchecked';
}

export interface ResultSignature {
  alg: string;
  key_id: string;
  value: string;
}

/** result/v1, the part the console reads. */
export interface RunResult {
  contract_version: 'result/v1';
  task_id: string;
  node_id?: string;
  status: Exclude<RunState, 'queued' | 'running' | 'lost'>;
  summary: string;
  claims?: Claim[];
  evidence?: Evidence[];
  open_questions?: string[];
  confidence: number;
  usage: {
    input: number;
    output: number;
    cache_read: number;
    cache_write: number;
    usd: number | null;
  };
  actor?: string;
  signature?: ResultSignature;
  error?: { code: string; message: string; retryable: boolean; action?: string };
  [field: string]: unknown;
}

/** What GET /v1/runs/{id} says: the result (200), or the status of a run that is running (202) or lost (410). */
export type RunReply =
  | { state: 'finished'; result: RunResult }
  | { state: 'running'; status: RunStatus }
  | { state: 'lost'; status: RunStatus };

export interface WindowSpend {
  /** Billed to an API key; a subscription call adds none (ADR 0082). */
  usd: number;
  /** Every call at the price table, whoever paid; absent from an older server. */
  usd_at_api_price?: number;
  tokens: number;
  resets_at: string;
}

/** GET /v1/accounts/me. A limit that is absent is no cap. */
export interface Me {
  name: string;
  owner: boolean;
  driven: boolean;
  repositories?: string[] | null;
  limits: {
    max_usd_per_day?: number;
    max_usd_per_month?: number;
    max_tokens_per_day?: number;
    max_tokens_per_month?: number;
    max_concurrent_runs?: number;
  };
  spend: { day: WindowSpend; month: WindowSpend };
  unfinished_runs: number;
  halted: boolean;
  presets: string[];
  /** The month's spend at the trailing 7-day rate (CHARGEHAND-216); absent from an older server. */
  forecast?: SpendForecast | null;
}

/** Where the UTC month's spend is heading: the month so far plus the last 7 days' rate until it ends. */
export interface SpendForecast {
  rate_per_day: number;
  projected_usd: number;
  /** `max_usd_per_month`; absent: no cap. */
  cap_usd?: number | null;
  over_cap: boolean;
  /** When the cap is reached at that rate, within the month; now when it is reached already. */
  cap_reached_at?: string | null;
}

/** Pull requests of a group by what became of them; `open` includes those whose outcome was never read. */
export interface LearnPrCounts {
  total: number;
  merged_unchanged: number;
  merged_edited: number;
  merged: number;
  closed: number;
  open: number;
}

/**
 * GET /v1/learn: one row per repository and size class. Spend is the dollar equivalent of the tokens, over the `spend_known`
 * tasks that could be priced (nearest-rank percentiles); a rate is a share of `tasks`, or null when its denominator is none.
 */
export interface LearnGroup {
  repository: string;
  size_class: string;
  tasks: number;
  spend_known: number;
  spend_median_usd: number | null;
  spend_p80_usd: number | null;
  completed: number;
  partial: number;
  completion_rate: number;
  partial_rate: number;
  /** Tasks with at least one checkpoint decision, acted on or only logged. */
  checkpointed: number;
  checkpoint_rate: number;
  /** Of those, the ones that completed: the false-stop proxy of ADR 0045. */
  checkpointed_completed: number;
  checkpointed_completed_rate: number | null;
  prs: LearnPrCounts;
  merged_unchanged_rate: number | null;
}

export interface LearnReport {
  since: string | null;
  tasks: number;
  groups: LearnGroup[];
}
