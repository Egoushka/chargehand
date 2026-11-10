import { screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AutonomyEntry, LearnGroup, LearnReport } from '../../api/types';
import { finishedBatch, finishedRuns } from '../../testing/learning';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { autonomyRows, weeklyTrend } from './autonomy';

const group = (repository: string, merged: number, resolved: number): LearnGroup => ({
  repository,
  size_class: 'M',
  tasks: resolved,
  spend_known: resolved,
  spend_median_usd: 1,
  spend_p80_usd: 1.2,
  completed: resolved,
  partial: 0,
  completion_rate: 1,
  partial_rate: 0,
  checkpointed: 0,
  checkpoint_rate: 0,
  checkpointed_completed: 0,
  checkpointed_completed_rate: null,
  prs: {
    total: resolved,
    merged_unchanged: merged,
    merged_edited: 0,
    merged: 0,
    closed: resolved - merged,
    open: 0,
  },
  merged_unchanged_rate: resolved === 0 ? null : merged / resolved,
});

const earned: AutonomyEntry = {
  repository: 'acme/ledger',
  max_size_class: 'L',
  auto_merge: true,
  clean_streak: 12,
  lost_to: null,
  widens: null,
  narrows: 'A revert or a failed check resets the streak.',
};
const lost: AutonomyEntry = {
  repository: 'acme/storefront',
  max_size_class: 'M',
  auto_merge: false,
  clean_streak: 0,
  lost_to: 'revert',
  widens: '5 more pull requests merged unchanged earn size L; 10 earn auto-merge.',
  narrows: 'A revert or a failed check resets the streak.',
};

/** The storefront resolved two closed pull requests in the last week. Weeks, oldest first, as [merged unchanged, resolved]: a report since N days holds the weeks that started within N days. */
const WEEKS: Array<[number, number]> = [
  [4, 5],
  [3, 3],
  [2, 4],
  [1, 1],
];
const reportSince = (since: string | undefined): LearnReport => {
  const days = Number.parseInt(since ?? '30', 10);
  const kept = WEEKS.filter((_, i) => 28 - 7 * i <= days);
  const merged = kept.reduce((n, w) => n + w[0], 0);
  const resolved = kept.reduce((n, w) => n + w[1], 0);
  return {
    since: null,
    tasks: resolved,
    groups: [group('acme/ledger', merged, resolved), group('acme/storefront', 0, 2)],
    autonomy: [earned, lost],
  };
};

const batches = () => {
  const runs = finishedRuns(Date.now());
  return {
    runs,
    batches: Object.fromEntries(runs.map((r) => [r.run_id, finishedBatch(r.run_id, Date.now())])),
  };
};

describe('autonomy beside the merge rate', () => {
  it('turns cumulative samples into one rate per week', () => {
    const reports = [28, 21, 14, 7].map((d) => reportSince(`${d}d`));
    const points = weeklyTrend(reports);
    expect(points?.map((p) => p.resolved)).toEqual([5, 3, 4, 3]);
    expect(points?.map((p) => p.rate)).toEqual([0.8, 1, 0.5, 1 / 3]);
    expect(weeklyTrend([undefined, ...reports.slice(1)])).toBeNull();
  });

  it('says a repository earned auto-merge and one lost it after a revert, with the reason', () => {
    const rows = autonomyRows([earned, lost], reportSince('30d'));
    expect(rows[0]).toMatchObject({ grant: 'Size L, auto-merge', earned: true, streak: 12 });
    expect(rows[1]?.grant).toBe('Size M, no auto-merge');
    expect(rows[1]?.reason).toContain('a pull request was reverted');
    expect(rows[0]?.rate).toBe('77%');
  });

  it('shows both repositories on the Learning screen and a point per week', async () => {
    serveApi({ learnFor: reportSince, ...batches() });
    renderConsole('/console/learning');
    const table = await screen.findByRole('table', { name: 'Earned autonomy by repository' });
    const rows = within(table).getAllByRole('row');
    expect(within(rows[1] as HTMLElement).getByText('Size L, auto-merge')).toBeTruthy();
    expect(within(rows[2] as HTMLElement).getByText('Size M, no auto-merge')).toBeTruthy();
    expect(within(rows[2] as HTMLElement).getByText(/a pull request was reverted/)).toBeTruthy();
    const list = await screen.findByRole('list', { name: 'Merge rate per week' });
    await waitFor(() => expect(within(list).getAllByRole('listitem')).toHaveLength(4));
    expect(within(list).getAllByRole('listitem')[0]?.textContent).toContain('80%');
  });
});
