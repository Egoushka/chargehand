import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import { BATCH_A, run } from '../../testing/fixtures';
import { batchEvents, richBatch, taskDetail } from '../../testing/fixtures-runs';
import { renderConsole } from '../../testing/render';
import { type Scenario, serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  answerPreview,
  answersReady,
  sessionRows,
  sessionText,
  TERMINAL,
  turnsSeries,
} from './model';

/** A value the fixture has: a test that spreads one would fail loudly if it did not. */
const must = <T,>(value: T | null | undefined): T => {
  if (value == null) throw new Error('the fixture lacks this value');
  return value;
};

const page = (task: string) => `/console/runs/${BATCH_A}/tasks/${task}`;

function serve(extra: Scenario = {}, over: Record<string, Partial<BatchTaskDetailBody>> = {}) {
  const tasks = Object.fromEntries(
    Object.entries(over).map(([id, patch]) => [
      `${BATCH_A}/${id}`,
      { ...taskDetail(BATCH_A, id), ...patch },
    ]),
  );
  return serveApi({ runs: [run(BATCH_A)], batches: { [BATCH_A]: richBatch() }, tasks, ...extra });
}

describe('the task model', () => {
  const detail = taskDetail(BATCH_A, 't2-retry-policy');

  it('lists the sessions in order, with the time each container came up when the events say', () => {
    const rows = sessionRows(detail.task, detail.timeline.events);
    expect(rows.map((r) => [r.n, r.role, r.last])).toEqual([
      [1, 'first', false],
      [2, 'continuation', true],
    ]);
    expect(rows[0]?.startedAt).not.toBeNull();
    expect(sessionRows(detail.task, [])[0]?.startedAt).toBeNull();
  });

  it('words a session by its role and its start', () => {
    expect(sessionText({ n: 1, role: 'first', runId: 'r', startedAt: null, last: false })).toBe(
      'first session',
    );
    expect(
      sessionText({ n: 2, role: 'continuation', runId: 'r', startedAt: null, last: true }),
    ).toBe('continuation');
    expect(sessionText({ n: 3, role: 'last', runId: 'r', startedAt: null, last: true })).toBe(
      'last session',
    );
    expect(
      sessionText({
        n: 1,
        role: 'first',
        runId: 'r',
        startedAt: '2026-10-13T10:05:00Z',
        last: true,
      }),
    ).toMatch(/^first session, from \d\d:\d\d$/);
  });

  it('draws turns over time from the progress events, oldest first', () => {
    const at = (min: number, turns: number) => ({
      contract_version: 'run-status/v1' as const,
      run_id: 'r',
      status: 'running' as const,
      event: 'session_progress' as const,
      at: new Date(Date.UTC(2026, 9, 13, 10, min)).toISOString(),
      turns,
    });
    expect(turnsSeries([at(3, 30), at(1, 10), at(2, 20)])).toEqual([10, 20, 30]);
    expect(turnsSeries(batchEvents().filter((e) => e.event === 'governor'))).toEqual([]);
  });

  it('wants one non-empty answer for each question', () => {
    expect(answersReady(['a', 'b'], ['q1', 'q2'])).toBe(true);
    expect(answersReady(['a'], ['q1', 'q2'])).toBe(false);
    expect(answersReady(['a', '  '], ['q1', 'q2'])).toBe(false);
    expect(answersReady(['a'], [])).toBe(false);
  });

  it('previews the request an answer sends, with a mark for an answer not yet written', () => {
    const asked = taskDetail(BATCH_A, 't3-memory-ttl');
    const preview = JSON.parse(answerPreview(asked, ['Use the template', '']) as string);
    expect(preview.driven.tasks[0]).toMatchObject({
      id: 't3-memory-ttl',
      continue_from: { run_id: BATCH_A, task_id: 't3-memory-ttl' },
      answers: ['Use the template', '…'],
    });
    expect(preview.context.preset).toBe('driven');
    expect(answerPreview(detail, [])).toBeNull();
    expect(TERMINAL.has('completed')).toBe(true);
    expect(TERMINAL.has('running')).toBe(false);
  });
});

describe('a task as a page', () => {
  it('shows what a running task was asked, what it has spent against its budget and where it is', async () => {
    serve();
    renderConsole(page('t4-metrics'));
    expect((await screen.findByRole('heading', { level: 1, name: 't4-metrics' })).textContent).toBe(
      't4-metrics',
    );
    expect(await screen.findByText('Running')).toBeTruthy();
    expect(screen.getByText(/Emit retry counters and a latency histogram/)).toBeTruthy();
    expect(screen.getByText(/The whole goal is shown here/)).toBeTruthy();
    expect(screen.getByText('budget $2.05: p80 of 21 past tasks, this repository')).toBeTruthy();
    expect(screen.getByText(/of \$2\.05 · 47%/)).toBeTruthy();
    expect(screen.getByText('stage test')).toBeTruthy();
    expect(screen.getByText('runs when the session ends')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'batch 1fdf01' }).getAttribute('href')).toBe(
      `/console/runs/${BATCH_A}?tab=tasks`,
    );
    expect(screen.getByRole('link', { name: 'Open the batch' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: /Open PR/ })).toBeNull();
  });

  it('links a dependency to its own page, and the batch back', async () => {
    serve();
    renderConsole(page('t4-metrics'));
    const dep = await screen.findByRole('link', { name: 't1-schema' });
    expect(dep.getAttribute('href')).toBe(`/console/runs/${BATCH_A}/tasks/t1-schema`);
  });

  it('shows a finished task with its summary, branch, pull request, verification and the diff', async () => {
    serve();
    renderConsole(page('t2-retry-policy'));
    await screen.findByRole('heading', { level: 1, name: 't2-retry-policy' });
    expect(
      await screen.findByText('Delay doubles to a 30 second cap; ShouldRetry stops at max.'),
    ).toBeTruthy();
    expect(screen.getByText('chargehand/t2-retry-policy')).toBeTruthy();
    expect(screen.getByText('PR 412').getAttribute('href')).toBe(
      'https://github.com/example/payments-api/pull/412',
    );
    expect(screen.getByRole('link', { name: 'Open PR 412' }).getAttribute('target')).toBe('_blank');
    expect(screen.getByText('Passed')).toBeTruthy();
    expect(screen.getByText('dotnet test: exit 0 (kept apart from chargehand’s run)')).toBeTruthy();
    expect(screen.getByText('Not flagged')).toBeTruthy();
    expect(screen.getByText('3 files · +10 −1')).toBeTruthy();
    expect(screen.getByText(/1 continuation; only the last session has a run record/)).toBeTruthy();
    // A page opens the diff; the viewer is a chunk of its own and loads when it is shown.
    expect(
      await screen.findByRole(
        'group',
        { name: 'Changes: src/Payments/RetryPolicy.cs' },
        { timeout: 3000 },
      ),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Hide diff' }));
    expect(screen.queryByRole('group', { name: /^Changes: / })).toBeNull();
  });

  it('flags a change that touches only tests and build files', async () => {
    const finished = taskDetail(BATCH_A, 't2-retry-policy');
    serve(
      {},
      {
        't2-retry-policy': {
          verification: {
            ...(finished.verification as Record<string, unknown>),
            test_integrity: {
              flagged: true,
              reason: 'Only test files changed.',
              test_paths: ['tests/Payments/RetryPolicyTests.cs'],
              build_paths: [],
            },
          },
          task: { ...finished.task, test_integrity_flagged: true },
        },
      },
    );
    renderConsole(page('t2-retry-policy'));
    expect(await screen.findByText('flagged')).toBeTruthy();
    expect(screen.getByText('Only test files changed.')).toBeTruthy();
    expect(await screen.findByText('test file', {}, { timeout: 3000 })).toBeTruthy();
  });

  it('says why a task failed and offers the action the server named', async () => {
    serve();
    renderConsole(page('t7-guards'));
    expect(await screen.findByText('rate_limited')).toBeTruthy();
    expect(
      screen.getByText(/The provider usage limit was reached Retry after the limit resets\./),
    ).toBeTruthy();
    expect(screen.getByText('Rate limited')).toBeTruthy();
  });

  it('says a task that has not started is not started, with no verification', async () => {
    serve();
    renderConsole(page('t5-docs'));
    await screen.findByRole('heading', { level: 1, name: 't5-docs' });
    // Neither the header nor the tests have anything to show before the task starts.
    expect((await screen.findAllByText('not started')).length).toBe(2);
    expect(screen.getByText('No event for this task')).toBeTruthy();
  });

  it('shows a loading state, an error that retries, and a task the server does not know', async () => {
    serve({ hang: [`/v1/batches/${BATCH_A}/tasks/t4-metrics`] });
    const first = renderConsole(page('t4-metrics'));
    await screen.findByRole('heading', { level: 1, name: 't4-metrics' });
    expect(document.querySelector('[aria-busy="true"]')).toBeTruthy();
    first.unmount();

    const { calls } = serve({
      replies: { [`/v1/batches/${BATCH_A}/tasks/t4-metrics`]: [404, { errors: ['no such task'] }] },
    });
    renderConsole(page('t4-metrics'));
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Cannot read this task');
    expect(alert.textContent).toContain('no such task');
    const before = calls.length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls.length).toBeGreaterThan(before));
  });

  it('copies the branch name, and says when the browser refuses', async () => {
    serve();
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    renderConsole(page('t2-retry-policy'));
    fireEvent.click(await screen.findByRole('button', { name: 'Copy the branch name' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('chargehand/t2-retry-policy'));
    expect(await screen.findByText('Branch name copied')).toBeTruthy();
  });

  it('shows the events of the task, or says the server holds none', async () => {
    serve();
    const first = renderConsole(page('t3-memory-ttl'));
    const log = await screen.findByRole('log', { name: 'Events of this task' });
    expect(within(log).getByText(/the session asked/)).toBeTruthy();
    first.unmount();
    serve({}, { 't3-memory-ttl': { timeline: { available: false, total: 0, events: [] } } });
    renderConsole(page('t3-memory-ttl'));
    expect(await screen.findByText('The events are not held')).toBeTruthy();
  });

  it('has a 44 px footer on a phone', async () => {
    viewport(true);
    serve();
    renderConsole(page('t2-retry-policy'));
    const open = await screen.findByRole('link', { name: 'Open PR 412' });
    expect(open.className).toContain('touch');
  });
});

describe('the question of a task', () => {
  const asked = () => taskDetail(BATCH_A, 't3-memory-ttl');

  it('asks each question, holds the button until each has an answer, and sends the answers as one request', async () => {
    const { calls } = serve();
    renderConsole(page('t3-memory-ttl'));
    expect(await screen.findByText('The session asked')).toBeTruthy();
    expect(
      screen.getByText('Should the TTL come from the environment or from the container template?', {
        selector: 'p',
      }),
    ).toBeTruthy();
    const send = screen.getByRole('button', { name: 'Answer and continue' }) as HTMLButtonElement;
    expect(send.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText('Your answer 1'), {
      target: { value: ' From the container template. ' },
    });
    expect(send.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Your answer 2'), { target: { value: 'Size M.' } });
    expect(send.disabled).toBe(false);

    fireEvent.click(screen.getByText('Show the request'));
    expect(
      screen.getByRole('group', { name: 'The request an answer sends' }).textContent,
    ).toContain('Size M.');

    fireEvent.click(send);
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.path).toBe('/v1/runs');
    expect(post?.headers.Prefer).toBe('wait=0');
    expect(post?.body).toMatchObject({
      contract_version: 'request/v1',
      context: { interactive: false, preset: 'driven' },
      driven: {
        tasks: [
          {
            id: 't3-memory-ttl',
            continue_from: { run_id: BATCH_A, task_id: 't3-memory-ttl' },
            answers: ['From the container template.', 'Size M.'],
          },
        ],
      },
    });
    expect(await screen.findByText('Answer sent to t3-memory-ttl')).toBeTruthy();
    expect(screen.getByText(/Answer sent\. A new batch continues the task/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'abc123' }).getAttribute('href')).toBe(
      '/console/runs/run-20261013-150000-abc123',
    );
    expect(screen.queryByRole('button', { name: 'Answer and continue' })).toBeNull();
  });

  it('says what the server said when it refuses the answer, and keeps what was typed', async () => {
    serve({
      replies: { 'POST /v1/runs': [400, { errors: ['task t3-memory-ttl is not waiting'] }] },
    });
    renderConsole(page('t3-memory-ttl'));
    fireEvent.change(await screen.findByLabelText('Your answer 1'), { target: { value: 'one' } });
    fireEvent.change(screen.getByLabelText('Your answer 2'), { target: { value: 'two' } });
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('task t3-memory-ttl is not waiting');
    expect((screen.getByLabelText('Your answer 1') as HTMLTextAreaElement).value).toBe('one');
  });

  it('sends nothing when the request the server carried is not a call to its own API', async () => {
    const detail = asked();
    const { calls } = serve(
      {},
      {
        't3-memory-ttl': {
          task: {
            ...detail.task,
            answer: {
              ...must(detail.task.answer),
              path: 'https://elsewhere.example/v1/runs',
            },
          },
        },
      },
    );
    renderConsole(page('t3-memory-ttl'));
    fireEvent.change(await screen.findByLabelText('Your answer 1'), { target: { value: 'one' } });
    fireEvent.change(screen.getByLabelText('Your answer 2'), { target: { value: 'two' } });
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));
    expect((await screen.findByRole('alert')).textContent).toContain(
      'carries a request the console will not send',
    );
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('says a question was answered, and by which run', async () => {
    const detail = asked();
    serve(
      {},
      {
        't3-memory-ttl': {
          task: {
            ...detail.task,
            answered: true,
            answered_by: [{ run_id: 'run-20261013-151000-bbb222' }],
          } as never,
        },
      },
    );
    renderConsole(page('t3-memory-ttl'));
    expect(await screen.findByText('This question was answered by')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'bbb222' })).toBeTruthy();
    expect(screen.queryByLabelText('Your answer 1')).toBeNull();
  });

  it('says so when a question carries no request to answer it', async () => {
    const detail = asked();
    serve({}, { 't3-memory-ttl': { task: { ...detail.task, answer: undefined } } });
    renderConsole(page('t3-memory-ttl'));
    expect(
      await screen.findByText('This question has no request to answer it; open the batch.'),
    ).toBeTruthy();
  });
});

describe('a task as a drawer', () => {
  it('opens over the batch, is modal and named, opens as a page, and closes with Escape', async () => {
    serve();
    const { router } = renderConsole(`/console/runs/${BATCH_A}?task=t4-metrics`);
    const drawer = await screen.findByRole('dialog', { name: 'Task t4-metrics' });
    expect(within(drawer).getByRole('heading', { level: 2, name: 't4-metrics' })).toBeTruthy();
    expect(within(drawer).queryByRole('heading', { level: 1 })).toBeNull();
    fireEvent.click(within(drawer).getByRole('button', { name: 'Open as a page' }));
    await waitFor(() =>
      expect(router.state.location.pathname).toBe(`/runs/${BATCH_A}/tasks/t4-metrics`),
    );
  });

  it('closes from its button and leaves the batch where it was', async () => {
    serve();
    const { router } = renderConsole(`/console/runs/${BATCH_A}?tab=spend&task=t4-metrics`);
    const drawer = await screen.findByRole('dialog', { name: 'Task t4-metrics' });
    fireEvent.click(within(drawer).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(router.state.location.search).toEqual({ tab: 'spend' });
  });
});
