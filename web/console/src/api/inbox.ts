import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';
import type { AnswerHint, ContractMeta, ErrorBody, PullRequestBody } from './common';

export interface InboxCounts {
  questions: number;
  pull_requests: number;
  failures: number;
  total: number;
  /** Open items in the Decide lane, what the badge counts (ADR 0085). Absent from a server before it. */
  decide?: number;
}

/** The request an option sends: always a route of the API. */
export interface InboxCall {
  method: string;
  path: string;
  body?: unknown;
}

/**
 * A choice a card offers (ADR 0085), with its effect: `retry`, `take_over` (a `command` to copy), `answer` (the item's
 * `answer` box), `open` (a `url`), `dismiss`, and `undo` on a handled item.
 */
export interface InboxOption {
  id: string;
  label: string;
  effect: string;
  estimate_usd?: number | null;
  call?: InboxCall | null;
  command?: string | null;
  url?: string | null;
}

/** `decide`: a person's choice; `fix`: a failure; `handled`: a rule or a person resolved it. */
export type InboxLane = 'decide' | 'fix' | 'handled';

export type InboxKind = 'question' | 'pull_request' | 'failure';

/**
 * One thing that needs a person. `question`: a task that ended `needs_input` and no batch has continued; `answer` is the request to send.
 * `pull_request`: open and waiting (`reason` is `policy_ask`, `test_integrity` or `partial_draft`). `failure`: a failed task, run or batch
 * (`reason` is the error code, or `partial` or `lost`; `subject` is `task`, `run` or `batch`).
 */
export interface InboxItem {
  kind: InboxKind;
  id: string;
  at: string;
  actor?: string | null;
  batch_run_id?: string | null;
  task_id?: string | null;
  run_id?: string | null;
  repository?: string | null;
  title: string;
  questions: string[];
  answer?: AnswerHint | null;
  pull_request?: PullRequestBody | null;
  error?: ErrorBody | null;
  reason?: string | null;
  subject?: 'task' | 'run' | 'batch' | null;
  /** How many identical failures this item stands for (ADR 0064); it is the newest of them. Absent from a server before it: 1. */
  count?: number;
  /** What happened, in one sentence; absent from a server before ADR 0085, as are `why`, `options` and `lane`. */
  what?: string | null;
  /** The cause in plain words. */
  why?: string | null;
  options?: InboxOption[] | null;
  lane?: InboxLane | null;
  /** For a handled item: the rule that resolved it, or `dismissed`. */
  resolved_by?: string | null;
}

/** What left the inbox in the window without being dealt with here (ADR 0064). */
export interface InboxResolved {
  /** A later task or run of the same account replaced it. */
  superseded: number;
  /** chargehand's own failure, and a later run on the repository finished ok. */
  infrastructure: number;
  /** A question a batch answered or retried. */
  continued: number;
  dismissed: number;
  /** Failures folded into an identical newer one. */
  duplicates: number;
  /** A failure about a pull request that was merged or closed since. Absent from a server before CHARGEHAND-237. */
  pr_closed?: number;
}

/**
 * Open failures with the same repository and code (CHARGEHAND-237), two or more. Each item stays in `items`; `id` dismisses or
 * restores all of them, and `items` lists their ids newest first (with a `limit`, some may not be in the body's `items`).
 */
export interface InboxGroup {
  id: string;
  kind: 'failure';
  repository?: string | null;
  reason?: string | null;
  /** The failures it stands for: the sum of its items' `count`. */
  count: number;
  at: string;
  items: string[];
}

/** `inbox/v1`: what needs a person, newest first. */
export interface InboxBody extends ContractMeta {
  contract_version: 'inbox/v1';
  since: string;
  counts: InboxCounts;
  items: InboxItem[];
  /** Absent from a server before ADR 0064. */
  resolved?: InboxResolved;
  /** Absent from a server before CHARGEHAND-237. */
  groups?: InboxGroup[];
  /** What a rule resolved or a person dismissed in the window, newest first, each with Undo. Absent from a server before ADR 0085. */
  handled?: InboxItem[];
}

export const inboxQuery = (limit = 100) =>
  queryOptions({
    queryKey: ['inbox', limit],
    queryFn: ({ signal }) => getJson<InboxBody>(`/v1/inbox?limit=${limit}`, signal),
    refetchInterval: 10_000,
  });

export const useInbox = (limit = 100) => useQuery(inboxQuery(limit));

/** What `POST /v1/inbox/dismiss` and `/restore` answer: `ids` changed, `unchanged` were so already, `unknown` are not in the caller's inbox. */
export interface DismissResult {
  action: 'dismiss' | 'restore';
  changed: boolean;
  ids: string[];
  unchanged: string[];
  unknown: string[];
}

/** `POST /v1/inbox/dismiss`, or `/v1/inbox/restore` to undo it. */
export async function dismissItems(ids: string[], restore = false): Promise<DismissResult> {
  const reply = await call<DismissResult>(restore ? '/v1/inbox/restore' : '/v1/inbox/dismiss', {
    method: 'POST',
    body: { ids },
  });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}

/** Sends an option's call. Only a route of the API: a call that names anything else is refused here, whatever the server said. */
export async function sendOption(option: InboxOption): Promise<Record<string, unknown>> {
  const target = option.call;
  if (
    !target ||
    target.method !== 'POST' ||
    !target.path.startsWith('/v1/') ||
    target.path.includes('..')
  )
    throw new ApiError(400, ['That option has no request to send.']);
  const reply = await call<Record<string, unknown>>(target.path, {
    method: 'POST',
    body: target.body ?? undefined,
  });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}

/** Sends an option and refreshes the inbox and the overview. */
export function useOption() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (option: InboxOption) => sendOption(option),
    onSettled: () =>
      Promise.all(
        ['inbox', 'overview', 'runs'].map((key) =>
          queryClient.invalidateQueries({ queryKey: [key] }),
        ),
      ),
  });
}

/** Dismisses inbox items (ADR 0064), or undoes it with `restore`. The ids are the inbox's, sent in the body: a pull request's holds its URL. */
export function useDismiss() {
  const queryClient = useQueryClient();
  const refresh = () =>
    Promise.all(
      ['inbox', 'overview'].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
    );
  const mutation = useMutation({
    mutationFn: ({ ids, restore = false }: { ids: string[]; restore?: boolean }) =>
      dismissItems(ids, restore),
    onSettled: refresh,
  });
  /** Undo from a notice: the card that dismissed is gone by then, so this does not go through the mutation. */
  const restore = (ids: string[]) => dismissItems(ids, true).finally(refresh);
  return { ...mutation, restore };
}
