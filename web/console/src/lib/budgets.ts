import type { BatchBody, BudgetSourceBody } from '../api/batch';
import { formatCount, formatUsd } from './format';

export type SizeClass = 'S' | 'M' | 'L';
export const SIZES: readonly SizeClass[] = ['S', 'M', 'L'];

/** The budget a task of a size class was last held to, and where it came from (ADR 0045). */
export interface BudgetInForce {
  size: SizeClass;
  usd: number;
  source: BudgetSourceBody;
  /** The batch run and task the figure is read from. */
  runId: string;
  taskId: string;
  /** When that task ended, else started, else its batch started. */
  at: string;
}

/** "p80 of 23 past tasks, this repository", "flat budget of the size class", "set by the request". */
export function describeBudgetSource(source: BudgetSourceBody): string {
  switch (source.kind) {
    case 'history': {
      const rank = source.percentile != null ? `p${source.percentile}` : 'a percentile';
      const n = source.n != null ? ` of ${formatCount(source.n)} past tasks` : '';
      const scope =
        source.scope === 'repository'
          ? ', this repository'
          : source.scope === 'all'
            ? ', all repositories'
            : '';
      return `${rank}${n}${scope}`;
    }
    case 'task':
      return 'set by the request';
    case 'flat':
      return 'flat budget of the size class';
    default:
      return 'no governor';
  }
}

/** `$4.00 · p80 of 23 past tasks, this repository`. */
export function describeBudget(budget: Pick<BudgetInForce, 'usd' | 'source'>): string {
  return `${formatUsd(budget.usd, true)} · ${describeBudgetSource(budget.source)}`;
}

const isSize = (value: unknown): value is SizeClass =>
  value === 'S' || value === 'M' || value === 'L';

/**
 * The newest budget per size class among the tasks of these batches. A task that had no governor (no dollar figure) says nothing
 * about a budget and is left out. A size class no task shows is absent from the result.
 */
export function budgetsInForce(batches: readonly BatchBody[]): BudgetInForce[] {
  const newest = new Map<SizeClass, BudgetInForce>();
  for (const batch of batches) {
    for (const task of batch.tasks) {
      const usd = task.budget?.usd;
      if (!isSize(task.size) || usd == null || !task.budget) continue;
      const at = task.finished_at ?? task.started_at ?? batch.run.started_at;
      const known = newest.get(task.size);
      if (known && Date.parse(known.at) >= Date.parse(at)) continue;
      newest.set(task.size, {
        size: task.size,
        usd,
        source: task.budget.source,
        runId: batch.run.run_id,
        taskId: task.id,
        at,
      });
    }
  }
  return SIZES.flatMap((size) => newest.get(size) ?? []);
}
