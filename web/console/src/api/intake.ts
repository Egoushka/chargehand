import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';
import type { ContractMeta } from './common';

export interface IntakeRepositoryBody {
  repository: string;
  path: string;
}

export interface IntakeCaps {
  max_concurrent: number;
  max_review_rounds: number;
  max_tokens_total: number;
  max_usd_total: number;
  /** `S`, `M` or `L`: the size class of a task whose issue has no size label. */
  budget_class: string;
}

/**
 * A trigger that waits to start, oldest first: `max_concurrent` (no free slot), `admission` (the
 * server or `automation.account` refused the start), `checkout`, or `lost` (a restart lost the issue's run,
 * which starts again once; `detail` is the lost run). Otherwise `detail` is the refusal.
 */
export interface IntakeWaitingBody {
  trigger: string;
  repository: string;
  issue: number;
  since: string;
  reason: string;
  detail?: string | null;
}

export interface IntakeTaskBody {
  repository: string;
  issue: number;
  batch_run_id: string;
  task_id: string;
  started: string;
  size: string;
  phase: 'running' | 'asked' | 'done';
  pr_url?: string | null;
  branch?: string | null;
  round?: number | null;
  awaiting_answer: boolean;
}

/** A review comment waiting for a round. The comment's text is not returned. */
export interface IntakePendingBody {
  id: string;
  repository: string;
  pull: number;
  author: string;
  path?: string | null;
  line?: number | null;
  at: string;
}

/**
 * Why intake did not act, as it wrote it on the issue: `active`, `author`, `gone`, `wait`, `cap`,
 * `notask`, `busy` or `rounds`; `other` for a key that does not split.
 */
export interface IntakeNoticeBody {
  reason: string;
  trigger: string;
}

/** What intake did with a trigger, newest first. `reason` is set for `ignored`: `author_not_allowed`, `active`, `author`, `cap` (before 0.19.4), `gone`, `withdrawn`, `not_chargehand_branch` or `review_rounds_off`. Never the comment's text. */
export interface IntakeTriggerBody {
  at: string;
  trigger: string;
  outcome: 'started' | 'continued' | 'round_started' | 'queued' | 'ignored';
  reason?: string | null;
  /** The login that set the label or wrote the comment. */
  author?: string | null;
}

/** Triggers handled so far; a trigger is handled once. */
export interface IntakeHandledBody {
  total: number;
  labels: number;
  comments: number;
  reviews: number;
  rounds: number;
}

/**
 * `intake/v1`: GitHub intake's settings and what it remembers, read from the file the poller saves.
 * Owner only. `enabled` is false when the profile has no `automation.github`.
 */
export interface IntakeBody extends ContractMeta {
  contract_version: 'intake/v1';
  enabled: boolean;
  state_present: boolean;
  state_saved_at?: string | null;
  watched: IntakeRepositoryBody[];
  allowed_authors: string[];
  label?: string | null;
  mention?: string | null;
  poll_seconds?: number | null;
  caps?: IntakeCaps | null;
  waiting?: IntakeWaitingBody[] | null;
  /** The intake governor's subscription check (ADR 0084): whether `subscription_block` is on, and when a reported rate-limit pause ends. */
  subscription?: { block: boolean; resets_at: string | null } | null;
  running: number;
  tasks: IntakeTaskBody[];
  pending_reviews: IntakePendingBody[];
  handled: IntakeHandledBody;
  notices: IntakeNoticeBody[];
  comment_cursors: Record<string, string>;
  /** The latest 100 triggers and what came of them. */
  triggers?: IntakeTriggerBody[];
}

export const intakeQuery = () =>
  queryOptions({
    queryKey: ['intake'],
    queryFn: ({ signal }) => getJson<IntakeBody>('/v1/intake', signal),
  });

export const useIntake = () => useQuery(intakeQuery());

/** A batch intake started for a source, newest first. `item` is `#12` or `#12 round 2`. */
export interface IntakeSourceRunBody {
  batch_run_id: string;
  task_id: string;
  item: string;
  started: string;
  phase: 'running' | 'asked' | 'done';
  pr_url?: string | null;
}

/** A repository GitHub intake watches, with its switch and its queue. */
export interface IntakeSourceBody {
  repository: string;
  enabled: boolean;
  /** `listed` in the profile, or `discovered` (the App can see it; its checkout is a managed clone). */
  origin: 'listed' | 'discovered';
  path: string;
  running?: number;
  /** Review comments waiting for a round, and issues or answers told to wait for a free slot. */
  queued?: number;
  recent?: IntakeSourceRunBody[] | null;
}

/**
 * `intake-sources/v1`: the on/off switch of every source intake watches (CHARGEHAND-241, 242). Owner only.
 */
export interface IntakeSourcesBody {
  contract_version: 'intake-sources/v1';
  configured: boolean;
  discover: boolean;
  sources: IntakeSourceBody[];
  not_recorded?: string[] | null;
  /** The owner's review policy (ADR 0085); absent from an older server. */
  reviews?: ReviewPolicy | null;
}

/** Repositories left out of reviews, and the budget of one review when it is above the preset's. */
export interface ReviewPolicy {
  excluded: string[];
  budget_usd: number | null;
}

export const intakeSourcesQuery = () =>
  queryOptions({
    queryKey: ['intake-sources'],
    queryFn: ({ signal }) => getJson<IntakeSourcesBody>('/v1/intake/sources', signal),
  });

/** One switch: a GitHub repository. */
export interface SourceSwitch {
  id: string;
  enabled: boolean;
}

/** `PUT /v1/intake/sources`; answers the whole list. */
export async function setIntakeSource(change: SourceSwitch): Promise<IntakeSourcesBody> {
  const entry = { enabled: change.enabled };
  const reply = await call<IntakeSourcesBody>('/v1/intake/sources', {
    method: 'PUT',
    body: { sources: [{ repository: change.id, ...entry }] },
  });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}

/** Sets a switch; the answer replaces the cached list. */
export function useSetIntakeSource() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: setIntakeSource,
    onSuccess: (body) => queryClient.setQueryData(intakeSourcesQuery().queryKey, body),
  });
}

/** A policy change: put a repository back into reviews, or clear the review budget. */
export type PolicyChange = { kind: 'include'; repository: string } | { kind: 'budget'; usd: null };

/** `POST /v1/intake/reviews/include` or `/budget`; the policy is read again afterwards. */
export function useReviewPolicy() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (change: PolicyChange) => {
      const reply =
        change.kind === 'include'
          ? await call('/v1/intake/reviews/include', {
              method: 'POST',
              body: { repository: change.repository },
            })
          : await call('/v1/intake/reviews/budget', { method: 'POST', body: { usd: change.usd } });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: intakeSourcesQuery().queryKey }),
  });
}
