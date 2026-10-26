// Shapes shared by the console's read contracts (GET /v1/overview, /v1/inbox, /v1/batches/..., /v1/pulls and the rest).
// Written by hand from the records in src/Chargehand.Server/Console and docs/guide/server.md#console-routes. The server writes
// snake_case names and leaves a null field out, so an optional field here is "absent or null"; a timestamp is an ISO 8601 string
// and a decimal is a number.

import type { Me } from './types';

/** Fields every console body carries. */
export interface ContractMeta {
  generated_at: string;
  /** Things the console might expect and the server does not keep, as plain sentences. */
  not_recorded: string[];
}

export interface ErrorBody {
  code: string;
  message: string;
  retryable: boolean;
  action?: string | null;
}

export interface BatchRepository {
  path: string;
  commit: string;
}

export interface ContinueFrom {
  run_id: string;
  task_id: string;
}

/** How to answer a question: the body of a `POST /v1/runs` is a driven task with `continue_from` and `answers` (ADR 0045). */
export interface AnswerHint {
  method: 'POST';
  path: string;
  continue_from: ContinueFrom;
  repository?: BatchRepository | null;
  preset: string;
}

export type PullRequestState =
  | 'open'
  | 'merged_unchanged'
  | 'merged_edited'
  | 'merged'
  | 'closed'
  | 'unknown';
export type IntegratorMode = 'off' | 'ask' | 'merge';
export type AwaitingReason = 'policy_ask' | 'test_integrity' | 'partial_draft';

export interface PullRequestBody {
  url: string;
  number?: number | null;
  /** `owner/name`, or `host/owner/name` for a host other than the default. */
  repository?: string | null;
  batch_run_id: string;
  task_id: string;
  run_id?: string | null;
  state: PullRequestState;
  state_at?: string | null;
  partial: boolean;
  test_integrity_flagged: boolean;
  integrator_mode: IntegratorMode;
  awaiting_person: boolean;
  awaiting_reason?: AwaitingReason | null;
  integrator_last?: string | null;
  actor?: string | null;
  opened_at?: string | null;
}

export interface RateLimitBody {
  batch_run_id: string;
  task_id: string;
  observed_at: string;
  resets_at: string;
}

/** The host that runs the session containers, as admission reads it (ADR 0045). */
export interface HostBody {
  available_mb?: number | null;
  total_mb?: number | null;
  swap_total_mb?: number | null;
  swap_free_mb?: number | null;
  load1?: number | null;
  cpus?: number | null;
  free_after_reserve_mb?: number | null;
  reserve_mb: number;
  max_load_per_cpu?: number | null;
}

/** An account's caps and spend, as `GET /v1/accounts/me` says them. */
export type AccountStanding = Me;

export interface CredentialBody {
  /** `own_key`, `own_gateway`, `server` or `none`. */
  mode: string;
  /** Whether the secret item resolves; never the value. */
  present?: boolean | null;
  /** Set while the provider rejects the credential: the account's runs are refused until a probe succeeds. */
  paused?: { since: string; reason: string } | null;
}

export interface KeyBody {
  id: string;
  label?: string | null;
  created: string;
  revoked?: string | null;
  /** The time of the key's last request, written at most once a minute; absent for a key not used since the server began to record it. */
  last_used?: string | null;
}

/** An account as `accounts/v1` lists it, and as the owner's `overview/v1` carries it: its standing without the server-wide `halted` and `presets`. */
export interface AccountBody extends Omit<AccountStanding, 'halted' | 'presets'> {
  credential: CredentialBody;
  keys: KeyBody[];
  /** Automated starts (intake, schedules) are charged to this account (ADR 0076). */
  automation?: boolean;
  /** Its automated starts are held while the forecast passes the cap. */
  pause_on_forecast?: boolean;
}
