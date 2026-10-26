import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { costBody, finishedBatch, finishedRuns } from '../../testing/learning';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';

const batches = () => {
  const runs = finishedRuns(Date.now());
  return {
    runs,
    batches: Object.fromEntries(runs.map((r) => [r.run_id, finishedBatch(r.run_id, Date.now())])),
  };
};

const at = <T,>(items: T[], i: number): T => {
  const item = items[i];
  if (item === undefined) throw new Error(`no item ${i}`);
  return item;
};

describe('Dollars per merged pull request', () => {
  it('shows spend, merged pull requests and dollars per merged one per repository and preset, for 7 and 30 days', async () => {
    const cost = costBody();
    const month = at(cost.windows, 1);
    month.total = { ...month.total, usd: 40, usd_per_merged: 40 };
    serveApi({ cost, ...batches() });
    renderConsole('/console/analytics');

    const table = await screen.findByRole('table', { name: /last 7 days/ });
    const rows = within(table).getAllByRole('row');
    expect(rows.map((r) => r.textContent)).toEqual([
      expect.stringContaining('$ / merged'),
      expect.stringContaining('All'),
      expect.stringContaining('acme/appdriven'),
      expect.stringContaining('acme/appdefault'),
    ]);
    expect(
      within(at(rows, 2))
        .getAllByRole('cell')
        .map((c) => c.textContent),
    ).toEqual(['acme/app', 'driven', '4', '$10.00', '3', '1', '1', '$10.00', '$3.00']);
    // A preset that opened nothing has no dollars per merged pull request: a dash, not $0.
    expect(at(within(at(rows, 3)).getAllByRole('cell'), 7).textContent).toBe('—');

    fireEvent.click(screen.getByRole('button', { name: '30 days' }));
    const wide = await screen.findByRole('table', { name: /last 30 days/ });
    expect(at(within(wide).getAllByRole('row'), 1).textContent).toContain('$40.00');
  });

  it('says so in the panel when the route fails, and leaves the rest of the screen', async () => {
    serveApi({
      replies: { '/v1/cost': [500, { error: { message: 'forge down' } }] },
      ...batches(),
    });
    renderConsole('/console/analytics');
    expect(await screen.findByText('Cannot read cost per merged pull request')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1, name: 'Analytics' })).toBeTruthy();
  });
});
