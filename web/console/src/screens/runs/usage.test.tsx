import { screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SubscriptionUsage } from '../../api/types';
import { run } from '../../testing/fixtures';
import { layout } from '../../testing/layout';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { latestUsage, usageLabel } from './model';

const NOW = Date.parse('2026-10-13T12:00:00Z');

const week: SubscriptionUsage = {
  five_hour: { utilization: 0.12 },
  seven_day: { utilization: 0.42, resets_at: '2026-10-16T09:00:00Z' },
  used: 0.42,
  window: 'seven_day',
};

beforeEach(() => layout());

describe('subscription usage on a run', () => {
  it('words the larger window as a percentage with its name', () => {
    expect(usageLabel(week)).toBe('42% 7d');
    expect(usageLabel({ five_hour: { utilization: 0.5 }, used: 0.5, window: 'five_hour' })).toBe(
      '50% 5h',
    );
  });

  it('shows a window past its cap as over 100%, and a run with no reading as nothing', () => {
    expect(usageLabel({ five_hour: { utilization: 1.2 }, used: 1.2, window: 'five_hour' })).toBe(
      '120% 5h',
    );
    expect(usageLabel(undefined)).toBeNull();
    expect(usageLabel(null)).toBeNull();
  });

  it('takes the latest finished run that carries a reading', () => {
    const runs = [
      run('old', { subscription_usage: week, finished_at: '2026-10-13T08:00:00Z' }, NOW),
      run('none', { finished_at: '2026-10-13T11:00:00Z' }, NOW),
      run(
        'new',
        {
          subscription_usage: { ...week, used: 0.5, window: 'five_hour' },
          finished_at: '2026-10-13T10:00:00Z',
        },
        NOW,
      ),
    ];
    expect(latestUsage(runs)?.run_id).toBe('new');
    expect(latestUsage([run('x', {}, NOW)])).toBeNull();
  });
});

describe('the Runs table', () => {
  it('shows each run’s window, and a dash for a run with no reading', async () => {
    serveApi({
      runs: [
        run('a-sub', { subscription_usage: week, credential: 'subscription' }),
        run('b-key', { credential: 'api_key' }),
      ],
    });
    renderConsole('/console/runs');
    await screen.findByRole('table', { name: 'Runs' });
    const rows = () =>
      within(screen.getByRole('table', { name: 'Runs' }))
        .getAllByRole('row')
        .slice(1);
    await waitFor(() => expect(rows().length).toBe(2));

    expect(screen.getByRole('columnheader', { name: 'Window' })).toBeTruthy();
    expect(within(rows()[0] as HTMLElement).getByText('42% 7d')).toBeTruthy();
    expect(within(rows()[1] as HTMLElement).queryByText(/% (5h|7d)/)).toBeNull();
  });
});

describe('the budget router panel', () => {
  it('shows how far into its windows the subscription is, from the latest run that read them', async () => {
    serveApi({
      runs: [run('a-sub', { subscription_usage: week, finished_at: '2026-10-13T11:00:00Z' }, NOW)],
    });
    renderConsole('/console/analytics');

    const line = await screen.findByText(/Subscription windows/);
    expect(line.textContent).toContain('5h 12%');
    expect(line.textContent).toContain('7d 42%');
  });

  it('says no run has read the windows when none did', async () => {
    serveApi({ runs: [run('x', {}, NOW)] });
    renderConsole('/console/analytics');

    expect(await screen.findByText(/No run has read a subscription window/)).toBeTruthy();
  });
});
