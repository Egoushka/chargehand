import { type Draft, fromRequest, type Issue } from './model';

/**
 * What another screen hands to Start a batch through the router's state (nothing goes in the address): a request/v1, whole or
 * the part the sender has. "Clone as new batch" on a batch sends the batch's request this way. The composer fills the form from it
 * and checks it as it checks anything typed, so a sender need not be exact: a missing commit becomes the repository's head, a
 * missing `contract_version` or `context.interactive` is supplied.
 */
export interface StartBatchState {
  request: unknown;
  /** The run it was cloned from; the composer says so above the form. */
  from?: string;
}

declare module '@tanstack/history' {
  interface HistoryState {
    startBatch?: StartBatchState;
  }
}

/**
 * The options for `navigate` that open Start a batch with a request filled in:
 * `navigate(cloneAsNewBatch(request, batch.run.run_id))`.
 */
export function cloneAsNewBatch(request: unknown, from?: string) {
  return { to: '/new' as const, state: { startBatch: { request, from } } };
}

export interface Prefill {
  draft: Draft;
  from?: string;
  notes: Issue[];
}

/** The prefill in a location's state, or null for a visit with none. The state came from a page, so its shape is not assumed. */
export function readPrefill(state: unknown): Prefill | null {
  const sent = (state as { startBatch?: unknown } | null | undefined)?.startBatch;
  if (typeof sent !== 'object' || sent === null || !('request' in sent)) return null;
  const { request, from } = sent as StartBatchState;
  return { ...fromRequest(request), ...(typeof from === 'string' && from !== '' ? { from } : {}) };
}
