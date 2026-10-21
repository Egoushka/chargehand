import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { run } from '../../testing/fixtures';
import { runList } from '../../testing/fixtures-runs';
import { layout } from '../../testing/layout';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  activeCount,
  cleanFilters,
  DEFAULT_FILTERS,
  matchesFilters,
  matchesState,
  narrowsInBrowser,
  parseViews,
  pullNumber,
  rangeSince,
  serverStatus,
  toRunFilter,
} from './model';
import { readViews, writeViews } from './views';

const NOW = Date.parse('2026-10-13T12:00:00Z');
const dataRows = () =>
  within(screen.getByRole('table', { name: 'Runs' }))
    .getAllByRole('row')
    .slice(1);

beforeEach(() => layout());
afterEach(() => vi.useRealTimers());

describe('the runs model: session calls', () => {
  it('hides a batch’s research and review calls unless asked for', () => {
    const call = run('c', { parent_run_id: 'run-parent' }, NOW);
    expect(matchesFilters(call, DEFAULT_FILTERS, NOW)).toBe(false);
    expect(matchesFilters(call, { ...DEFAULT_FILTERS, calls: true }, NOW)).toBe(true);
    expect(matchesFilters(run('b', {}, NOW), DEFAULT_FILTERS, NOW)).toBe(true);
  });
});

describe('the runs model', () => {
  const runs = runList(40, NOW);
  const failed = runs.find((r) => r.status === 'failed' && r.error_code === 'verification_failed');
  const cancelled = runs.find((r) => r.error_code === 'cancelled');
  const denied = runs.find((r) => r.status === 'denied');

  it('reads a state the way the design words it: a cancelled run is not a failure, a denied run is', () => {
    expect(failed && matchesState(failed, 'failed')).toBe(true);
    expect(cancelled && matchesState(cancelled, 'failed')).toBe(false);
    expect(cancelled && matchesState(cancelled, 'cancelled')).toBe(true);
    expect(denied && matchesState(denied, 'failed')).toBe(true);
    expect(failed && matchesState(failed, 'attention')).toBe(true);
    expect(cancelled && matchesState(cancelled, 'attention')).toBe(false);
    expect(
      runs.filter((r) => matchesState(r, 'running')).every((r) => r.status === 'running'),
    ).toBe(true);
  });

  it('asks the server only for what it can narrow: one state, kind, repository, account and a start time', () => {
    expect(serverStatus('done')).toBe('completed');
    expect(serverStatus('cancelled')).toBe('failed');
    expect(serverStatus('failed')).toBeUndefined();
    expect(serverStatus('attention')).toBeUndefined();
    const filter = toRunFilter(
      { ...DEFAULT_FILTERS, state: 'done', kind: 'batch', repository: '/srv/a', account: 'bot' },
      NOW,
      200,
    );
    expect(filter).toMatchObject({
      limit: 200,
      status: 'completed',
      kind: 'batch',
      repository: '/srv/a',
      actor: 'bot',
      since: '2026-10-06T12:00:00.000Z',
    });
    expect(toRunFilter(DEFAULT_FILTERS, NOW, 100).kind).toBeUndefined();
    expect(rangeSince('all', NOW)).toBeUndefined();
    expect(rangeSince('24h', NOW + 20_000)).toBe('2026-10-12T12:00:00.000Z');
  });

  it('narrows by search, preset and range in the browser, whatever the server returned', () => {
    const old = { ...run('r-old'), started_at: new Date(NOW - 10 * 86_400_000).toISOString() };
    const fresh = run('run-search-me-ab12cd', { preset: 'cheap', branch: 'feature/retry' }, NOW);
    expect(matchesFilters(old, DEFAULT_FILTERS, NOW)).toBe(false);
    expect(matchesFilters(old, { ...DEFAULT_FILTERS, range: 'all' }, NOW)).toBe(true);
    expect(matchesFilters(fresh, { ...DEFAULT_FILTERS, q: 'SEARCH-me' }, NOW)).toBe(true);
    expect(matchesFilters(fresh, { ...DEFAULT_FILTERS, q: 'retry' }, NOW)).toBe(true);
    expect(matchesFilters(fresh, { ...DEFAULT_FILTERS, q: 'nope' }, NOW)).toBe(false);
    expect(matchesFilters(fresh, { ...DEFAULT_FILTERS, preset: 'cheap' }, NOW)).toBe(true);
    expect(matchesFilters(fresh, { ...DEFAULT_FILTERS, preset: 'thorough' }, NOW)).toBe(false);
  });

  it('knows which filters only the browser applies, and counts the ones that are set', () => {
    expect(narrowsInBrowser(DEFAULT_FILTERS)).toBe(false);
    expect(narrowsInBrowser({ ...DEFAULT_FILTERS, q: 'x' })).toBe(true);
    expect(narrowsInBrowser({ ...DEFAULT_FILTERS, state: 'done' })).toBe(false);
    expect(narrowsInBrowser({ ...DEFAULT_FILTERS, state: 'failed' })).toBe(true);
    expect(activeCount(DEFAULT_FILTERS)).toBe(0);
    expect(activeCount({ ...DEFAULT_FILTERS, q: 'x', state: 'done', range: '30d' })).toBe(3);
  });

  it('keeps only saved views that are sound, and falls back per field', () => {
    expect(parseViews(null)).toEqual([]);
    expect(parseViews('not json')).toEqual([]);
    expect(parseViews('{"a":1}')).toEqual([]);
    const views = parseViews(
      JSON.stringify([
        { id: 'a', name: ' Mine ', filters: { state: 'bogus', kind: 'batch', range: 'forever' } },
        { id: 'b', name: '', filters: {} },
        { name: 'no id' },
        null,
      ]),
    );
    expect(views).toHaveLength(1);
    expect(views[0]).toMatchObject({
      id: 'a',
      name: 'Mine',
      filters: { ...DEFAULT_FILTERS, kind: 'batch' },
    });
    expect(cleanFilters(undefined)).toEqual(DEFAULT_FILTERS);
    expect(pullNumber('https://h/o/n/pull/412')).toBe('412');
    expect(pullNumber(null)).toBeNull();
  });

  it('keeps views when storage works and survives storage that refuses', () => {
    writeViews([{ id: 'v1', name: 'Mine', filters: DEFAULT_FILTERS }]);
    expect(readViews()).toHaveLength(1);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(() => writeViews([])).not.toThrow();
    expect(readViews()).toEqual([]);
  });
});

describe('Runs', () => {
  it('lists the runs newest first with the state word, the id, kind, repository, tokens and spend', async () => {
    const { calls } = serveApi({ runs: runList(12) });
    renderConsole('/console/runs');
    await screen.findByRole('table', { name: 'Runs' });
    await waitFor(() => expect(dataRows().length).toBe(12));

    expect(screen.getByRole('heading', { level: 1, name: 'Runs' })).toBeTruthy();
    expect(screen.getByText('12 runs, newest first · Kyiv time')).toBeTruthy();
    const first = dataRows()[0] as HTMLElement;
    expect(within(first).getByText('Running')).toBeTruthy();
    expect(within(first).getByRole('link', { name: runList(1)[0]?.run_id }).textContent).toBe(
      'a10000',
    );
    expect(within(first).getByText('payments-api')).toBeTruthy();
    expect(within(first).getByText('batch')).toBeTruthy();
    const words = dataRows().map(
      (r) => within(r).getByText(/^(Running|Done|Failed|Question|Cancelled|Lost)$/).textContent,
    );
    expect(words.slice(0, 7)).toEqual([
      'Running',
      'Done',
      'Done',
      'Failed',
      'Question',
      'Done',
      'Cancelled',
    ]);
    expect(calls.find((c) => c.path.startsWith('/v1/runs?'))?.path).toMatch(/limit=100/);
    expect(calls.find((c) => c.path.startsWith('/v1/runs?'))?.path).toMatch(/since=/);
  });

  it('renders only the rows in view of a long list, and says how many it holds', async () => {
    serveApi({ runs: runList(100) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBeGreaterThan(10));
    expect(dataRows().length).toBeLessThan(40);
    expect(screen.getByRole('table', { name: 'Runs' }).getAttribute('aria-rowcount')).toBe('101');
    expect(screen.getByText(/Showing 100 of 100 loaded/)).toBeTruthy();
  });

  it('asks the server for the filters it knows, and narrows the rest in the page', async () => {
    const { calls } = serveApi({ runs: runList(40) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBeGreaterThan(5));

    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'batch' } });
    await waitFor(() =>
      expect(calls.some((c) => c.path.includes('kind=batch') && c.path.includes('limit=100'))).toBe(
        true,
      ),
    );
    fireEvent.change(screen.getByLabelText('State'), { target: { value: 'done' } });
    await waitFor(() => expect(calls.some((c) => c.path.includes('status=completed'))).toBe(true));
    await waitFor(() =>
      expect(dataRows().every((r) => within(r).queryByText('Done') !== null)).toBe(true),
    );
    fireEvent.change(screen.getByLabelText('Date'), { target: { value: 'all' } });
    await waitFor(() => expect(calls.at(-1)?.path).not.toContain('since='));
    expect(screen.getByRole('button', { name: /Remove filter Kind batch/ })).toBeTruthy();
  });

  it('searches the loaded runs and focuses the search box on the slash key', async () => {
    serveApi({ runs: runList(30) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBeGreaterThan(5));
    const search = screen.getByLabelText('Search runs');
    fireEvent.keyDown(document.body, { key: '/' });
    expect(document.activeElement).toBe(search);
    fireEvent.change(search, { target: { value: 'a10003' } });
    await waitFor(() => expect(dataRows().length).toBe(1));
    expect(screen.getByText(/1 of 30 loaded runs match/)).toBeTruthy();
    fireEvent.change(search, { target: { value: 'no such run' } });
    expect(await screen.findByText('No run matches these filters')).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Clear filters' }).at(-1) as HTMLElement);
    await waitFor(() => expect(dataRows().length).toBeGreaterThan(5));
  });

  it('applies a built-in view, saves the current filters as a view, and deletes it', async () => {
    serveApi({ runs: runList(30) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBeGreaterThan(5));
    const views = screen.getByLabelText('Saved views') as HTMLSelectElement;
    expect(views.value).toBe('all');

    fireEvent.change(views, { target: { value: 'attention' } });
    expect((screen.getByLabelText('State') as HTMLSelectElement).value).toBe('attention');

    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'single' } });
    expect((screen.getByLabelText('Saved views') as HTMLSelectElement).value).toBe('custom');
    fireEvent.click(screen.getByRole('button', { name: 'Save view' }));
    const dialog = await screen.findByRole('dialog', { name: 'Save this view' });
    fireEvent.change(within(dialog).getByLabelText('Name'), {
      target: { value: 'Single and stuck' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save view' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    const select = screen.getByLabelText('Saved views') as HTMLSelectElement;
    expect(select.selectedOptions[0]?.textContent).toBe('View: Single and stuck');
    expect(JSON.parse(localStorage.getItem('chargehand.console.runs.views') ?? '[]')).toMatchObject(
      [{ name: 'Single and stuck', filters: { state: 'attention', kind: 'single' } }],
    );

    fireEvent.click(screen.getByRole('button', { name: 'Delete the view Single and stuck' }));
    expect(localStorage.getItem('chargehand.console.runs.views')).toBe('[]');
    expect((screen.getByLabelText('Saved views') as HTMLSelectElement).value).toBe('all');
  });

  it('refuses a view name that is taken', async () => {
    serveApi({ runs: runList(5) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(5));
    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'single' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save view' }));
    const dialog = await screen.findByRole('dialog', { name: 'Save this view' });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'all runs' } });
    expect(within(dialog).getByText('A view with this name exists. Pick another.')).toBeTruthy();
    expect(
      (within(dialog).getByRole('button', { name: 'Save view' }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('selects runs, counts the ones cancel can act on, and cancels only those after a confirm', async () => {
    const runs = runList(12);
    const { calls } = serveApi({ runs });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(12));
    const running = runs.filter((r) => r.status === 'running').map((r) => r.run_id);
    const done = runs.find((r) => r.status === 'completed') as (typeof runs)[number];
    for (const id of [...running.slice(0, 1), done.run_id])
      fireEvent.click(screen.getByLabelText(`Select run ${id.slice(-6)}`));

    expect(await screen.findByText('2 selected')).toBeTruthy();
    expect(screen.getByText('1 finished: skipped by cancel')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel 1 running run' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel 1 running run?' });
    expect(within(dialog).getByText(/1 selected run has finished and is skipped/)).toBeTruthy();
    expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep running' }));
    expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Cancel 1 running run' }));
    fireEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel 1 run' }),
    );
    await waitFor(() => expect(calls.filter((c) => c.path.endsWith('/cancel'))).toHaveLength(1));
    const post = calls.find((c) => c.path.endsWith('/cancel'));
    expect(post?.method).toBe('POST');
    expect(post?.path).toBe(`/v1/runs/${running[0]}/cancel`);
    expect(await screen.findByText('Cancelling 1 of 1 run')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('2 selected')).toBeNull());
  });

  it('selects every run in the list from the header, and clears the selection', async () => {
    serveApi({ runs: runList(6) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(6));
    fireEvent.click(screen.getByLabelText('Select all runs in the list'));
    expect(await screen.findByText('6 selected')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    await waitFor(() => expect(screen.queryByText('6 selected')).toBeNull());
  });

  it('moves a cursor with j and k, selects with x, and opens the run with Enter', async () => {
    const runs = runList(8);
    serveApi({ runs });
    const { router } = renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(8));
    expect(dataRows()[0]?.getAttribute('data-cursor')).toBe('true');
    fireEvent.keyDown(document.body, { key: 'j' });
    await waitFor(() => expect(dataRows()[1]?.getAttribute('data-cursor')).toBe('true'));
    fireEvent.keyDown(document.body, { key: 'x' });
    expect(await screen.findByText('1 selected')).toBeTruthy();
    fireEvent.keyDown(document.body, { key: 'k' });
    await waitFor(() => expect(dataRows()[0]?.getAttribute('data-cursor')).toBe('true'));
    fireEvent.keyDown(document.body, { key: 'Enter' });
    await waitFor(() => expect(router.state.location.pathname).toBe(`/runs/${runs[0]?.run_id}`));
  });

  it('asks for the next 100 when the server filled the window', async () => {
    const { calls } = serveApi({ runs: runList(250) });
    renderConsole('/console/runs');
    const more = await screen.findByRole('button', { name: 'Load the next 100' });
    expect(screen.getByText(/Showing 100 of 100 loaded/)).toBeTruthy();
    fireEvent.click(more);
    await waitFor(() => expect(calls.some((c) => c.path.includes('limit=200'))).toBe(true));
    await screen.findByText(/Showing 200 of 200 loaded/);
  });

  it('keeps asking by itself while a browser-side search finds too few matches', async () => {
    const { calls } = serveApi({ runs: runList(250) });
    renderConsole('/console/runs');
    await screen.findByRole('button', { name: 'Load the next 100' });
    fireEvent.change(screen.getByLabelText('Search runs'), { target: { value: 'zzz-nothing' } });
    await waitFor(() => expect(calls.some((c) => c.path.includes('limit=300'))).toBe(true));
  });

  it('shows a loading state, an error that keeps the older list and retries, and an empty state', async () => {
    serveApi({ hang: ['/v1/runs'] });
    const loading = renderConsole('/console/runs');
    expect(await screen.findByText('Loading')).toBeTruthy();
    loading.unmount();

    serveApi({ replies: { '/v1/runs': [502, { errors: ['bad gateway'] }] } });
    renderConsole('/console/runs');
    expect(await screen.findByText('Cannot load runs')).toBeTruthy();
    expect(screen.getByText('GET /v1/runs')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('says there are no runs yet, and offers to start a batch', async () => {
    serveApi({ runs: [] });
    renderConsole('/console/runs');
    expect(await screen.findByText('No runs yet')).toBeTruthy();
    expect(screen.getAllByRole('link', { name: /Start a batch/ }).length).toBeGreaterThan(0);
  });

  it('lays out long content without widening the page', async () => {
    const long = run(
      'run-20261013-999999-abcdef',
      {
        status: 'failed',
        error_code: 'checkout_has_secrets_in_a_very_long_error_code_name',
        repository: '/srv/work/a-repository-with-a-very-long-name-that-does-not-fit-in-a-cell',
        actor: 'a-service-account-with-a-very-long-name',
        branch: 'chargehand/20261013-a-very-long-branch-name-that-goes-on-and-on',
      },
    );
    serveApi({ runs: [long] });
    renderConsole('/console/runs');
    const row = (await waitFor(() => dataRows()[0])) as HTMLElement;
    expect(
      within(row)
        .getByText(/checkout_has_secrets/)
        .closest('[title]')
        ?.getAttribute('title'),
    ).toBe('checkout_has_secrets_in_a_very_long_error_code_name');
    expect(within(row).getByTitle(long.repository as string)).toBeTruthy();
  });

  it('never makes a link of a pull request address that is not http or https', async () => {
    const runs = [
      run('run-20261013-000001-aaaaaa', {
        status: 'completed',
        pr_url: 'javascript:alert(1)',
      }),
      run('run-20261013-000002-bbbbbb', {
        status: 'completed',
        pr_url: 'https://github.com/example/payments-api/pull/412',
      }),
    ];
    serveApi({ runs });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(2));
    const links = screen.getAllByRole('link', { name: /^#?PR|^#\d+/ });
    expect(links).toHaveLength(1);
    expect(links[0]?.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/412');
    expect(links[0]?.textContent).toBe('#412');
  });

  it('is a list of cards on a phone, with the filters behind a button and no bulk bar', async () => {
    viewport(true);
    serveApi({ runs: runList(6) });
    renderConsole('/console/runs');
    await screen.findAllByRole('listitem');
    expect(screen.queryByRole('table', { name: 'Runs' })).toBeNull();
    expect(screen.queryByLabelText('Kind')).toBeNull();
    const toggle = screen.getByRole('button', { name: /Filters/ });
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'single' } });
    expect(toggle.textContent).toContain('1');
  });

  it('on a phone, says a running run has no count yet instead of a bare dash', async () => {
    viewport(true);
    serveApi({
      runs: [run('run-20261013-000001-aaaaaa', { status: 'running', tokens: null, usd: null })],
    });
    renderConsole('/console/runs');
    const card = (await screen.findByRole('link', { name: 'run-20261013-000001-aaaaaa' })).closest(
      'li',
    ) as HTMLElement;
    expect(within(card).getByText(/tokens not counted yet/)).toBeTruthy();
    expect(within(card).queryByText('—')).toBeNull();
  });

  it('refreshes quickly while a run is active and slowly when nothing is', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { calls } = serveApi({ runs: runList(4) });
    renderConsole('/console/runs');
    await waitFor(() => expect(dataRows().length).toBe(4));
    const before = calls.filter((c) => c.path.startsWith('/v1/runs?')).length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_500);
    });
    expect(calls.filter((c) => c.path.startsWith('/v1/runs?')).length).toBeGreaterThan(before);
  });
});
