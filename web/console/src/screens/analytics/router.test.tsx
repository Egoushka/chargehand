import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderConsole } from '../../testing/render';
import { routerBody } from '../../testing/router';
import { serveApi } from '../../testing/server';

describe('Pools and routes on the spend screen', () => {
  it('draws the pools in drain order with balance, room today and spend, and the routes with their tier', async () => {
    serveApi({ router: routerBody() });
    renderConsole('/console/analytics');

    const table = await screen.findByRole('table', { name: 'Budget router pools' });
    const rows = within(table).getAllByRole('row');
    expect(rows.map((r) => r.textContent)).toEqual([
      expect.stringContaining('Pool'),
      expect.stringContaining('free'),
      expect.stringContaining('bought'),
    ]);
    const cells = within(rows[2] as HTMLElement)
      .getAllByRole('cell')
      .map((c) => c.textContent);
    expect(cells).toEqual(['bought', 'fixed', '$23.00', '$0.77', '$1.00', '$7.00', 'room']);
    expect(screen.getByRole('table', { name: 'Budget router routes' }).textContent).toContain(
      'review',
    );
  });

  it('shows a pool that may not take a call with its reason', async () => {
    const body = routerBody();
    const pool = body.pools[1];
    if (!pool) throw new Error('fixture');
    pool.has_room = false;
    pool.why = 'exhausted';
    serveApi({ router: body });
    renderConsole('/console/analytics');

    const table = await screen.findByRole('table', { name: 'Budget router pools' });
    expect(within(table).getByText('exhausted')).toBeTruthy();
  });

  it('says the router is off, without an error, when the profile has no pools block', async () => {
    serveApi({ router: routerBody({ enabled: false, pools: [], routes: [] }) });
    renderConsole('/console/analytics');

    expect(await screen.findByText('Router off')).toBeTruthy();
    expect(screen.queryByText(/Cannot read the budget router/)).toBeNull();
  });

  it('says so in the panel when the route fails', async () => {
    serveApi({ replies: { '/v1/router': [500, { error: { message: 'down' } }] } });
    renderConsole('/console/analytics');

    expect(await screen.findByText('Cannot read the budget router')).toBeTruthy();
  });
});
