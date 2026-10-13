import { QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { getKey, setKey } from './api/key';
import type { Me, RunSummary } from './api/types';
import { createAppRouter, createQueryClient } from './router';
import { SCREENS } from './shell/screens';

const me: Me = {
  name: 'alice',
  owner: false,
  driven: false,
  limits: { max_usd_per_day: 5 },
  spend: {
    day: { usd: 1.25, tokens: 300, resets_at: '2026-10-16T00:00:00Z' },
    month: { usd: 3.25, tokens: 400, resets_at: '2026-11-01T00:00:00Z' },
  },
  unfinished_runs: 0,
  halted: false,
  presets: ['draft'],
};

const run = (id: string, over: Partial<RunSummary> = {}): RunSummary => ({
  contract_version: 'run-summary/v1',
  run_id: id,
  preset: 'draft',
  status: 'completed',
  started_at: '2026-10-15T11:00:00Z',
  usd: 0.5,
  tokens: 1234,
  repository: '/srv/app',
  kind: 'single',
  actor: 'alice',
  ...over,
});

const reply = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

/** A server that accepts one key and answers the routes the console calls. */
function serve(validKey: string, runs: RunSummary[] = []) {
  const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
    const auth = (init?.headers as Record<string, string> | undefined)?.Authorization;
    if (auth !== `Bearer ${validKey}`) return new Response('', { status: 401 });
    if (path === '/v1/accounts/me') return reply(me);
    if (path.startsWith('/v1/runs?')) return reply({ runs });
    if (path.endsWith('/events')) return new Response('', { status: 200 });
    if (path.startsWith('/v1/runs/')) return reply({ status: 'running', run_id: 'r-1' }, 202);
    return reply({ errors: [`no route ${path}`] }, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function open(path: string) {
  const router = createAppRouter({
    basepath: '/console',
    history: createMemoryHistory({ initialEntries: [path] }),
  });
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

beforeEach(() => vi.spyOn(console, 'error').mockImplementation(() => {}));
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('signing in', () => {
  it('asks for a key first, and shows the runs table after a key the server accepts', async () => {
    const fetchMock = serve('chk1.alice.secret', [
      run('r-1'),
      run('r-2', { status: 'failed', error_code: 'cost_cap_reached', usd: null }),
    ]);
    open('/console/runs');

    const field = await screen.findByLabelText('API key');
    expect(fetchMock).not.toHaveBeenCalled();
    fireEvent.change(field, { target: { value: ' chk1.alice.secret ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const table = await screen.findByRole('table');
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(3); // header + two runs
    expect(
      within(rows[1] as HTMLElement)
        .getByRole('link', { name: 'r-1' })
        .getAttribute('href'),
    ).toBe('/console/runs/r-1');
    expect(within(rows[2] as HTMLElement).getByText('cost_cap_reached')).toBeTruthy();
    expect(getKey()).toBe('chk1.alice.secret');
    expect(sessionStorage.getItem('chargehand.key')).toBe('chk1.alice.secret');
    expect(screen.getByText(/alice · today \$1\.25 of \$5\.00/)).toBeTruthy();
    const sent = fetchMock.mock.calls.map(
      ([, init]) => (init?.headers as Record<string, string> | undefined)?.Authorization,
    );
    expect(sent.length).toBeGreaterThan(0);
    expect(sent.every((auth) => auth === 'Bearer chk1.alice.secret')).toBe(true);
  });

  it('stores nothing for a key the server refuses', async () => {
    serve('right');
    open('/console/');

    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect((await screen.findByRole('alert')).textContent).toBe('The server rejected that key.');
    expect(getKey()).toBeNull();
    expect(sessionStorage.length).toBe(0);
  });

  it('goes back to the sign-in screen on sign-out, and when a call is refused later', async () => {
    serve('k');
    setKey('k');
    open('/console/runs');
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByLabelText('API key')).toBeTruthy();
    expect(getKey()).toBeNull();

    serve('another-key');
    setKey('k');
    open('/console/runs');
    expect((await screen.findByRole('alert')).textContent).toBe(
      'The server rejected the key. Sign in again.',
    );
  });
});

describe('the screens', () => {
  it.each([...SCREENS.map((s) => [s.label, s.to] as const), ['Run', '/runs/r-1'] as const])(
    'opens %s at %s',
    async (label, to) => {
      serve('k', [run('r-1')]);
      setKey('k');
      open(`/console${to === '/' ? '/' : to}`);

      expect((await screen.findByRole('heading', { level: 1 })).textContent).toContain(label);
    },
  );

  it('marks the open screen in the navigation', async () => {
    serve('k');
    setKey('k');
    open('/console/analytics');

    await screen.findByRole('heading', { name: 'Analytics' });
    expect(screen.getByRole('link', { name: 'Analytics' }).getAttribute('aria-current')).toBe(
      'page',
    );
    expect(screen.getByRole('link', { name: 'Overview' }).getAttribute('aria-current')).toBeNull();
  });

  it('shows only http and https pull request links', async () => {
    serve('k', [
      run('r-1', { pr_url: 'https://example.com/o/r/pull/7' }),
      run('r-2', { pr_url: 'javascript:alert(1)' }),
    ]);
    setKey('k');
    open('/console/runs');

    const links = (await screen.findAllByRole('link', { name: 'PR' })).map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toEqual(['https://example.com/o/r/pull/7']);
  });

  it('says what the server said when a list fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (path: string) =>
        path === '/v1/accounts/me' ? reply(me) : reply({ errors: ['limit must be 1 to 500'] }, 400),
      ),
    );
    setKey('k');
    open('/console/runs');

    expect(await screen.findByText('limit must be 1 to 500')).toBeTruthy();
  });
});
