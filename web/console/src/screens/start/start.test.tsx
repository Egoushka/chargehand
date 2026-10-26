import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { SystemBody } from '../../api/system';
import { BATCH_A, me, REPO, system } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { type Scenario, serveApi } from '../../testing/server';
import { HEAD, OTHER_HEAD, OTHER_REPO, repositories, startReplies } from '../../testing/start';
import { viewport } from '../../testing/viewport';
import { cloneAsNewBatch } from './prefill';

const NEW_RUN = 'run-20261013-150000-abc123';

/** Opens Start a batch against the fake server, with the checkouts and the learning report it reads. */
async function open(scenario: Scenario = {}) {
  const api = serveApi({ ...scenario, replies: { ...startReplies(), ...scenario.replies } });
  const view = renderConsole('/console/new');
  await screen.findByRole('heading', { level: 1, name: 'Start a batch' });
  await screen.findByRole('option', { name: /payments-api/ });
  return { api, ...view };
}

const field = (label: string) => screen.getByLabelText(label) as HTMLInputElement;
const type = (label: string, value: string) =>
  fireEvent.change(field(label), { target: { value } });
const typeAt = (label: string, index: number, value: string) =>
  fireEvent.change(screen.getAllByLabelText(label)[index] as HTMLElement, { target: { value } });
const click = (name: string | RegExp) => fireEvent.click(screen.getByRole('button', { name }));
const post = (api: ReturnType<typeof serveApi>) =>
  api.calls.find((c) => c.method === 'POST' && c.path === '/v1/runs');

/** Fills the form so it can be sent: a repository (which brings its commit), a label, the first task's goal and a dollar cap. */
function complete() {
  type('Repository', REPO);
  type('Label', 'Intake aliases');
  fireEvent.change(screen.getAllByLabelText('Goal')[0] as HTMLElement, {
    target: { value: 'Let intake watch a repository by an alias.' },
  });
  type('Dollar cap', '12');
}

const owner = (profile: unknown, presets: unknown[] = []) =>
  ({ ...system(), presets, profile }) as unknown as SystemBody;

describe('Start a batch: the form', () => {
  it('takes the place of the placeholder, with every part of the screen', async () => {
    await open();
    for (const name of [
      'Request',
      'Tasks',
      'Options and caps',
      'Dependencies',
      'Estimate',
      'Your limits',
    ])
      expect(screen.getByRole('heading', { name })).toBeTruthy();
    expect(screen.getByRole('group', { name: 'View' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Templates' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Check' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start batch' })).toBeTruthy();
    expect(screen.queryByText(/is not built yet/i)).toBeNull();
  });

  it('lists the preset the account may use, and the checkouts the server reports, with what was detected', async () => {
    await open();
    expect(
      within(field('Preset'))
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['default', 'driven', 'draft']);
    expect(field('Preset').value).toBe('driven');
    const options = within(field('Repository'))
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(options).toEqual([
      'Choose a repository',
      'payments-api (example/payments-api)',
      'billing-worker (example/billing-worker)',
    ]);
    type('Repository', OTHER_REPO);
    expect(screen.getByText('Detected: node, python.')).toBeTruthy();
  });

  it('fills the commit with the head of the repository it chose, and follows a change of repository until the commit is typed over', async () => {
    await open();
    expect(field('Commit').value).toBe('');
    type('Repository', REPO);
    expect(field('Commit').value).toBe(HEAD);
    expect(screen.getByText('HEAD of main is', { exact: false })).toBeTruthy();
    type('Repository', OTHER_REPO);
    expect(field('Commit').value).toBe(OTHER_HEAD);
    type('Commit', '0c5e921');
    type('Repository', REPO);
    expect(field('Commit').value).toBe('0c5e921');
    fireEvent.click(screen.getByRole('button', { name: HEAD.slice(0, 12) }));
    expect(field('Commit').value).toBe(HEAD);
  });

  it('chooses the only repository by itself, with its head', async () => {
    const [first] = repositories().repositories;
    await open({
      replies: { '/v1/repositories': [200, { ...repositories(), repositories: [first] }] },
    });
    await waitFor(() => expect(field('Repository').value).toBe(REPO));
    expect(field('Commit').value).toBe(HEAD);
  });

  it('adds, removes and reorders tasks', async () => {
    await open();
    expect((screen.getAllByLabelText('Task id') as HTMLInputElement[]).map((i) => i.value)).toEqual(
      ['task-1'],
    );
    click('Add task');
    click('Add task');
    const ids = () =>
      (screen.getAllByLabelText('Task id') as HTMLInputElement[]).map((i) => i.value);
    expect(ids()).toEqual(['task-1', 'task-2', 'task-3']);
    expect(screen.getByRole('heading', { name: 'Tasks' }).parentElement?.textContent).toContain(
      '3',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Move task-3 up' }));
    expect(ids()).toEqual(['task-1', 'task-3', 'task-2']);
    expect(
      (screen.getByRole('button', { name: 'Move task-1 up' }) as HTMLButtonElement).disabled,
    ).toBe(true);
    expect(
      (screen.getByRole('button', { name: 'Move task-2 down' }) as HTMLButtonElement).disabled,
    ).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Remove task-3' }));
    expect(ids()).toEqual(['task-1', 'task-2']);
    fireEvent.click(screen.getByRole('button', { name: 'Remove task-1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove task-2' }));
    expect(screen.getByRole('heading', { name: 'No tasks yet' })).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Add task' })[0] as HTMLElement);
    expect(ids()).toEqual(['task-1']);
  });

  it('stops at 20 tasks', async () => {
    await open();
    for (let n = 1; n < 20; n++) click('Add task');
    expect(screen.getAllByLabelText('Task id')).toHaveLength(20);
    expect(
      (screen.getAllByRole('button', { name: 'Add task' })[0] as HTMLButtonElement).disabled,
    ).toBe(true);
  }, 20_000);

  it('sets a size, a budget and a multi-line goal on a task', async () => {
    await open();
    fireEvent.click(screen.getByRole('button', { name: 'L' }));
    expect(screen.getByRole('button', { name: 'L' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Auto' }));
    expect(screen.getByRole('button', { name: 'L' }).getAttribute('aria-pressed')).toBe('false');
    type('Budget, USD (optional)', '2.5');
    expect(screen.getByText(/Budget \$2\.50 · the task's own budget_usd/)).toBeTruthy();
    expect(field('Goal').tagName).toBe('TEXTAREA');
  });
});

describe('Start a batch: repository readiness', () => {
  const below = (path: string) => ({
    score: 34,
    threshold: 60,
    below: true,
    signals: [],
    gaps: ['No CI configuration: add a workflow that builds and tests every pull request.'],
    proposal: {
      contract_version: 'request/v1',
      text: 'Make billing-worker agent-ready',
      context: { repository: { path } },
      driven: { tasks: [{ id: 'agent-ready', goal: 'Make it ready.\n- No CI.', size: 'M' }] },
    },
  });
  const withReadiness = () => {
    const body = repositories();
    return {
      ...body,
      repositories: body.repositories.map((r) =>
        r.path === OTHER_REPO ? { ...r, readiness: below(OTHER_REPO) } : r,
      ),
    };
  };

  it('warns about a repository under the threshold, lists its gaps, and still lets the batch start', async () => {
    await open({ replies: { '/v1/repositories': [200, withReadiness()] } });
    type('Repository', REPO);
    expect(screen.queryByText(/for agent readiness/)).toBeNull();
    type('Repository', OTHER_REPO);
    expect(screen.getByText('billing-worker scores 34 of 100 for agent readiness')).toBeTruthy();
    expect(screen.getByText(/No CI configuration/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start batch' })).toHaveProperty('disabled', false);
  });

  it('swaps in the proposed agent-ready task, and the warning goes', async () => {
    await open({ replies: { '/v1/repositories': [200, withReadiness()] } });
    type('Repository', OTHER_REPO);
    click('Use the agent-ready task instead');
    expect(field('Label').value).toBe('Make billing-worker agent-ready');
    expect((screen.getAllByLabelText('Goal')[0] as HTMLTextAreaElement).value).toBe(
      'Make it ready.\n- No CI.',
    );
    expect(field('Commit').value).toBe(OTHER_HEAD);
    expect(screen.queryByText(/for agent readiness/)).toBeNull();
  });
});

describe('Start a batch: dependencies', () => {
  it('chooses a dependency from the other tasks, draws it, and removes it again', async () => {
    await open();
    click('Add task');
    click('Add task');
    const add = screen.getByRole('combobox', { name: 'Add a dependency to task-3' });
    expect(
      within(add)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Add dependency', 'task-1', 'task-2']);
    fireEvent.change(add, { target: { value: 'task-1' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Add a dependency to task-2' }), {
      target: { value: 'task-1' },
    });
    const graph = screen.getByRole('group', {
      name: /^Dependency graph: 3 tasks, 2 dependencies, 2 steps/,
    });
    expect(graph.querySelectorAll('path.ch-edge')).toHaveLength(2);
    expect(within(graph).getByText('task-3')).toBeTruthy();
    expect(screen.getByText('2 tasks in a row')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Remove dependency task-1 from task-3' }));
    expect(graph.querySelectorAll('path.ch-edge')).toHaveLength(1);
    // The one that is left follows its task when the id changes.
    typeAt('Task id', 0, 'first');
    expect(
      screen.getByRole('button', { name: 'Remove dependency first from task-2' }),
    ).toBeTruthy();
  });

  it('does not offer a dependency that would close a loop, and takes a removed task out of what waited for it', async () => {
    await open();
    click('Add task');
    fireEvent.change(screen.getByRole('combobox', { name: 'Add a dependency to task-2' }), {
      target: { value: 'task-1' },
    });
    const forTask1 = screen.getByRole('combobox', { name: 'Add a dependency to task-1' });
    const option = within(forTask1).getByRole('option', {
      name: 'task-2 (would make a cycle)',
    }) as HTMLOptionElement;
    expect(option.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Remove task-1' }));
    expect(screen.queryByRole('button', { name: /Remove dependency/ })).toBeNull();
  });

  it('says so at the tasks, and under the graph, when a pasted request loops', async () => {
    await open();
    click('JSON');
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: {
        value: JSON.stringify({
          text: 'x',
          driven: {
            tasks: [
              { id: 'a', goal: 'g', depends_on: ['b'] },
              { id: 'b', goal: 'g', depends_on: ['a'] },
            ],
          },
        }),
      },
    });
    expect(
      screen.getAllByText(/Tasks depend on each other in a cycle: a → b → a\./).length,
    ).toBeGreaterThanOrEqual(2);
    click('Form');
    expect(screen.getAllByText(/Tasks depend on each other in a cycle: a → b → a\./)).toHaveLength(
      2,
    );
    expect(screen.getByText(/These tasks wait for each other: a → b → a\./)).toBeTruthy();
    expect(screen.getByRole('group', { name: /Dependency graph: 2 tasks/ })).toBeTruthy();
  });
});

describe('Start a batch: the estimate', () => {
  it('gives each task a budget from history, and says where the number came from', async () => {
    await open();
    type('Repository', REPO);
    // M: 14 past tasks here and 12 in another repository: only pooled do they pass the minimum of 20.
    expect(
      screen.getByText(/Budget ≈ \$0\.95 · about p80 of 26 past M tasks in 2 repositories/),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'S' }));
    expect(
      screen.getByText(/Budget ≈ \$0\.35 · p80 of 24 past S tasks in this repository/),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'L' }));
    expect(
      screen.getByText(
        /Budget \$10\.00 · the flat L budget; only 6 past L tasks, the server needs 20/,
      ),
    ).toBeTruthy();
  });

  it('adds up the budgets, compares them with the dollar cap, and lists the sources', async () => {
    await open();
    type('Repository', REPO);
    fireEvent.click(screen.getByRole('button', { name: 'S' }));
    click('Add task');
    fireEvent.click(screen.getAllByRole('button', { name: 'L' })[1] as HTMLElement);
    const estimate = within(
      screen.getByRole('heading', { name: 'Estimate' }).closest('section') as HTMLElement,
    );
    expect(estimate.getByText('$10.35')).toBeTruthy();
    expect(estimate.getByText('All independent')).toBeTruthy();
    expect(estimate.getByText('2 sessions')).toBeTruthy();
    expect(estimate.getByText('p80 of 24 past S tasks in this repository')).toBeTruthy();
    expect(estimate.getByText(/the flat L budget/)).toBeTruthy();
    type('Dollar cap', '8');
    expect(
      estimate.getByText(/more than the cap of \$8\.00: the cap stops the batch first/),
    ).toBeTruthy();
    type('Dollar cap', '20');
    expect(estimate.getByText('The budgets fit under the cap of $20.00.')).toBeTruthy();
  });

  it('uses the owner’s own thresholds and flat budgets, and says nothing about defaults', async () => {
    await open({
      replies: {
        '/v1/system': [
          200,
          owner(
            {
              driven: {
                max_parallel: 2,
                governor: {
                  mode: 'observe',
                  budgets: { S: 2, M: 5, L: 12 },
                  history: { min_tasks: 5, window_days: 30, percentile: 80 },
                },
              },
            },
            [{ name: 'driven', driven: true, approval: false, allowed_actions: [] }],
          ),
        ],
      },
    });
    await screen.findByText('Governor: observe, set in the profile.');
    fireEvent.click(screen.getByRole('button', { name: 'L' }));
    type('Repository', REPO);
    // Six past L tasks pass the owner's minimum of 5.
    expect(
      screen.getByText(/Budget ≈ \$3\.10 · p80 of 6 past L tasks in this repository/),
    ).toBeTruthy();
    expect(screen.queryByText(/are the server's defaults/)).toBeNull();
    expect(screen.getByText(/trusted from 5 tasks/)).toBeTruthy();
    expect(screen.getByText(/and runs at most 2/)).toBeTruthy();
    expect(
      within(field('Preset'))
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['driven']);
  });

  it('falls back on the flat budgets and says so when the history cannot be read', async () => {
    await open({ replies: { '/v1/learn': [500, { errors: ['boom'] }] } });
    expect(
      await screen.findByText(/The history could not be read \(GET \/v1\/learn\)/),
    ).toBeTruthy();
    expect(screen.getByText(/Budget \$4\.00 · the flat M budget/)).toBeTruthy();
  });
});

describe('Start a batch: the checks', () => {
  it('shows nothing red on an empty form, and every problem at its field once a start is tried', async () => {
    const { api } = await open();
    expect(screen.queryByRole('alert')).toBeNull();
    click('Start batch');

    const summary = await screen.findByText(/things to fix before this can start/);
    expect(summary.textContent).toBe('5 things to fix before this can start');
    expect(field('Label').getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(field('Label'));
    expect(
      screen.getByText('The request needs text: a label that names the batch in lists.', {
        selector: '.ch-error',
      }),
    ).toBeTruthy();
    expect(field('Repository').getAttribute('aria-invalid')).toBe('true');
    expect(field('Commit').getAttribute('aria-invalid')).toBe('true');
    expect(field('Goal').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getAllByText(/Set a token cap \(a subscription\) or a dollar cap/)).toHaveLength(
      2,
    );
    expect(post(api)).toBeUndefined();

    // Each problem in the list takes the focus to its field.
    fireEvent.click(screen.getByRole('button', { name: 'Task 1 goal' }));
    expect(document.activeElement).toBe(field('Goal'));
    // A problem goes when it is fixed.
    type('Label', 'Intake aliases');
    expect(field('Label').getAttribute('aria-invalid')).toBeNull();
    expect(screen.getByText(/4 things to fix/)).toBeTruthy();
  });

  it('checks a task id as it is typed, and a duplicate at both tasks', async () => {
    await open();
    click('Add task');
    typeAt('Task id', 0, 'Bad Id');
    expect(screen.getByText(/An id uses lowercase letters, digits/)).toBeTruthy();
    typeAt('Task id', 0, 'task-2');
    expect(screen.getAllByText('Another task uses the id task-2.')).toHaveLength(2);
  });

  it('checks the caps at their fields', async () => {
    await open();
    type('max_parallel', '40');
    type('Token cap (input plus output)', 'lots');
    type('Dollar cap', '-1');
    expect(screen.getByText('Use a whole number from 1 to 16.')).toBeTruthy();
    expect(screen.getByText(/Use a whole number of tokens above 0/)).toBeTruthy();
    expect(screen.getByText('Use a dollar amount above 0, such as 12.00.')).toBeTruthy();
    type('max_parallel', '3');
    type('Token cap (input plus output)', '2m');
    type('Dollar cap', '');
    expect(screen.queryByText('Use a whole number from 1 to 16.')).toBeNull();
    expect(screen.queryByText(/Use a whole number of tokens above 0/)).toBeNull();
  });

  it('Check passes a complete request and says so, and fails an incomplete one at its field', async () => {
    const { api } = await open();
    click('Check');
    expect(await screen.findByText(/things to fix before this can start/)).toBeTruthy();
    complete();
    click('Check');
    expect(await screen.findByText('No problems found')).toBeTruthy();
    expect(screen.queryByText(/things to fix/)).toBeNull();
    expect(post(api)).toBeUndefined();
    type('Label', 'changed');
    expect(screen.queryByText('No problems found')).toBeNull();
  });

  it('warns about a cap above what the account has left, without stopping the start', async () => {
    await open({
      me: me({ name: 'ci-bot', owner: false, limits: { max_usd_per_day: 40 } }),
    });
    type('Dollar cap', '20');
    expect(
      screen.getByText('The account has $13.86 left. The server cuts the cap to that.'),
    ).toBeTruthy();
    expect(
      (screen.getByRole('button', { name: 'Start batch' }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });
});

describe('Start a batch: the account', () => {
  it('shows what the account may do and what is left of its caps', async () => {
    await open({
      me: me({
        name: 'ci-bot',
        owner: false,
        repositories: ['/srv/work'],
        limits: { max_usd_per_day: 40, max_tokens_per_month: 100_000_000, max_concurrent_runs: 5 },
      }),
    });
    const panel = within(
      screen.getByRole('heading', { name: 'Your limits' }).closest('section') as HTMLElement,
    );
    expect(panel.getByText('ci-bot')).toBeTruthy();
    expect(panel.getByText('Allowed')).toBeTruthy();
    expect(panel.getByText('1 allowed')).toBeTruthy();
    expect(panel.getByText('$13.86 left of $40.00')).toBeTruthy();
    expect(panel.getByText('38M left of 100M')).toBeTruthy();
    expect(panel.getByText('2 of 5')).toBeTruthy();
  });

  it('does not read the profile for an account, which is refused it', async () => {
    const { api } = await open({ me: me({ name: 'ci-bot', owner: false }) });
    expect(api.calls.some((c) => c.path === '/v1/system')).toBe(false);
    expect(screen.queryByText(/Governor:/)).toBeNull();
  });

  it('says an account that may not run batches cannot, and disables the start', async () => {
    await open({ me: me({ name: 'ci-bot', owner: false, driven: false }) });
    expect(
      screen.getByText('Account ci-bot may not start a batch. Ask the owner to allow driven runs.'),
    ).toBeTruthy();
    expect(
      (screen.getByRole('button', { name: 'Start batch' }) as HTMLButtonElement).disabled,
    ).toBe(true);
    expect(screen.getByText('Not allowed for this account')).toBeTruthy();
  });

  it('disables the start while the server is halted', async () => {
    await open({ me: me({ halted: true }) });
    expect(
      screen.getByText(/The server is halted\. It refuses new runs until the owner resumes it\./),
    ).toBeTruthy();
    expect(
      (screen.getByRole('button', { name: 'Start batch' }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('warns that an account at its concurrency limit will be refused', async () => {
    await open({
      me: me({
        owner: false,
        name: 'ci-bot',
        limits: { max_concurrent_runs: 2 },
        unfinished_runs: 2,
      }),
    });
    expect(
      screen.getByText(/unfinished runs, its limit\. The server answers 429 until one ends\./),
    ).toBeTruthy();
  });
});

describe('Start a batch: sending it', () => {
  it('posts the request with Prefer wait=0, then opens the new run', async () => {
    const { api, router } = await open();
    complete();
    fireEvent.click(screen.getByRole('button', { name: 'Plan first' }));
    type('max_parallel', '3');
    click('Add task');
    fireEvent.change(screen.getAllByLabelText('Goal')[1] as HTMLElement, {
      target: { value: 'Document aliases.\nMention the path form.' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Add a dependency to task-2' }), {
      target: { value: 'task-1' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'S' })[0] as HTMLElement);
    click('Start batch');

    await waitFor(() => expect(router.state.location.pathname).toBe(`/runs/${NEW_RUN}`));
    const sent = post(api);
    expect(sent?.path).toBe('/v1/runs');
    expect(sent?.headers.Prefer).toBe('wait=0');
    expect(sent?.body).toEqual({
      contract_version: 'request/v1',
      text: 'Intake aliases',
      context: { interactive: false, preset: 'driven', repository: { path: REPO, commit: HEAD } },
      driven: {
        plan: true,
        max_parallel: 3,
        max_usd_total: 12,
        draft_pr: true,
        tasks: [
          { id: 'task-1', goal: 'Let intake watch a repository by an alias.', size: 'S' },
          {
            id: 'task-2',
            goal: 'Document aliases.\nMention the path form.',
            size: 'M',
            depends_on: ['task-1'],
          },
        ],
      },
    });
    expect(await screen.findByText('Batch accepted')).toBeTruthy();
  });

  it('shows the server’s refusal with its status and code, and keeps the form', async () => {
    const { router } = await open({
      replies: {
        'POST /v1/runs': [
          403,
          {
            errors: ['account ci-bot may not use the repository /srv/work/payments-api'],
            error: { code: 'repository_not_allowed' },
          },
        ],
      },
    });
    complete();
    click('Start batch');
    const alert = await screen.findByText('The server refused the request');
    expect(alert.closest('[role="alert"]')?.textContent).toContain('403 repository_not_allowed');
    expect(alert.closest('[role="alert"]')?.textContent).toContain(
      'account ci-bot may not use the repository',
    );
    expect(router.state.location.pathname).toBe('/new');
    expect(field('Label').value).toBe('Intake aliases');
  });

  it('shows a 400’s lines, and a 503 from a halted server', async () => {
    await open({
      replies: {
        'POST /v1/runs': [400, { errors: ['driven.tasks[0].id is invalid', 'second line'] }],
      },
    });
    complete();
    click('Start batch');
    const alert = (await screen.findByText('The server refused the request')).closest(
      '[role="alert"]',
    );
    expect(alert?.textContent).toContain('400');
    expect(alert?.textContent).toContain('driven.tasks[0].id is invalid; second line');
  });

  it('says it could not reach the server when the call fails', async () => {
    const api = serveApi({ replies: startReplies() });
    renderConsole('/console/new');
    await screen.findByRole('option', { name: /payments-api/ });
    complete();
    const real = api.mock.getMockImplementation();
    api.mock.mockImplementation((input: string, init?: RequestInit) =>
      init?.method === 'POST'
        ? Promise.reject(new TypeError('Failed to fetch'))
        : (real as typeof fetch)(input, init),
    );
    click('Start batch');
    expect(await screen.findByText('Could not reach the server')).toBeTruthy();
  });

  it('sends nothing while the form has errors', async () => {
    const { api } = await open();
    click('Start batch');
    await screen.findByText(/things to fix/);
    expect(post(api)).toBeUndefined();
  });
});

describe('Start a batch: the JSON view', () => {
  it('shows the request the form makes, and a form change shows in it', async () => {
    await open();
    complete();
    click('JSON');
    const json = JSON.parse((screen.getByLabelText('Request JSON') as HTMLTextAreaElement).value);
    expect(json).toMatchObject({
      contract_version: 'request/v1',
      text: 'Intake aliases',
      context: { repository: { path: REPO, commit: HEAD } },
      driven: { max_usd_total: 12, draft_pr: true, tasks: [{ id: 'task-1' }] },
    });
    expect(screen.getByText('The same request from the shell')).toBeTruthy();
  });

  it('round-trips: JSON typed there fills the form, and the form’s JSON is the same JSON', async () => {
    await open();
    click('JSON');
    const request = {
      contract_version: 'request/v1',
      text: 'From JSON',
      context: {
        interactive: false,
        preset: 'driven',
        repository: { path: OTHER_REPO, commit: OTHER_HEAD },
      },
      driven: {
        plan: false,
        max_parallel: 2,
        max_tokens_total: 1_500_000,
        draft_pr: true,
        tasks: [
          { id: 'a', goal: 'first', size: 'S', budget_usd: 1.25 },
          { id: 'b', goal: 'second', depends_on: ['a'] },
        ],
      },
    };
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: { value: JSON.stringify(request) },
    });
    click('Form');
    expect(field('Label').value).toBe('From JSON');
    expect(field('Repository').value).toBe(OTHER_REPO);
    expect(field('Commit').value).toBe(OTHER_HEAD);
    expect(field('max_parallel').value).toBe('2');
    expect(field('Token cap (input plus output)').value).toBe('1500000');
    expect(screen.getByRole('button', { name: 'Skip' }).getAttribute('aria-pressed')).toBe('true');
    expect((screen.getAllByLabelText('Task id') as HTMLInputElement[]).map((i) => i.value)).toEqual(
      ['a', 'b'],
    );
    expect(screen.getByRole('button', { name: 'Remove dependency a from b' })).toBeTruthy();
    expect(
      (screen.getAllByLabelText('Budget, USD (optional)') as HTMLInputElement[])[0]?.value,
    ).toBe('1.25');

    click('JSON');
    expect(
      JSON.parse((screen.getByLabelText('Request JSON') as HTMLTextAreaElement).value),
    ).toEqual(request);
  });

  it('keeps what the form cannot show, and sends it', async () => {
    const { api } = await open();
    click('JSON');
    const request = {
      contract_version: 'request/v1',
      text: 'Review round',
      inputs: [{ id: 'i', kind: 'note', text: 'x' }],
      context: {
        interactive: false,
        preset: 'driven',
        verify: ['sh', '-c', 'npm test'],
        repository: { path: REPO, commit: HEAD },
      },
      driven: {
        max_usd_total: 5,
        draft_pr: true,
        tasks: [{ id: 'a', ref: 'ISSUE-1', max_turns: 30 }],
      },
    };
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: { value: JSON.stringify(request) },
    });
    click('Start batch');
    await waitFor(() => expect(post(api)).toBeDefined());
    expect(post(api)?.body).toMatchObject({
      inputs: request.inputs,
      context: { verify: ['sh', '-c', 'npm test'] },
      driven: { tasks: [{ id: 'a', ref: 'ISSUE-1', max_turns: 30 }] },
    });
  });

  it('says what is wrong with JSON that does not parse, keeps the last good form, and will not leave until it parses', async () => {
    await open();
    type('Label', 'Kept');
    click('JSON');
    fireEvent.change(screen.getByLabelText('Request JSON'), { target: { value: '{ "text": ' } });
    const alert = screen.getByText(/The form keeps the last JSON that parsed\./);
    expect(alert.textContent).toMatch(/\.\s+The form keeps/);
    expect(screen.getByLabelText('Request JSON').getAttribute('aria-invalid')).toBe('true');
    click('Form');
    expect(screen.getByLabelText('Request JSON')).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText('Request JSON'));
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: { value: '{"text":"Fixed"}' },
    });
    click('Form');
    expect(field('Label').value).toBe('Fixed');
  });

  it('lists every problem in the JSON view and will not send a request with errors', async () => {
    const { api } = await open();
    click('JSON');
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: {
        value: JSON.stringify({ text: 'x', driven: { draft_pr: false, tasks: [{ id: 'a' }] } }),
      },
    });
    const checks = within(
      screen.getByRole('heading', { name: 'Checks' }).closest('section') as HTMLElement,
    );
    expect(
      checks.getByText(
        'draft_pr can only be true: pull requests always open as drafts, so it is sent as true.',
      ),
    ).toBeTruthy();
    expect(checks.getByText('Describe what the change should do.')).toBeTruthy();
    click('Start batch');
    expect(post(api)).toBeUndefined();
    expect(document.activeElement).toBe(screen.getByLabelText('Request JSON'));
  });

  it('copies the JSON, and says when the browser would not', async () => {
    await open();
    click('JSON');
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    click('Copy JSON');
    await screen.findByText('JSON copied');
    expect(writeText).toHaveBeenCalledWith(
      (screen.getByLabelText('Request JSON') as HTMLTextAreaElement).value,
    );
    writeText.mockRejectedValue(new Error('denied'));
    click('Copy JSON');
    expect(await screen.findByText('Could not copy')).toBeTruthy();
  });
});

describe('Start a batch: tasks from pasted text', () => {
  const dialog = () => within(screen.getByRole('dialog', { name: 'Import tasks' }));

  it('adds one task per pasted line with the size it names, and leaves out GitHub issues, saying why', async () => {
    await open();
    click('Import tasks');
    expect(dialog().getByText(/the server has no route that lists them/)).toBeTruthy();
    expect(
      (dialog().getByRole('button', { name: 'Add 0 tasks' }) as HTMLButtonElement).disabled,
    ).toBe(true);
    fireEvent.change(dialog().getByLabelText('Tasks'), {
      target: {
        value:
          '[S] Let intake watch a repository by an alias\n- (L) Report peak memory per container\nplain line',
      },
    });
    expect(dialog().getByText('3 tasks found.')).toBeTruthy();
    fireEvent.click(dialog().getByRole('button', { name: 'Add 3 tasks' }));
    expect(screen.queryByRole('dialog', { name: 'Import tasks' })).toBeNull();
    expect((screen.getAllByLabelText('Task id') as HTMLInputElement[]).map((i) => i.value)).toEqual(
      [
        'task-1',
        'let-intake-watch-a-repository-by-an',
        'report-peak-memory-per-container',
        'plain-line',
      ],
    );
    expect(
      screen.getByRole('button', { name: 'Remove report-peak-memory-per-container' }),
    ).toBeTruthy();
  });

  it('reads a JSON list, reports JSON that does not parse, and counts what does not fit', async () => {
    await open();
    click('Import tasks');
    fireEvent.change(dialog().getByLabelText('Tasks'), {
      target: { value: '["a goal", {"id":"x","goal":"another"}' },
    });
    expect(dialog().getByText(/This looks like JSON but does not parse/)).toBeTruthy();
    fireEvent.change(dialog().getByLabelText('Tasks'), {
      target: { value: '["a goal", {"id":"x","goal":"another"}]' },
    });
    expect(dialog().getByText('2 tasks found.')).toBeTruthy();
    const many = Array.from({ length: 25 }, (_, i) => `task about thing ${i}`).join('\n');
    fireEvent.change(dialog().getByLabelText('Tasks'), { target: { value: many } });
    expect(
      dialog().getByText(
        '19 tasks found. 6 more would pass the limit of 20 tasks and are left out.',
      ),
    ).toBeTruthy();
  });
});

describe('Start a batch: templates', () => {
  const KEY = 'chargehand.console.start-templates';
  const dialog = () => within(screen.getByRole('dialog', { name: 'Templates' }));

  it('saves the form as a named template, in this browser, without its commit', async () => {
    await open();
    complete();
    click('Templates');
    expect(dialog().getByText('No templates yet')).toBeTruthy();
    expect(
      (dialog().getByRole('button', { name: 'Save template' }) as HTMLButtonElement).disabled,
    ).toBe(true);
    fireEvent.change(dialog().getByLabelText('Save the form as a template'), {
      target: { value: 'Nightly backlog' },
    });
    fireEvent.click(dialog().getByRole('button', { name: 'Save template' }));
    expect(await dialog().findByText('Nightly backlog')).toBeTruthy();
    const stored = JSON.parse(localStorage.getItem(KEY) ?? '[]');
    expect(stored).toHaveLength(1);
    expect(stored[0].name).toBe('Nightly backlog');
    expect(stored[0].request.context.repository).toEqual({ path: REPO });
    expect(stored[0].request.driven.tasks[0].goal).toBe(
      'Let intake watch a repository by an alias.',
    );
  });

  it('loads a template into the form, with the repository’s head as the commit, and deletes one', async () => {
    localStorage.setItem(
      KEY,
      JSON.stringify([
        {
          name: 'Docs sweep',
          savedAt: Date.now(),
          request: {
            text: 'Docs sweep',
            context: { preset: 'driven', repository: { path: OTHER_REPO } },
            driven: {
              max_tokens_total: 1_000_000,
              plan: false,
              tasks: [{ id: 'docs', goal: 'Fix docs', size: 'S' }],
            },
          },
        },
      ]),
    );
    await open();
    type('Label', 'About to be replaced');
    click('Templates');
    fireEvent.click(dialog().getByRole('button', { name: 'Load' }));
    expect(screen.queryByRole('dialog', { name: 'Templates' })).toBeNull();
    expect(field('Label').value).toBe('Docs sweep');
    expect(field('Repository').value).toBe(OTHER_REPO);
    expect(field('Commit').value).toBe(OTHER_HEAD);
    expect(field('Task id').value).toBe('docs');
    expect(field('Token cap (input plus output)').value).toBe('1000000');
    expect(await screen.findByText('Template Docs sweep loaded')).toBeTruthy();

    click('Templates');
    fireEvent.click(dialog().getByRole('button', { name: 'Delete template Docs sweep' }));
    expect(dialog().getByText('No templates yet')).toBeTruthy();
    expect(JSON.parse(localStorage.getItem(KEY) ?? 'null')).toEqual([]);
  });

  it('says so when the browser will not keep a template, and works with storage that throws', async () => {
    await open();
    const blocked = () => {
      throw new Error('blocked');
    };
    vi.stubGlobal('localStorage', { getItem: blocked, setItem: blocked, removeItem: blocked });
    click('Templates');
    expect(dialog().getByText('No templates yet')).toBeTruthy();
    fireEvent.change(dialog().getByLabelText('Save the form as a template'), {
      target: { value: 'x' },
    });
    fireEvent.click(dialog().getByRole('button', { name: 'Save template' }));
    expect(
      await dialog().findByText('This browser would not keep it: site storage is blocked or full.'),
    ).toBeTruthy();
  });
});

describe('Start a batch: cloned from another batch', () => {
  const request = {
    contract_version: 'request/v1',
    text: 'Copy of the nightly batch',
    context: { preset: 'driven', repository: { path: REPO, commit: HEAD } },
    driven: {
      max_parallel: 2,
      max_usd_total: 15,
      tasks: [
        { id: 't1-schema', goal: 'Change the schema.', size: 'M' },
        { id: 't2-docs', goal: 'Document it.', size: 'S', depends_on: ['t1-schema'] },
      ],
    },
  };

  it('fills the form from the request in the router’s state, says where it came from, and sends nothing', async () => {
    const { api, router } = await open();
    await act(async () => {
      await router.navigate(cloneAsNewBatch(request, BATCH_A));
    });
    expect(await screen.findByText('Cloned from batch 1fdf01')).toBeTruthy();
    expect(field('Label').value).toBe('Copy of the nightly batch');
    expect(field('Repository').value).toBe(REPO);
    expect(field('Commit').value).toBe(HEAD);
    expect(field('max_parallel').value).toBe('2');
    expect(field('Dollar cap').value).toBe('15');
    expect((screen.getAllByLabelText('Task id') as HTMLInputElement[]).map((i) => i.value)).toEqual(
      ['t1-schema', 't2-docs'],
    );
    expect(
      screen.getByRole('button', { name: 'Remove dependency t1-schema from t2-docs' }),
    ).toBeTruthy();
    expect(post(api)).toBeUndefined();

    // What is sent is the clone, as a new request.
    type('Label', 'Edited copy');
    click('Start batch');
    await waitFor(() => expect(post(api)).toBeDefined());
    expect(post(api)?.body).toMatchObject({
      text: 'Edited copy',
      context: { interactive: false, repository: { path: REPO, commit: HEAD } },
      driven: {
        max_usd_total: 15,
        draft_pr: true,
        tasks: [{ id: 't1-schema' }, { id: 't2-docs', depends_on: ['t1-schema'] }],
      },
    });
  });

  it('starts over from a second clone, and opens empty from the rail', async () => {
    const { router } = await open();
    await act(async () => {
      await router.navigate(cloneAsNewBatch(request, BATCH_A));
    });
    await screen.findByText('Cloned from batch 1fdf01');
    await act(async () => {
      await router.navigate(
        cloneAsNewBatch({ ...request, text: 'Another' }, 'run-20261013-124402-77bd0a'),
      );
    });
    expect(await screen.findByText('Cloned from batch 77bd0a')).toBeTruthy();
    expect(field('Label').value).toBe('Another');
    await act(async () => {
      await router.navigate({ to: '/new' });
    });
    await waitFor(() => expect(screen.queryByText(/Cloned from batch/)).toBeNull());
    expect(field('Label').value).toBe('');
  });

  it('copes with a clone that is only part of a request, or not a request at all', async () => {
    const { router } = await open();
    await act(async () => {
      await router.navigate(
        cloneAsNewBatch({ driven: { tasks: [{ id: 'only', goal: 'g', size: 'XL' }] } }),
      );
    });
    await waitFor(() => expect(field('Task id').value).toBe('only'));
    expect(field('Repository').value).toBe('');
    expect(screen.queryByText(/Cloned from batch/)).toBeNull();
    await act(async () => {
      await router.navigate(cloneAsNewBatch('garbage'));
    });
    expect(await screen.findByText('Part of this request could not be carried over')).toBeTruthy();
    expect(
      screen.getByText('The request was not a JSON object, so the form starts empty.'),
    ).toBeTruthy();
    expect(field('Task id').value).toBe('task-1');
  });
});

describe('Start a batch: every state', () => {
  it('shows a skeleton while the account loads', async () => {
    serveApi({ replies: startReplies(), hang: ['/v1/accounts/me'] });
    renderConsole('/console/new');
    expect(await screen.findByRole('heading', { level: 1, name: 'Start a batch' })).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
  });

  it('says which call failed when the account cannot be read, and retries', async () => {
    const api = serveApi({
      replies: { ...startReplies(), '/v1/accounts/me': [500, { errors: ['down'] }] },
    });
    renderConsole('/console/new');
    expect(await screen.findByText('Cannot read your account')).toBeTruthy();
    expect(screen.getByText('GET /v1/accounts/me')).toBeTruthy();
    const before = api.calls.filter((c) => c.path === '/v1/accounts/me').length;
    click('Retry');
    await waitFor(() =>
      expect(api.calls.filter((c) => c.path === '/v1/accounts/me').length).toBeGreaterThan(before),
    );
  });

  it('falls back on a typed repository path when the checkouts cannot be listed, and says why', async () => {
    const api = serveApi({
      replies: { ...startReplies(), '/v1/repositories': [500, { errors: ['no roots'] }] },
    });
    renderConsole('/console/new');
    expect(await screen.findByText('Cannot list the repositories')).toBeTruthy();
    expect(screen.getByText('GET /v1/repositories')).toBeTruthy();
    expect(field('Repository').tagName).toBe('INPUT');
    type('Repository', REPO);
    type('Commit', 'a91c0ff');
    expect(field('Repository').value).toBe(REPO);
    const before = api.calls.filter((c) => c.path === '/v1/repositories').length;
    click('Retry');
    await waitFor(() =>
      expect(api.calls.filter((c) => c.path === '/v1/repositories').length).toBeGreaterThan(before),
    );
  });

  it('shows a skeleton in place of the repository while the checkouts load', async () => {
    serveApi({ replies: startReplies(), hang: ['/v1/repositories'] });
    renderConsole('/console/new');
    await screen.findByRole('heading', { level: 1, name: 'Start a batch' });
    await waitFor(() => expect(screen.getByText('Repository')).toBeTruthy());
    expect(screen.queryByRole('combobox', { name: 'Repository' })).toBeNull();
  });

  it('warns about a repository path that the server did not list', async () => {
    await open();
    click('JSON');
    fireEvent.change(screen.getByLabelText('Request JSON'), {
      target: {
        value: JSON.stringify({
          text: 'x',
          context: { repository: { path: '/elsewhere', commit: HEAD } },
        }),
      },
    });
    click('Form');
    expect(
      screen.getByText(
        'The server did not list this path. It refuses a path outside its repository roots.',
      ),
    ).toBeTruthy();
    expect(within(field('Repository')).getByRole('option', { name: '/elsewhere' })).toBeTruthy();
  });

  it('puts Check and Start in a bar above the tab bar on a phone, once', async () => {
    viewport(true);
    await open();
    const bar = document.querySelector('.start-bar') as HTMLElement;
    expect(bar).not.toBeNull();
    expect(within(bar).getByRole('button', { name: 'Check' })).toBeTruthy();
    expect(within(bar).getByRole('button', { name: 'Start batch' })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Start batch' })).toHaveLength(1);
  });

  it('keeps Check and Start in the header on a desktop', async () => {
    await open();
    expect(document.querySelector('.start-bar')).toBeNull();
    expect(
      within(document.querySelector('.screen__actions') as HTMLElement).getByRole('button', {
        name: 'Start batch',
      }),
    ).toBeTruthy();
  });
});

describe('Start a single run', () => {
  it('sends one goal, a preset and a repository at its head, and opens the run', async () => {
    const { api, router } = await open({
      replies: { 'POST /v1/runs': [202, {}, { Location: `/v1/runs/${NEW_RUN}` }] },
    });
    click('Single run');
    expect(await screen.findByRole('heading', { level: 1, name: 'Start a run' })).toBeTruthy();
    expect(within(field('Preset')).queryByRole('option', { name: 'driven' })).toBeNull();
    click('Start run');
    expect(screen.getByText('Write what the run should find out or change.')).toBeTruthy();
    expect(post(api)).toBeUndefined();
    type('Goal', 'Where is the retry delay set?');
    type('Repository', REPO);
    click('Start run');
    await waitFor(() => expect(post(api)).toBeDefined());
    expect(post(api)?.body).toEqual({
      contract_version: 'request/v1',
      text: 'Where is the retry delay set?',
      context: { interactive: false, preset: 'default', repository: { path: REPO, commit: HEAD } },
    });
    await waitFor(() => expect(router.state.location.pathname).toBe(`/runs/${NEW_RUN}`));
  });
});
