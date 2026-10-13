import { fireEvent, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ApiError, Unauthorized } from './api/client';
import { getKey, setKey } from './api/key';
import { createQueryClient } from './router';
import { SCREENS } from './shell/screens';
import { LOGIN, me, run } from './testing/fixtures';
import { layout } from './testing/layout';
import { renderConsole } from './testing/render';
import { serveApi } from './testing/server';

// The runs table is virtual: happy-dom has no layout, so give it one.
beforeEach(() => layout());

const runs = [
  run('r-1', {
    status: 'completed',
    kind: 'single',
    usd: 0.5,
    tokens: 1234,
    preset: 'draft',
    repository: '/srv/app',
    commit: undefined,
  }),
  run('r-2', {
    status: 'failed',
    kind: 'single',
    error_code: 'cost_cap_reached',
    usd: null,
    tokens: 1234,
    preset: 'draft',
    repository: '/srv/app',
    commit: undefined,
  }),
];

describe('signing in', () => {
  it('asks for a key first, and shows the screen after a key the server accepts', async () => {
    const { calls } = serveApi({ runs });
    renderConsole('/console/runs', { key: null });

    const field = await screen.findByLabelText('API key');
    expect(calls).toHaveLength(0);
    fireEvent.change(field, { target: { value: ` ${LOGIN} ` } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const table = await screen.findByRole('table', { name: 'Runs' });
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(3); // header and two runs
    expect(
      within(rows[1] as HTMLElement)
        .getByRole('link', { name: 'r-1' })
        .getAttribute('href'),
    ).toBe('/console/runs/r-1');
    expect(within(rows[2] as HTMLElement).getByText('cost_cap_reached')).toBeTruthy();
    expect(getKey()).toBe(LOGIN);
    expect(sessionStorage.getItem('chargehand.key')).toBe(LOGIN);
    const sent = calls.map((c) => c.headers.Authorization);
    expect(sent.length).toBeGreaterThan(0);
    expect(sent.every((auth) => auth === `Bearer ${LOGIN}`)).toBe(true);
  });

  it('stores nothing for a key the server refuses', async () => {
    serveApi();
    renderConsole('/console/', { key: null });

    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect((await screen.findByRole('alert')).textContent).toBe('The server rejected that key.');
    expect(getKey()).toBeNull();
    expect(sessionStorage.length).toBe(0);
  });

  it('goes back to the sign-in screen on sign-out from the account menu, and when a call is refused later', async () => {
    serveApi();
    renderConsole('/console/runs');
    fireEvent.keyDown(await screen.findByRole('button', { name: 'Account' }), { key: 'Enter' });
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Sign out' }));
    expect(await screen.findByLabelText('API key')).toBeTruthy();
    expect(getKey()).toBeNull();
  });

  it('drops the key and says why when the server refuses it mid-session', async () => {
    serveApi({ key: 'another-key' });
    setKey(LOGIN);
    renderConsole('/console/runs');
    expect((await screen.findByRole('alert')).textContent).toBe(
      'The server rejected the key. Sign in again.',
    );
  });
});

describe('the screens', () => {
  it.each([...SCREENS.map((s) => [s.label, s.to] as const), ['r-1', '/runs/r-1'] as const])(
    'opens %s at %s with its own heading',
    async (label, to) => {
      serveApi({ runs });
      renderConsole(`/console${to === '/' ? '/' : to}`);
      expect((await screen.findByRole('heading', { level: 1 })).textContent).toContain(label);
    },
  );

  it('marks the open screen in the navigation, and a run as part of Runs', async () => {
    serveApi({ runs });
    renderConsole('/console/analytics');
    await screen.findByRole('heading', { level: 1, name: 'Analytics' });
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(within(nav).getByRole('link', { name: 'Analytics' }).getAttribute('aria-current')).toBe(
      'page',
    );
    expect(
      within(nav).getByRole('link', { name: 'Overview' }).getAttribute('aria-current'),
    ).toBeNull();
  });

  it('keeps the owner-only screens out of an account’s navigation', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/runs');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    await within(nav).findByRole('link', { name: 'Repositories' });
    expect(within(nav).queryByRole('link', { name: 'System' })).toBeNull();
    expect(within(nav).queryByRole('link', { name: 'Accounts and keys' })).toBeNull();
    expect(within(nav).queryByRole('link', { name: 'Intake and automation' })).toBeNull();
  });

  it('shows only http and https pull request links', async () => {
    serveApi({
      runs: [
        run('r-1', { pr_url: 'https://example.com/o/r/pull/7' }),
        run('r-2', { pr_url: 'javascript:alert(1)' }),
      ],
    });
    renderConsole('/console/runs');
    await screen.findByRole('table', { name: 'Runs' });
    const links = (await screen.findAllByRole('link', { name: '#7' })).map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toEqual(['https://example.com/o/r/pull/7']);
    expect(screen.queryByRole('link', { name: 'PR' })).toBeNull();
  });

  it('says what the server said when a list fails, and offers a retry', async () => {
    serveApi({ replies: { '/v1/runs': [400, { errors: ['limit must be 1 to 500'] }] } });
    renderConsole('/console/runs');
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('limit must be 1 to 500');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('keeps the unknown route out of the app: a missing screen says so', async () => {
    serveApi();
    renderConsole('/console/nowhere');
    expect((await screen.findByRole('heading', { level: 1 })).textContent).toBe('Not found');
  });
});

describe('the query client', () => {
  it('does not retry what asking again cannot fix, and retries a server error twice', () => {
    const retry = createQueryClient().getDefaultOptions().queries?.retry as (
      n: number,
      e: Error,
    ) => boolean;
    expect(retry(0, new Unauthorized())).toBe(false);
    expect(retry(0, new ApiError(400, ['bad']))).toBe(false);
    expect(retry(0, new ApiError(404, ['gone']))).toBe(false);
    expect(retry(0, new ApiError(503, ['down']))).toBe(true);
    expect(retry(1, new ApiError(503, ['down']))).toBe(true);
    expect(retry(2, new ApiError(503, ['down']))).toBe(false);
  });
});
