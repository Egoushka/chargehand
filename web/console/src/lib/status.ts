import type { RunState } from '../api/types';

/** The design system's fifteen states: one word each, always a glyph and a word, never colour alone. */
export type Status =
  | 'running'
  | 'waiting'
  | 'queued'
  | 'partial'
  | 'rate_limited'
  | 'failed'
  | 'cancelled'
  | 'merged'
  | 'asked'
  | 'off'
  | 'done'
  | 'question'
  | 'lost'
  | 'halted'
  | 'not_started';

export type Tone = 'accent' | 'ok' | 'warn' | 'danger' | 'info' | 'neutral';

export const STATUS: Record<Status, { label: string; tone: Tone; note: string }> = {
  running: { label: 'Running', tone: 'info', note: 'A session is working on the task now.' },
  waiting: {
    label: 'Waiting',
    tone: 'neutral',
    note: 'Held back: a dependency has not finished, or the host has no free memory or load.',
  },
  queued: { label: 'Queued', tone: 'neutral', note: 'Accepted, not started.' },
  partial: {
    label: 'Partial',
    tone: 'warn',
    note: 'Stopped with work kept; a [partial] draft PR holds it.',
  },
  rate_limited: {
    label: 'Rate limited',
    tone: 'warn',
    note: 'The model hit a usage limit. New starts are paused until the reset; retryable.',
  },
  failed: {
    label: 'Failed',
    tone: 'danger',
    note: 'Ended without a finished result. The error code says why.',
  },
  cancelled: {
    label: 'Cancelled',
    tone: 'neutral',
    note: 'Stopped by a person or by halt. Nothing was pushed.',
  },
  merged: {
    label: 'Merged',
    tone: 'ok',
    note: 'The integrator merged the pull request through its required checks.',
  },
  asked: {
    label: 'Asked',
    tone: 'accent',
    note: 'Policy is ask: the PR is ready and waits for you to merge it.',
  },
  off: {
    label: 'Off',
    tone: 'neutral',
    note: 'No integrator policy for this repository: the draft is left alone.',
  },
  done: {
    label: 'Done',
    tone: 'ok',
    note: 'The task finished and opened a draft pull request.',
  },
  question: {
    label: 'Question',
    tone: 'accent',
    note: 'The session needs an answer from you (needs_input).',
  },
  lost: {
    label: 'Lost',
    tone: 'danger',
    note: 'The process that ran it ended before it finished.',
  },
  halted: {
    label: 'Halted',
    tone: 'danger',
    note: 'The server is halted: nothing starts until you resume.',
  },
  not_started: {
    label: 'Not started',
    tone: 'neutral',
    note: 'A dependency failed, asked or was cancelled, so this never began.',
  },
};

/** A run's contract state as the design's word. A denied run reads `failed`; its error code says why. */
export function runStatus(state: RunState): Status {
  switch (state) {
    case 'queued':
      return 'queued';
    case 'running':
      return 'running';
    case 'lost':
      return 'lost';
    case 'completed':
      return 'done';
    case 'needs_input':
      return 'question';
    default:
      return 'failed';
  }
}

/** A batch task's `state` (batch/v1). */
export function taskStatus(state: string): Status {
  switch (state) {
    case 'pending':
      return 'queued';
    case 'running':
      return 'running';
    case 'completed':
      return 'done';
    case 'needs_input':
      return 'question';
    case 'not_started':
      return 'not_started';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'failed';
  }
}

export interface InboxLike {
  kind: 'question' | 'pull_request' | 'failure';
  reason?: string | null;
  error?: { code: string } | null;
}

/** The word for one inbox item: a question, a pull request that waits (`partial` for a kept-work draft), or a failure. */
export function inboxStatus(item: InboxLike): Status {
  if (item.kind === 'question') return 'question';
  if (item.kind === 'pull_request') return item.reason === 'partial_draft' ? 'partial' : 'asked';
  if (item.reason === 'lost') return 'lost';
  if (item.reason === 'partial') return 'partial';
  if (item.reason === 'rate_limited' || item.error?.code === 'rate_limited') return 'rate_limited';
  return 'failed';
}
