import type { BatchTaskDetailBody } from '../../api/batch-task';
import { asRecord, changeTotals, verificationFacts } from '../batch/artifacts';

/** One side of the address: `<batch>/<task>`. */
export interface TaskRef {
  batch: string;
  task: string;
}

export const refText = (ref: TaskRef): string => `${ref.batch}/${ref.task}`;

/** Reads `<batch>/<task>` from the address; anything else is no side. */
export function parseRef(value: unknown): TaskRef | null {
  if (typeof value !== 'string') return null;
  const at = value.indexOf('/');
  if (at <= 0 || at === value.length - 1 || value.indexOf('/', at + 1) >= 0) return null;
  return { batch: value.slice(0, at), task: value.slice(at + 1) };
}

export interface QualityCard {
  state: string;
  /** chargehand's own verification run: not the session's word for it. */
  verification: 'passed' | 'failed' | 'none';
  flagged: boolean;
  files: number;
  lines: number;
  added: number;
  removed: number;
  diff: string | null;
}

export function qualityCard(detail: BatchTaskDetailBody): QualityCard {
  const v = verificationFacts(detail.verification);
  const totals = changeTotals(detail.changes);
  const changes = asRecord(detail.changes);
  return {
    state: detail.task.state,
    verification: v ? (v.passed ? 'passed' : 'failed') : 'none',
    flagged: Boolean(detail.task.test_integrity_flagged || v?.flagged),
    files: totals?.files ?? 0,
    added: totals?.added ?? 0,
    removed: totals?.removed ?? 0,
    lines: (totals?.added ?? 0) + (totals?.removed ?? 0),
    diff: typeof changes?.diff === 'string' ? changes.diff : null,
  };
}

export interface Verdict {
  /** The stronger side, or null when the cards do not separate. */
  winner: 'a' | 'b' | null;
  reason: string;
}

/**
 * The stronger card, by what chargehand itself recorded, in this order: the task finished, its verification run passed, the
 * test-integrity check did not flag it, and the change is smaller. The first difference decides; the reason names it.
 */
export function verdict(a: QualityCard, b: QualityCard): Verdict {
  const done = (c: QualityCard) => (c.state === 'completed' ? 1 : 0);
  const passed = (c: QualityCard) => (c.verification === 'passed' ? 1 : 0);
  const steps: Array<[string, number, number]> = [
    ['finished', done(a), done(b)],
    ['passed verification', passed(a), passed(b)],
    ['is not flagged by the test-integrity check', a.flagged ? 0 : 1, b.flagged ? 0 : 1],
    ['has the smaller change', b.lines, a.lines],
  ];
  for (const [what, x, y] of steps) {
    if (x !== y)
      return {
        winner: x > y ? 'a' : 'b',
        reason: `Task ${x > y ? 'A' : 'B'} is stronger: it ${what}.`,
      };
  }
  return { winner: null, reason: 'The cards do not separate.' };
}
