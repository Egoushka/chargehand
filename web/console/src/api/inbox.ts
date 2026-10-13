import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
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
}

/** `inbox/v1`: what needs a person, newest first. */
export interface InboxBody extends ContractMeta {
  contract_version: 'inbox/v1';
  since: string;
  counts: InboxCounts;
  items: InboxItem[];
}

export const inboxQuery = (limit = 100) =>
  queryOptions({
    queryKey: ['inbox', limit],
    queryFn: ({ signal }) => getJson<InboxBody>(`/v1/inbox?limit=${limit}`, signal),
    refetchInterval: 10_000,
  });

export const useInbox = (limit = 100) => useQuery(inboxQuery(limit));
