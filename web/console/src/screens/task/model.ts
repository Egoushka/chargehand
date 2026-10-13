import type { BatchTaskBody } from '../../api/batch';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import type { RunStatus } from '../../api/types';
import { formatClock } from '../../lib/format';

export interface SessionRow {
  n: number;
  role: string;
  runId: string;
  /** When the session's container came up, if the batch's events say. */
  startedAt: string | null;
  last: boolean;
}

/**
 * The sessions of the task. The server lists them as run ids with a role; only the last has a run record, and the times come from the
 * `container_started` events when the batch still holds them.
 */
export function sessionRows(task: BatchTaskBody, events: readonly RunStatus[]): SessionRow[] {
  const starts = events
    .filter((e) => e.event === 'container_started' && e.at)
    .map((e) => e.at as string)
    .sort();
  return task.sessions.map((s, i) => ({
    n: i + 1,
    role: s.role,
    runId: s.run_id,
    startedAt: starts[i] ?? null,
    last: i === task.sessions.length - 1,
  }));
}

export function sessionText(row: SessionRow): string {
  const role =
    row.role === 'continuation'
      ? 'continuation'
      : row.role === 'last'
        ? 'last session'
        : 'first session';
  return row.startedAt ? `${role}, from ${formatClock(row.startedAt)}` : role;
}

/** The turns the task had reached at each progress event: the series the sparkline draws. */
export function turnsSeries(events: readonly RunStatus[]): number[] {
  return events
    .filter((e) => e.event === 'session_progress' && typeof e.turns === 'number')
    .sort((a, b) => Date.parse(a.at ?? '') - Date.parse(b.at ?? ''))
    .map((e) => e.turns as number);
}

/** One answer for each question, none of them empty. */
export const answersReady = (answers: readonly string[], questions: readonly string[]): boolean =>
  questions.length > 0 &&
  answers.length >= questions.length &&
  questions.every((_, i) => (answers[i] ?? '').trim().length > 0);

/** The request an answer sends, for the "Show the request" disclosure. */
export function answerPreview(
  detail: BatchTaskDetailBody,
  answers: readonly string[],
): string | null {
  const task = detail.task;
  if (!task.answer) return null;
  const { continue_from: from, repository, preset } = task.answer;
  return JSON.stringify(
    {
      driven: {
        tasks: [
          {
            id: task.id,
            continue_from: from,
            answers: task.questions.map((_, i) => (answers[i] ?? '').trim() || '…'),
          },
        ],
      },
      context: { preset, ...(repository ? { repository } : {}) },
    },
    null,
    2,
  );
}

export const TERMINAL: ReadonlySet<string> = new Set([
  'completed',
  'failed',
  'cancelled',
  'not_started',
]);
