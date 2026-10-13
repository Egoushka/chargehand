import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { BATCH_A, run } from '../../testing/fixtures';
import { batchEvents, batchPulls, finishedBatch, richBatch } from '../../testing/fixtures-runs';
import { renderConsole } from '../../testing/render';
import { type Scenario, serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';

const open = (tab?: string, id = BATCH_A) => `/console/runs/${id}${tab ? `?tab=${tab}` : ''}`;

const serve = (extra: Scenario = {}) =>
  serveApi({
    runs: [run(BATCH_A)],
    batches: { [BATCH_A]: richBatch() },
    pulls: batchPulls(),
    ...extra,
  });

const tab = (name: RegExp | string) => screen.findByRole('tab', { name });

beforeEach(() => {
  // React Flow reads the box of its container and the browser's matrix class; happy-dom has neither.
  vi.stubGlobal(
    'DOMMatrixReadOnly',
    class {
      m22 = 1;
      constructor(public transform?: string) {}
    },
  );
});

describe('the batch header', () => {
  it('names the batch, where it runs and what it has used against its caps', async () => {
    serve();
    renderConsole(open());

    expect((await screen.findByRole('heading', { level: 1, name: BATCH_A })).textContent).toBe(
      BATCH_A,
    );
    expect(screen.getByText('payments-api')).toBeTruthy();
    expect(screen.getByText('@a91c0ff')).toBeTruthy();
    expect(screen.getByText('account owner')).toBeTruthy();
    expect(screen.getAllByText('Running').length).toBeGreaterThan(0);
    expect(await screen.findByText(/of 4M · \d+%/)).toBeTruthy();
    expect(screen.getByText(/of \$15\.00 · \d+%/)).toBeTruthy();
    expect(screen.getByText('2 of 8 done')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Cancel batch' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Halt server' })).toBeTruthy();
  });

  it('counts the tasks, the governor decisions and the pull requests on their tabs', async () => {
    serve();
    renderConsole(open());
    expect((await tab(/^Tasks/)).textContent).toContain('8');
    expect(screen.getByRole('tab', { name: /^Governor/ }).textContent).toContain('4');
    expect(screen.getByRole('tab', { name: /^Pull requests/ }).textContent).toContain('2');
    expect(screen.getAllByRole('tab')).toHaveLength(8);
    expect(screen.getByRole('tab', { name: /^Tasks/ }).getAttribute('aria-selected')).toBe('true');
  });

  it('opens the tab named in the address, and writes the tab you pick into it', async () => {
    serve();
    const { router } = renderConsole(open('spend'));
    expect((await tab(/^Spend/)).getAttribute('aria-selected')).toBe('true');
    expect(await screen.findByRole('table', { name: 'Spend per task' })).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /^Request/ }), { button: 0 });
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'request' }));
    expect(await screen.findByRole('group', { name: 'The request as JSON' })).toBeTruthy();
  });

  it('shows a finished batch with its error, how long it took, and no cancel', async () => {
    const done = finishedBatch();
    serve({ batches: { [BATCH_A]: done }, runs: [done.run] });
    renderConsole(open());
    expect(await screen.findByText('tasks_incomplete', { selector: '.b' })).toBeTruthy();
    expect(screen.getByText(/Send the tasks that did not finish again/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Cancel batch' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Halt server' })).toBeNull();
    expect(screen.getByText('39m')).toBeTruthy();
  });

  it('keeps Halt server for the owner', async () => {
    serve({ me: { ...(await import('../../testing/fixtures')).me({}), owner: false } });
    renderConsole(open());
    await screen.findByRole('button', { name: 'Cancel batch' });
    expect(screen.queryByRole('button', { name: 'Halt server' })).toBeNull();
  });
});

describe('cancelling a batch', () => {
  it('asks first, says how many tasks stop, and sends one cancel for the batch', async () => {
    const { calls } = serve();
    renderConsole(open());
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel batch' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel 1fdf01?' });
    expect(within(dialog).getByText(/1 running task stops/)).toBeTruthy();
    expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep running' }));
    expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Cancel batch' }));
    fireEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel batch' }),
    );
    await waitFor(() => expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(true));
    const post = calls.find((c) => c.path.endsWith('/cancel'));
    expect(post?.method).toBe('POST');
    expect(post?.path).toBe(`/v1/runs/${BATCH_A}/cancel`);
    expect(await screen.findByText('Cancelling the batch')).toBeTruthy();
  });

  it('says so when the server will not cancel it', async () => {
    serve({
      replies: {
        [`POST /v1/runs/${BATCH_A}/cancel`]: [409, { errors: ['already finished'] }],
      },
    });
    renderConsole(open());
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel batch' }));
    fireEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel batch' }),
    );
    expect(await screen.findByText('The batch was not cancelled')).toBeTruthy();
  });
});

describe('a batch that is not here yet', () => {
  it('shows a loading state while the view is read', async () => {
    serve({ hang: [`/v1/batches/${BATCH_A}`] });
    renderConsole(open());
    await screen.findByRole('heading', { level: 1, name: BATCH_A });
    expect(document.querySelector('[aria-busy="true"]')).toBeTruthy();
  });

  it('says a batch the server accepted has not started, and asks again', async () => {
    const { calls } = serve({
      replies: { [`/v1/batches/${BATCH_A}`]: [404, { errors: ['no such batch'] }] },
    });
    renderConsole(open());
    expect(await screen.findByText('The batch has not started')).toBeTruthy();
    expect(calls.filter((c) => c.path === `/v1/batches/${BATCH_A}?events=500`).length).toBe(1);
  });

  it('says what the server said when the view cannot be read, and retries', async () => {
    const { calls } = serve({
      replies: { [`/v1/batches/${BATCH_A}`]: [500, { errors: ['the log is unreadable'] }] },
    });
    renderConsole(open());
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Cannot read the batch');
    expect(alert.textContent).toContain('the log is unreadable');
    const before = calls.length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls.length).toBeGreaterThan(before));
  });

  it('says there is no such run for an id the caller does not see', async () => {
    serveApi({ runs: [] });
    renderConsole(open(undefined, 'run-unknown'));
    expect(await screen.findByText('No such run')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to Runs' }).getAttribute('href')).toBe(
      '/console/runs',
    );
  });
});

describe('the Tasks tab', () => {
  it('draws the task graph and lists the tasks with their state, dependencies, spend and pull request', async () => {
    serve();
    renderConsole(open());
    const table = await screen.findByRole('table', { name: 'Tasks' });
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(9);
    const t4 = rows.find((r) => within(r).queryByText('t4-metrics')) as HTMLElement;
    expect(within(t4).getByText('Running')).toBeTruthy();
    expect(within(t4).getByText('t1-schema')).toBeTruthy();
    expect(within(t4).getByText('stage test · 22 turns')).toBeTruthy();
    const t2 = rows.find((r) => within(r).queryByText('t2-retry-policy')) as HTMLElement;
    expect(within(t2).getByRole('link', { name: '#412' }).getAttribute('href')).toBe(
      'https://github.com/example/payments-api/pull/412',
    );
    expect(screen.getByText('planned: sizes assigned')).toBeTruthy();
    const graph = await waitFor(
      () => {
        const el = document.querySelector('.graph');
        if (!el) throw new Error('the graph is not drawn yet');
        return el;
      },
      { timeout: 3000 },
    );
    // happy-dom has no Web Worker, so the plain layering draws it.
    expect(graph.getAttribute('data-layout')).toBe('fallback');
    expect(document.querySelectorAll('.react-flow__node').length).toBe(8);
  });

  it('opens a task over the batch, with its id in the address, and closes it again', async () => {
    serve();
    const { router } = renderConsole(open());
    fireEvent.click(await screen.findByRole('button', { name: 't3-memory-ttl' }));
    const drawer = await screen.findByRole('dialog', { name: 'Task t3-memory-ttl' });
    expect(router.state.location.search).toMatchObject({ task: 't3-memory-ttl' });
    expect(await within(drawer).findByText('The session asked')).toBeTruthy();
    fireEvent.click(within(drawer).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(router.state.location.search).not.toHaveProperty('task');
  });

  it('opens the task named in the address as a drawer on a desktop', async () => {
    serve();
    renderConsole(`${open()}?task=t7-guards`.replace('?task=', '?task='));
    expect(await screen.findByRole('dialog', { name: 'Task t7-guards' })).toBeTruthy();
  });

  it('lists the tasks as cards on a phone, and a task opens as a page', async () => {
    viewport(true);
    serve();
    const { router } = renderConsole(open());
    fireEvent.click(await screen.findByRole('button', { name: 'Open task t5-docs' }));
    await waitFor(() =>
      expect(router.state.location.pathname).toBe(`/runs/${BATCH_A}/tasks/t5-docs`),
    );
    expect(await screen.findByRole('heading', { level: 1, name: 't5-docs' })).toBeTruthy();
  });
});

describe('the Timeline tab', () => {
  it('draws the sessions in slots, marks a continuation, and says new starts are held', async () => {
    serve();
    renderConsole(open('timeline'));
    const group = await screen.findByRole('group', { name: 'Sessions by slot' });
    expect(within(group).getAllByRole('button')).toHaveLength(6);
    const second = within(group).getByRole('button', { name: /t2-retry-policy, session 2/ });
    expect(second.textContent).toContain('S2');
    expect(second.getAttribute('data-cont')).toBe('true');
    expect(screen.getByText(/New starts are held until/)).toBeTruthy();
    expect(screen.getByText(/1 of 4 slots busy/)).toBeTruthy();
    expect(screen.getByText(/2 tasks are waiting to start/)).toBeTruthy();
    expect(screen.getByRole('note').textContent).toContain('Not recorded');
  });

  it('opens the task of a bar', async () => {
    serve();
    const { router } = renderConsole(open('timeline'));
    fireEvent.click(await screen.findByRole('button', { name: /t7-guards, session 1/ }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ task: 't7-guards' }));
  });

  it('says so, and says each task is one bar, when the server holds no events', async () => {
    const done = finishedBatch();
    serve({ batches: { [BATCH_A]: done }, runs: [done.run] });
    renderConsole(open('timeline'));
    expect(await screen.findByText(/The batch events are not held/)).toBeTruthy();
    expect(screen.queryByText(/New starts are held until/)).toBeNull();
  });

  it('says no session has started for a batch whose tasks all wait', async () => {
    const idle = richBatch();
    serve({
      batches: {
        [BATCH_A]: {
          ...idle,
          tasks: idle.tasks.filter((t) => !t.started_at),
          rate_limits: [],
          timeline: { available: true, total: 0, events: [] },
        },
      },
    });
    renderConsole(open('timeline'));
    expect(await screen.findByText('No session has started')).toBeTruthy();
  });
});

describe('the Events tab', () => {
  const stream = (n = 3) =>
    batchEvents(Date.now())
      .slice(-n)
      .map((e) => `event: status\ndata: ${JSON.stringify(e)}\n\n`)
      .join('');

  it('lists the recorded events and the live ones once each, and follows the stream', async () => {
    serve({ streams: { [`/v1/runs/${BATCH_A}/events`]: stream() } });
    renderConsole(open('events'));
    const log = await screen.findByRole('log', { name: 'Events of this batch' });
    await waitFor(() => expect(screen.getByText('Stream ended')).toBeTruthy());
    expect(within(log).getAllByText('session container is up').length).toBeGreaterThan(0);
    // The three newest events are in both lists and appear once.
    expect(within(log).getAllByText('draft pull request opened /pull/412')).toHaveLength(1);
    expect(log.textContent).toMatch(/\d+ of \d+ events shown/);
  });

  it('narrows by kind and by task, and says when nothing matches', async () => {
    serve();
    renderConsole(open('events'));
    const log = await screen.findByRole('log', { name: 'Events of this batch' });
    const total = within(log).getAllByText(/./).length;

    fireEvent.click(screen.getByRole('button', { name: 'Governor' }));
    await waitFor(() => expect(log.textContent).toContain('observe · warn · repeated_call'));
    expect(log.textContent).not.toContain('session container is up');

    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    fireEvent.change(screen.getByLabelText('Task'), { target: { value: 't3-memory-ttl' } });
    await waitFor(() => expect(log.textContent).not.toContain('t1-schema'));
    expect(within(log).getAllByText(/./).length).toBeLessThan(total);

    fireEvent.click(screen.getByRole('button', { name: 'PRs and merges' }));
    expect(await screen.findByText('No event matches this filter')).toBeTruthy();
  });

  it('pauses the list while the stream goes on, and resumes it', async () => {
    serve();
    renderConsole(open('events'));
    await screen.findByRole('log', { name: 'Events of this batch' });
    const pause = screen.getByRole('button', { name: 'Pause' });
    fireEvent.click(pause);
    expect(screen.getByRole('button', { name: 'Resume' }).getAttribute('aria-pressed')).toBe(
      'true',
    );
    expect(screen.getByText('Paused', { selector: '.ch-live' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
    expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy();
  });

  it('says there are no events when the server holds none and the stream is closed', async () => {
    const idle = richBatch();
    // A comment line keeps the body non-empty: the stream is open, says nothing, and closes.
    serve({
      batches: { [BATCH_A]: { ...idle, timeline: { available: false, total: 0, events: [] } } },
      streams: { [`/v1/runs/${BATCH_A}/events`]: ': nothing yet\n\n' },
    });
    renderConsole(open('events'));
    expect(await screen.findByText('No events')).toBeTruthy();
    expect(await screen.findByText('The server holds no events for this batch.')).toBeTruthy();
  });

  it('wraps the lines on a phone', async () => {
    viewport(true);
    serve();
    renderConsole(open('events'));
    await screen.findByRole('log', { name: 'Events of this batch' });
    expect(screen.getByLabelText('Task').className).toContain('touch');
  });
});

describe('the Governor tab', () => {
  it('says the mode, counts the decisions, shows each task’s budget and what is not recorded', async () => {
    serve();
    renderConsole(open('governor'));
    expect(await screen.findByText('Governor mode: observe')).toBeTruthy();
    const table = screen.getByRole('table', { name: 'Governor decisions' });
    expect(within(table).getAllByRole('row')).toHaveLength(4);
    expect(within(table).getAllByText('checkpoint_context').length).toBeGreaterThan(0);
    expect(screen.getByText('2 warn, 2 checkpoint, 0 stop')).toBeTruthy();
    expect(screen.getByText('1 of 2')).toBeTruthy();
    expect(screen.getByLabelText(/Budget of t4-metrics/)).toBeTruthy();
    expect(screen.getByRole('note').textContent).toContain(
      'Governor decisions are kept as counts per signal and rung',
    );
    expect(await screen.findByText('Live decision events')).toBeTruthy();
  });

  it('says no record has been written yet', async () => {
    const b = richBatch();
    serve({
      batches: {
        [BATCH_A]: { ...b, tasks: b.tasks.map((t) => ({ ...t, governor: undefined })) },
      },
    });
    renderConsole(open('governor'));
    expect(await screen.findByText('No governor record yet')).toBeTruthy();
  });

  it('warns that enforce acts, and that mixed modes differ', async () => {
    const b = richBatch();
    const modes = (m: (i: number) => string) => ({
      ...b,
      tasks: b.tasks.map((t, i) =>
        t.governor ? { ...t, governor: { ...t.governor, mode: m(i) } } : t,
      ),
    });
    serve({ batches: { [BATCH_A]: modes(() => 'enforce') as never } });
    const { unmount } = renderConsole(open('governor'));
    expect(await screen.findByText('Governor mode: enforce')).toBeTruthy();
    unmount();
    serve({ batches: { [BATCH_A]: modes((i) => (i % 2 ? 'enforce' : 'observe')) as never } });
    renderConsole(open('governor'));
    expect(await screen.findByText(/Governor modes differ/)).toBeTruthy();
  });
});

describe('the Spend tab', () => {
  it('totals tokens and dollars per task, says they are a dollar equivalent, and shows the cache', async () => {
    serve();
    renderConsole(open('spend'));
    const table = await screen.findByRole('table', { name: 'Spend per task' });
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(7); // header, five tasks, total
    expect(within(rows[1] as HTMLElement).getByText('t1-schema')).toBeTruthy();
    const total = rows.at(-1) as HTMLElement;
    expect(within(total).getByText('Total')).toBeTruthy();
    expect(within(total).getByText('≈ $6.34')).toBeTruthy();
    expect(screen.getByText('dollar equivalent, not a bill')).toBeTruthy();
    expect(screen.getByRole('img', { name: /^Spend share: t7-guards/ })).toBeTruthy();
    expect(screen.getByText(/Cache reads are priced at/)).toBeTruthy();
  });

  it('says nothing has been spent for a batch that has not started a task', async () => {
    const b = richBatch();
    serve({
      batches: { [BATCH_A]: { ...b, tasks: b.tasks.filter((t) => !t.spend && !t.live) } },
    });
    renderConsole(open('spend'));
    expect(await screen.findByText('Nothing spent yet')).toBeTruthy();
  });

  it('lists a card per task on a phone', async () => {
    viewport(true);
    serve();
    renderConsole(open('spend'));
    expect(await screen.findByText(/in 90k · out 21k/)).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Spend per task' })).toBeNull();
  });
});

describe('the Pull requests tab', () => {
  it('lists the batch’s pull requests with the integrator’s policy, last message and flags', async () => {
    const { calls } = serve();
    renderConsole(open('prs'));
    const table = await screen.findByRole('table', { name: 'Pull requests' });
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(3);
    // Newest first.
    expect(within(rows[1] as HTMLElement).getByRole('link', { name: '#412' })).toBeTruthy();
    expect(within(rows[1] as HTMLElement).getByText('Waits for you')).toBeTruthy();
    expect(within(rows[1] as HTMLElement).getByText('policy is ask')).toBeTruthy();
    expect(within(rows[2] as HTMLElement).getByText('Merged')).toBeTruthy();
    expect(within(rows[2] as HTMLElement).getByText('merged: required checks passed')).toBeTruthy();
    expect(screen.getByText('integrator policy')).toBeTruthy();
    expect(calls.some((c) => c.path.startsWith('/v1/pulls?') && c.path.includes('since='))).toBe(
      true,
    );
  });

  it('shows no link for an address that is not http or https', async () => {
    const b = richBatch();
    serve({
      batches: {
        [BATCH_A]: {
          ...b,
          pull_requests: b.pull_requests.map((p) => ({ ...p, url: 'javascript:alert(1)' })),
        },
        // The forge's list repeats the same two with their real addresses; only the batch's own are kept here.
      },
      pulls: { ...batchPulls(), pulls: [] },
    });
    renderConsole(open('prs'));
    await screen.findByRole('table', { name: 'Pull requests' });
    expect(document.querySelector('a[href^="javascript:"]')).toBeNull();
  });

  it('says there is no pull request yet', async () => {
    serve({
      batches: { [BATCH_A]: { ...richBatch(), pull_requests: [] } },
      pulls: { ...batchPulls(), pulls: [] },
    });
    renderConsole(open('prs'));
    expect(await screen.findByText('No pull request yet')).toBeTruthy();
  });
});

describe('the Artifacts tab', () => {
  it('lists the batch’s artifacts, and a task’s with the diff in the viewer', async () => {
    const done = finishedBatch();
    serve({ batches: { [BATCH_A]: done }, runs: [done.run] });
    renderConsole(open('artifacts'));
    expect(await screen.findByRole('button', { name: /Result JSON/ })).toBeTruthy();
    expect(await screen.findByRole('group', { name: 'Result JSON as JSON' })).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Artifacts of'), {
      target: { value: 't2-retry-policy' },
    });
    const changes = await screen.findByRole('button', { name: /Changes/ });
    fireEvent.click(changes);
    expect(await screen.findByText('3 files changed')).toBeTruthy();
    expect(
      await screen.findByRole(
        'group',
        { name: /Changes: src\/Payments\/RetryPolicy\.cs/ },
        { timeout: 3000 },
      ),
    ).toBeTruthy();
  });

  it('shows the verification run, and says a reference is not the content', async () => {
    serve();
    renderConsole(open('artifacts'));
    fireEvent.change(await screen.findByLabelText('Artifacts of'), {
      target: { value: 't7-guards' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /Kept work/ }));
    expect(await screen.findByRole('group', { name: 'Kept work as JSON' })).toBeTruthy();
  });

  it('says a governor log is a reference with its hash, never inline', async () => {
    serve();
    renderConsole(open('artifacts'));
    fireEvent.change(await screen.findByLabelText('Artifacts of'), {
      target: { value: 't1-schema' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /Governor log/ }));
    expect(await screen.findByText('a reference, not the content')).toBeTruthy();
    expect(screen.getByText(/never inline/)).toBeTruthy();
    expect(screen.getByText('f'.repeat(64))).toBeTruthy();
  });

  it('says there is no artifact yet for a batch that is still running', async () => {
    serve({ batches: { [BATCH_A]: { ...richBatch(), artifacts: [] } } });
    renderConsole(open('artifacts'));
    expect(await screen.findByText('No artifact yet')).toBeTruthy();
  });
});

describe('the Request tab', () => {
  it('shows the request rebuilt from the batch, and says it is rebuilt, not recorded', async () => {
    serve();
    renderConsole(open('request'));
    const json = await screen.findByRole('group', { name: 'The request as JSON' });
    expect(json.textContent).toContain('"contract_version": "request/v1"');
    expect(json.textContent).toContain('"id": "t5-docs"');
    expect(json.textContent).toContain('"depends_on"');
    expect(screen.getByText('Rebuilt, not recorded')).toBeTruthy();
    expect(screen.getByText('planned: sizes assigned')).toBeTruthy();
  });

  it('copies the JSON', async () => {
    serve();
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    renderConsole(open('request'));
    fireEvent.click(await screen.findByRole('button', { name: 'Copy JSON' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledOnce());
    expect(JSON.parse(writeText.mock.calls[0]?.[0] as string)).toMatchObject({
      contract_version: 'request/v1',
    });
    expect(await screen.findByText('Request copied')).toBeTruthy();
  });

  it('says so when the browser refuses the clipboard', async () => {
    serve();
    vi.stubGlobal('navigator', {
      ...navigator,
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('no')) },
    });
    renderConsole(open('request'));
    fireEvent.click(await screen.findByRole('button', { name: 'Copy JSON' }));
    expect(await screen.findByText('Cannot copy')).toBeTruthy();
  });

  it('clones as a new batch: opens Start a batch with the request in the router state, and in no address', async () => {
    serve();
    const { router } = renderConsole(open('request'));
    const clone = await screen.findByRole('button', { name: 'Clone as a new batch' });
    await waitFor(() => expect((clone as HTMLButtonElement).disabled).toBe(false));
    await act(async () => {
      fireEvent.click(clone);
    });
    await waitFor(() => expect(router.state.location.pathname).toBe('/new'));
    expect(router.state.location.search).toEqual({});
    expect(router.state.location.href).not.toContain('t5-docs');
    const state = router.state.location.state as {
      startBatch?: { request: unknown; from?: string };
    };
    expect(state.startBatch?.from).toBe(BATCH_A);
    expect(state.startBatch?.request).toMatchObject({
      contract_version: 'request/v1',
      driven: { tasks: expect.any(Array) },
    });
  });

  it('fetches the whole goal of a task the view cut, before it clones', async () => {
    const b = richBatch();
    const cut = {
      ...b,
      tasks: b.tasks.map((t) =>
        t.id === 't5-docs' ? { ...t, goal: 'cut', goal_truncated: true } : t,
      ),
    };
    const { calls } = serve({ batches: { [BATCH_A]: cut } });
    renderConsole(open('request'));
    await waitFor(() =>
      expect(calls.some((c) => c.path.startsWith(`/v1/batches/${BATCH_A}/tasks/t5-docs`))).toBe(
        true,
      ),
    );
    const json = await screen.findByRole('group', { name: 'The request as JSON' });
    await waitFor(() => expect(json.textContent).toContain('The whole goal is shown here'));
  });

  it('has a Clone shortcut in the header that opens the Request tab', async () => {
    serve();
    const { router } = renderConsole(open());
    fireEvent.click(await screen.findByRole('button', { name: 'Clone' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'request' }));
  });
});
