// The merge-rate trend and the autonomy lines of the Learning screen. Pure.

import type { AutonomyEntry, LearnReport } from '../../api/types';
import { DASH, formatPercent } from '../../lib/format';

/** `GET /v1/learn?since=` samples, oldest first: each report holds everything since that many days ago, so a week is one sample minus the next. */
export const TREND_SINCE = ['28d', '21d', '14d', '7d'] as const;

export interface TrendPoint {
  /** Days ago the week started. */
  from: number;
  /** Pull requests merged or closed that week. */
  resolved: number;
  merged: number;
  /** Share merged unchanged; null when none was resolved. */
  rate: number | null;
}

const totals = (report: LearnReport | undefined) => {
  let merged = 0;
  let resolved = 0;
  for (const g of report?.groups ?? []) {
    merged += g.prs.merged_unchanged;
    resolved += g.prs.merged_unchanged + g.prs.merged_edited + g.prs.merged + g.prs.closed;
  }
  return { merged, resolved };
};

/** One point per week: the merge rate of the pull requests resolved in it. Null until every sample has arrived. */
export function weeklyTrend(reports: ReadonlyArray<LearnReport | undefined>): TrendPoint[] | null {
  if (reports.length !== TREND_SINCE.length || reports.some((r) => r === undefined)) return null;
  return reports.map((report, i) => {
    const here = totals(report);
    const next = i + 1 < reports.length ? totals(reports[i + 1]) : { merged: 0, resolved: 0 };
    const resolved = Math.max(0, here.resolved - next.resolved);
    const merged = Math.max(0, here.merged - next.merged);
    return {
      from: 28 - 7 * i,
      resolved,
      merged,
      rate: resolved === 0 ? null : merged / resolved,
    };
  });
}

export interface AutonomyRow {
  repository: string;
  /** The grant in words. */
  grant: string;
  earned: boolean;
  streak: number;
  /** Why it is what it is: what shrank it, or what would widen it. */
  reason: string;
  /** The merge rate over the report's range, or a dash. */
  rate: string;
}

const LOST: Record<string, string> = {
  revert: 'a pull request was reverted',
  failed_check: 'a task ended with its checks failing',
};

/** A row per repository: its grant, the rate it is computed from (the repository's groups of the report) and the reason. */
export function autonomyRows(
  entries: AutonomyEntry[],
  report: LearnReport | undefined,
): AutonomyRow[] {
  return entries.map((e) => {
    const groups = (report?.groups ?? []).filter((g) => g.repository === e.repository);
    const merged = groups.reduce((n, g) => n + g.prs.merged_unchanged, 0);
    const resolved = groups.reduce(
      (n, g) => n + g.prs.merged_unchanged + g.prs.merged_edited + g.prs.merged + g.prs.closed,
      0,
    );
    const lost = e.lost_to ? (LOST[e.lost_to] ?? e.lost_to) : null;
    const reason = lost
      ? `Streak reset: ${lost}.${e.widens ? ` ${e.widens}` : ''}`
      : (e.widens ?? 'Earned with a clean streak; a revert or failed check takes it back.');
    return {
      repository: e.repository,
      grant: `Size ${e.max_size_class}, ${e.auto_merge ? 'auto-merge' : 'no auto-merge'}`,
      earned: e.auto_merge,
      streak: e.clean_streak,
      reason,
      rate: resolved === 0 ? DASH : formatPercent((merged / resolved) * 100),
    };
  });
}
