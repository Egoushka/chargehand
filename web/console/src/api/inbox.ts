import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, getJson, problems } from './client';
import type { AnswerHint, ContractMeta, ErrorBody, PullRequestBody } from './common';

export interface InboxCounts {
  questions: number;
  pull_requests: number;
  failures: number;
  total: number;
}

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
}

/** `inbox/v1`: what needs a person, newest first. */
export interface InboxBody extends ContractMeta {
  contract_version: 'inbox/v1';
  since: string;
  counts: InboxCounts;
  items: InboxItem[];
  /** Absent from a server before ADR 0064. */
  resolved?: InboxResolved;
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
