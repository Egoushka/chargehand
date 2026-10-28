import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { answerRequest } from '../../api/answer';
import {
  BATCH_A,
  BATCH_B,
  COMMIT,
  decisionInbox,
  groupedInbox,
  inbox,
  inboxItems,
  longQuestion,
  REPO,
} from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { ordered, resolvedNote, rowIds, rows, summary, waitingFor } from './model';

const cards = () => screen.getAllByRole('article');
const findCard = async (name: RegExp | string) =>
  within(await screen.findByRole('article', { name }));

describe('Needs you', () => {
  it('lists what needs you, questions first, then pull requests, then failures, with the word for each', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');

    expect(screen.getByRole('heading', { level: 1, name: 'Needs you' })).toBeTruthy();
    expect(screen.getByText('1 question, 1 PR to merge, 1 failure')).toBeTruthy();
    const words = cards().map((c) => within(c).getByText(/^(Question|Asked|Failed)$/).textContent);
    expect(words).toEqual(['Question', 'Asked', 'Failed']);
    expect(cards()[0]?.className).toContain('card--accent');
  });

  it('shows a question with its text, the task, and the box that answers it', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const question = await findCard(
      'Should the TTL come from the environment or from the container template?',
    );
    expect(
      await question.findByText('If the template, which size class should the default follow?'),
    ).toBeTruthy();
    expect(question.getByText('Task: Add a TTL to the memory cache')).toBeTruthy();
    expect(question.getByLabelText('Your answer')).toBeTruthy();
    expect(question.getByText(/continues this task on its branch \(continue_from\)/)).toBeTruthy();
    expect(
      question.getByRole('link', { name: /Open task t3-memory-ttl/ }).getAttribute('href'),
    ).toBe(`/console/runs/${BATCH_A}`);
    expect(question.getByTitle('t3-memory-ttl')).toBeTruthy();
  });

  it('shows a pull request with what it waits for, and a link to it that is only ever http or https', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const pr = await findCard('example/payments-api #412');
    expect(await pr.findByText(/Policy is ask: the pull request is ready/)).toBeTruthy();
    const link = pr.getByRole('link', { name: /Open PR 412/ });
    expect(link.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/412');
    expect(link.getAttribute('rel')).toBe('noreferrer noopener');
    expect(pr.getByText('ask').getAttribute('data-mode')).toBe('ask');
  });

  it('gives a pull request with an unsafe address no link to follow', async () => {
    const body = inbox();
    const item = body.items.find((i) => i.kind === 'pull_request');
    if (item?.pull_request) item.pull_request.url = 'javascript:alert(1)';
    serveApi({ inbox: body });
    renderConsole('/console/needs-you');
    const pr = await findCard('example/payments-api #412');
    await pr.findByText(/Policy is ask/);
    expect(pr.queryByRole('link', { name: /Open PR/ })).toBeNull();
  });

  it('flags a pull request whose change touched only tests or build files', async () => {
    const body = inbox();
    const item = body.items.find((i) => i.kind === 'pull_request');
    if (item?.pull_request) {
      item.pull_request.test_integrity_flagged = true;
      item.reason = 'test_integrity';
    }
    serveApi({ inbox: body });
    renderConsole('/console/needs-you');
    const pr = await findCard('example/payments-api #412');
    expect((await pr.findAllByText('test-integrity flagged')).length).toBeGreaterThanOrEqual(1);
    expect(pr.getByText(/Check that no test was weakened/)).toBeTruthy();
    expect(pr.getByText(/Only test files or build files changed/)).toBeTruthy();
  });

  it('shows a failure with its error code in monospace, what happened and the next step', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const failure = await findCard('Scrub personal data from the export job');
    const code = await failure.findByText('verification_failed');
    expect(code.className).toContain('mono');
    expect(
      failure.getByText(
        /3 tests fail in a fresh container\. Open the verification artifact, then retry the task\./,
      ),
    ).toBeTruthy();
    expect(failure.getByRole('link', { name: /Open run/ }).getAttribute('href')).toBe(
      '/console/runs/run-20261013-124402-77bd0a',
    );
  });

  it('narrows the list by kind, with the counts on the filter', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    const filter = screen.getByRole('group', { name: 'Filter' });
    expect(within(filter).getByRole('button', { name: /All/ }).textContent).toBe('All 3');
    expect(within(filter).getByRole('button', { name: /Questions/ }).textContent).toBe(
      'Questions 1',
    );
    fireEvent.click(within(filter).getByRole('button', { name: /Failures/ }));
    expect(
      within(filter)
        .getByRole('button', { name: /Failures/ })
        .getAttribute('aria-pressed'),
    ).toBe('true');
    expect(cards()).toHaveLength(1);
    expect(screen.getByText('Scrub personal data from the export job')).toBeTruthy();
    fireEvent.click(within(filter).getByRole('button', { name: /All/ }));
    expect(cards()).toHaveLength(3);
  });
});

describe('decision cards (ADR 0085)', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('says what happened, why, and each option with its effect, with the goal collapsed under the card', async () => {
    serveApi({ inbox: decisionInbox() });
    renderConsole('/console/needs-you');
    const failure = await findCard('A task on example/payments-api failed its tests');

    expect(failure.getByText(/they failed, or the repository has no test command/)).toBeTruthy();
    const options = within(failure.getByRole('list', { name: /What you can do/ }));
    expect(options.getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringContaining('Retry, ≈ $1.50Starts a new batch of this one task'),
      expect.stringContaining('Take overFinish it yourself'),
      expect.stringContaining('DismissTakes it out of the inbox'),
    ]);
    expect(failure.getByText('Goal').closest('details')?.open).toBe(false);
    expect(failure.getByText('Owner:')).toBeTruthy();
    // The code shows only in the collapsed goal, under the cause in words.
    expect(failure.getByText('verification_failed').closest('details')).toBeTruthy();
  });

  it('puts questions and pull requests under Decide and failures under Fix, and the badge counts Decide only', async () => {
    serveApi({ inbox: decisionInbox() });
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');

    const decide = within(screen.getByRole('region', { name: /^Decide/ }));
    const fix = within(screen.getByRole('region', { name: /^Fix/ }));
    expect(decide.getAllByRole('article')).toHaveLength(2);
    expect(fix.getAllByRole('article').map((a) => a.getAttribute('aria-labelledby'))).toHaveLength(
      1,
    );
    expect(fix.getByText('A task on example/payments-api failed its tests')).toBeTruthy();
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(within(nav).getByRole('link', { name: /Needs you/ }).textContent).toContain('2');
  });

  it('retries from the card with the call the server named, and says which run it started', async () => {
    const api = serveApi({
      inbox: decisionInbox(),
      replies: {
        [`POST /v1/batches/${BATCH_B}/tasks/t1-pii-scrub/retry`]: [
          202,
          { action: 'retry', run_id: 'run-again-1234567890', changed: true },
        ],
      },
    });
    renderConsole('/console/needs-you');
    const failure = await findCard('A task on example/payments-api failed its tests');

    fireEvent.click(failure.getByRole('button', { name: 'Retry, ≈ $1.50 t1-pii-scrub' }));

    await waitFor(() =>
      expect(api.last(`/v1/batches/${BATCH_B}/tasks/t1-pii-scrub/retry`)?.method).toBe('POST'),
    );
    expect(await screen.findByText(/Retry, ≈ \$1.50: started/)).toBeTruthy();
  });

  it('shows the takeover command and copies it', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    serveApi({ inbox: decisionInbox() });
    renderConsole('/console/needs-you');
    const failure = await findCard('A task on example/payments-api failed its tests');

    fireEvent.click(failure.getByRole('button', { name: 'Take over' }));
    fireEvent.click(failure.getByRole('button', { name: 'Copy the take over command' }));

    expect(failure.getByText(`chargehand takeover ${BATCH_B}/t1-pii-scrub`)).toBeTruthy();
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith(`chargehand takeover ${BATCH_B}/t1-pii-scrub`),
    );
  });

  it('lists what a rule handled, with its reason in words and an Undo that restores it', async () => {
    const api = serveApi({ inbox: decisionInbox() });
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');

    const handled = within(screen.getByRole('list', { name: 'Handled' }));
    expect(handled.getByText('A later run on the repository worked')).toBeTruthy();
    fireEvent.click(
      handled.getByRole('button', { name: /Undo: put A run on example\/payments-api/ }),
    );

    await waitFor(() =>
      expect(api.last('/v1/inbox/restore')?.body).toEqual({ ids: ['failure:run-probe'] }),
    );
  });

  it('will not send an option to anything but a route of the API', async () => {
    const body = decisionInbox();
    const retry = body.items[2]?.options?.[0];
    if (retry?.call) retry.call.path = 'https://elsewhere.test/v1/x';
    const api = serveApi({ inbox: body });
    renderConsole('/console/needs-you');
    const failure = await findCard('A task on example/payments-api failed its tests');

    fireEvent.click(failure.getByRole('button', { name: 'Retry, ≈ $1.50 t1-pii-scrub' }));

    expect(await screen.findByText('Retry, ≈ $1.50 refused')).toBeTruthy();
    expect(api.calls.some((c) => c.path.includes('elsewhere'))).toBe(false);
  });
});

describe('dismissing', () => {
  it('dismisses one item by its id, says so with an Undo that restores it, and refreshes the inbox', async () => {
    const api = serveApi();
    renderConsole('/console/needs-you');
    const failure = await findCard('Scrub personal data from the export job');
    const before = api.calls.filter((c) => c.path.startsWith('/v1/inbox?')).length;

    fireEvent.click(failure.getByRole('button', { name: 'Dismiss t1-pii-scrub' }));

    await waitFor(() => expect(api.last('/v1/inbox/dismiss')).toBeTruthy());
    expect(api.last('/v1/inbox/dismiss')?.body).toEqual({
      ids: [`failure:${BATCH_B}:t1-pii-scrub`],
    });
    expect(api.last('/v1/inbox/dismiss')?.method).toBe('POST');
    expect(await screen.findByText('Dismissed t1-pii-scrub')).toBeTruthy();
    await waitFor(() =>
      expect(api.calls.filter((c) => c.path.startsWith('/v1/inbox?')).length).toBeGreaterThan(
        before,
      ),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await waitFor(() =>
      expect(api.last('/v1/inbox/restore')?.body).toEqual({
        ids: [`failure:${BATCH_B}:t1-pii-scrub`],
      }),
    );
  });

  it('dismisses all the items the filter shows and no others', async () => {
    const api = serveApi();
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    const filter = screen.getByRole('group', { name: 'Filter' });
    fireEvent.click(within(filter).getByRole('button', { name: /PRs/ }));

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss all 1 shown' }));

    await waitFor(() =>
      expect(api.last('/v1/inbox/dismiss')?.body).toEqual({
        ids: ['pull_request:https://github.com/example/payments-api/pull/412'],
      }),
    );
    expect(await screen.findByText('Dismissed 1 item')).toBeTruthy();
  });

  it('marks a failure that stands for several and says what left the inbox on its own', async () => {
    const body = inbox();
    const failure = body.items.find((i) => i.kind === 'failure');
    if (failure) failure.count = 17;
    body.resolved = {
      superseded: 4,
      infrastructure: 25,
      continued: 2,
      dismissed: 1,
      duplicates: 16,
    };
    serveApi({ inbox: body });
    renderConsole('/console/needs-you');
    const card = await findCard('Scrub personal data from the export job');

    expect(card.getByText('×17')).toBeTruthy();
    expect(
      screen.getByText(/31 resolved on their own, 1 dismissed, 16 duplicates folded/),
    ).toBeTruthy();
    expect((await findCard('example/payments-api #412')).queryByText(/^×/)).toBeNull();
  });

  it('puts the resolved counts in words, and nothing for a server that sends none', () => {
    expect(resolvedNote(undefined)).toBe('');
    expect(
      resolvedNote({ superseded: 0, infrastructure: 0, continued: 0, dismissed: 0, duplicates: 0 }),
    ).toBe('');
    expect(
      resolvedNote({ superseded: 1, infrastructure: 0, continued: 0, dismissed: 0, duplicates: 1 }),
    ).toBe('1 resolved on their own, 1 duplicate folded');
  });
});

describe('grouped failures', () => {
  const groupCard = () => findCard(/6 failures: checkout_has_secrets on infra-config/);

  it('shows failures with one repository and code as one card with the count, the newest and the rest behind Show all', async () => {
    serveApi({ inbox: groupedInbox() });
    renderConsole('/console/needs-you');
    const card = await groupCard();

    expect(cards()).toHaveLength(4);
    expect(card.getByText('×6')).toBeTruthy();
    expect(card.getByText('Newest: Review pull request example/infra-config#43.')).toBeTruthy();
    expect(card.getByText(/grep would still reach\. Use a preset that allows them/)).toBeTruthy();
    expect(card.queryByRole('list')).toBeNull();
    const toggle = card.getByRole('button', { name: 'Show 3 runs' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    fireEvent.click(toggle);

    const list = card.getByRole('list', { name: 'The checkout_has_secrets failures' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    expect(within(list).getByText('×4')).toBeTruthy();
    expect(
      within(list)
        .getByRole('link', { name: /Open run run-review-1/ })
        .getAttribute('href'),
    ).toBe('/console/runs/run-review-1');
    expect(card.getByRole('button', { name: 'Hide runs' }).getAttribute('aria-expanded')).toBe(
      'true',
    );
  });

  it('dismisses the whole group by its id in one call, with an Undo that restores it', async () => {
    const api = serveApi({ inbox: groupedInbox() });
    renderConsole('/console/needs-you');
    const card = await groupCard();

    fireEvent.click(
      card.getByRole('button', { name: 'Dismiss all 6 checkout_has_secrets failures' }),
    );

    await waitFor(() =>
      expect(api.last('/v1/inbox/dismiss')?.body).toEqual({
        ids: ['group:checkout_has_secrets:failure:run-review-3'],
      }),
    );
    expect(await screen.findByText('Dismissed 6 checkout_has_secrets failures')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await waitFor(() =>
      expect(api.last('/v1/inbox/restore')?.body).toEqual({
        ids: ['group:checkout_has_secrets:failure:run-review-3'],
      }),
    );
  });

  it('folds a group only where two or more of its items are in the list, and Dismiss all shown sends the group id', () => {
    const body = groupedInbox();
    const all = rows(ordered(body.items, 'all'), body.groups);
    expect(all.map((r) => r.type)).toEqual(['item', 'item', 'item', 'group']);
    expect(rowIds(all)).toContain('group:checkout_has_secrets:failure:run-review-3');
    const one = body.items.filter(
      (i) => i.id !== 'failure:run-review-1' && i.id !== 'failure:run-review-2',
    );
    expect(rows(one, body.groups).every((r) => r.type === 'item')).toBe(true);
    expect(rows(body.items).every((r) => r.type === 'item')).toBe(true);
  });

  it('counts failures about a closed pull request as resolved on their own', () => {
    expect(
      resolvedNote({
        superseded: 1,
        infrastructure: 0,
        continued: 0,
        dismissed: 0,
        duplicates: 0,
        pr_closed: 2,
      }),
    ).toBe('3 resolved on their own');
  });
});

describe('answering a question', () => {
  const type = (text: string) => {
    fireEvent.change(screen.getByLabelText('Your answer'), { target: { value: text } });
  };

  it('keeps the button off until there is an answer', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const button = (await screen.findByRole('button', {
      name: 'Answer and continue',
    })) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    type('   ');
    expect(button.disabled).toBe(true);
    type('Use the container template.');
    expect(button.disabled).toBe(false);
  });

  it('sends the request the inbox carried: a driven task that continues the asking one, with the answer as sent', async () => {
    const { calls } = serveApi();
    renderConsole('/console/needs-you');
    await screen.findByRole('button', { name: 'Answer and continue' });
    type('Use the container template; follow the M size class.');
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.path).toBe('/v1/runs');
    expect(post?.headers.Prefer).toBe('wait=0');
    expect(post?.headers['Content-Type']).toBe('application/json');
    expect(post?.body).toEqual({
      contract_version: 'request/v1',
      text: `Answer for task t3-memory-ttl of ${BATCH_A}`,
      context: { interactive: false, preset: 'driven', repository: { path: REPO, commit: COMMIT } },
      driven: {
        tasks: [
          {
            id: 't3-memory-ttl',
            continue_from: { run_id: BATCH_A, task_id: 't3-memory-ttl' },
            answers: ['Use the container template; follow the M size class.'],
          },
        ],
      },
    });
  });

  it('says the answer went, links the batch it started, and raises a notice', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    await screen.findByRole('button', { name: 'Answer and continue' });
    type('Yes.');
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));

    expect(await screen.findByText(/Answer sent\. A new batch continues the task/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'abc123' }).getAttribute('href')).toBe(
      '/console/runs/run-20261013-150000-abc123',
    );
    expect(screen.queryByLabelText('Your answer')).toBeNull();
    expect(await screen.findByText('Answer sent to t3-memory-ttl')).toBeTruthy();
  });

  it('shows what the server said when it refuses the answer, and keeps the text', async () => {
    serveApi({
      replies: { 'POST /v1/runs': [400, { errors: ['the earlier batch has not finished'] }] },
    });
    renderConsole('/console/needs-you');
    await screen.findByRole('button', { name: 'Answer and continue' });
    type('Yes.');
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));

    const alert = await screen.findByText('the earlier batch has not finished');
    expect(alert.closest('[role="alert"]')).not.toBeNull();
    expect((screen.getByLabelText('Your answer') as HTMLTextAreaElement).value).toBe('Yes.');
    expect(screen.getByLabelText('Your answer').getAttribute('aria-invalid')).toBe('true');
  });

  it('will not send to anything but a route of the API, whatever the hint says', async () => {
    const body = inbox();
    const item = body.items.find((i) => i.kind === 'question');
    if (item?.answer) item.answer.path = 'https://elsewhere.example/steal';
    const { calls } = serveApi({ inbox: body });
    renderConsole('/console/needs-you');
    await screen.findByRole('button', { name: 'Answer and continue' });
    type('Yes.');
    fireEvent.click(screen.getByRole('button', { name: 'Answer and continue' }));
    expect(await screen.findByText(/will not send/)).toBeTruthy();
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('builds the request from the hint alone, with no repository when the hint names none', () => {
    const body = answerRequest(
      {
        method: 'POST',
        path: '/v1/runs',
        continue_from: { run_id: 'b1', task_id: 't1' },
        preset: 'driven',
      },
      'A',
    );
    expect(body.context).toEqual({ interactive: false, preset: 'driven' });
    expect(body.driven.tasks[0]).toEqual({
      id: 't1',
      continue_from: { run_id: 'b1', task_id: 't1' },
      answers: ['A'],
    });
  });

  it('sends with ⌘Enter from the box', async () => {
    const { calls } = serveApi();
    renderConsole('/console/needs-you');
    await screen.findByRole('button', { name: 'Answer and continue' });
    type('Yes.');
    fireEvent.keyDown(screen.getByLabelText('Your answer'), { key: 'Enter', metaKey: true });
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
  });
});

describe('Needs you states', () => {
  it('says why it is empty, and goes back to the Overview', async () => {
    serveApi({ inbox: inbox([]) });
    renderConsole('/console/needs-you');
    expect(await screen.findByText('Nothing needs you')).toBeTruthy();
    expect(screen.getByText(/no run failed in the last 24 hours/)).toBeTruthy();
    expect(screen.getByText('All clear')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to Overview' }).getAttribute('href')).toBe(
      '/console/',
    );
  });

  it('shows skeleton cards while loading', async () => {
    serveApi({ hang: ['/v1/inbox'] });
    renderConsole('/console/needs-you');
    await screen.findByRole('heading', { level: 1, name: 'Needs you' });
    expect(screen.getByText('Loading')).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(3);
  });

  it('names the call when the inbox cannot be read, and retries', async () => {
    const { calls } = serveApi({ replies: { '/v1/inbox': [503, { errors: ['timed out'] }] } });
    renderConsole('/console/needs-you');
    const box = (await screen.findByText('Cannot refresh the inbox')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/inbox')).toBeTruthy();
    const before = calls.filter((c) => c.path.startsWith('/v1/inbox')).length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path.startsWith('/v1/inbox')).length).toBeGreaterThan(before),
    );
  });

  it('keeps the list on screen, with its age, when a refresh fails', async () => {
    serveApi();
    const { client } = renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    serveApi({ replies: { '/v1/inbox': [503, { errors: ['gateway timeout'] }] } });
    await act(() => client.invalidateQueries({ queryKey: ['inbox'] }));
    const box = (await screen.findByText('Cannot refresh the inbox')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(box.textContent).toMatch(
      /The list below is the data from \d\d:\d\d:\d\d\. Retrying every 10 s\./,
    );
    expect(cards()).toHaveLength(3);
  });

  it('lays out long content: the whole text stays, the repository truncates with its path as the title', async () => {
    serveApi({ inbox: inbox([longQuestion(), ...inboxItems()]) });
    renderConsole('/console/needs-you');
    const long = await screen.findByText(
      /The memory admission test passes on a 32 GiB host but the CI runner has 7 GiB: should the test read the limit from the environment, from the container template, or be skipped in CI with a visible reason that names the host limit/,
    );
    expect(long.className).toBe('card__title');
    const badge = screen.getByText(
      'a-repository-with-a-very-long-name-that-does-not-fit-in-a-badge',
    );
    expect(badge.getAttribute('title')).toBe(
      '/srv/work/a-repository-with-a-very-long-name-that-does-not-fit-in-a-badge',
    );
    expect(
      screen.getByTitle('ch-118-122-stability-guards-and-the-scheduler-memory-admission-test'),
    ).toBeTruthy();
  });

  it('shows a filter with nothing in it as such, and not as an empty inbox', async () => {
    serveApi({ inbox: inbox(inboxItems().filter((i) => i.kind !== 'failure')) });
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    fireEvent.click(
      within(screen.getByRole('group', { name: 'Filter' })).getByRole('button', {
        name: /Failures/,
      }),
    );
    expect(screen.getByText('Nothing in this filter')).toBeTruthy();
    expect(screen.queryByText('Nothing needs you')).toBeNull();
  });
});

describe('Needs you from the keyboard', () => {
  it('moves the cursor with j and k, and goes to the answer box with a', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    const cursor = () => cards().findIndex((c) => c.getAttribute('data-cursor') === 'true');
    expect(cursor()).toBe(0);
    fireEvent.keyDown(document.body, { key: 'j' });
    expect(cursor()).toBe(1);
    fireEvent.keyDown(document.body, { key: 'j' });
    fireEvent.keyDown(document.body, { key: 'j' });
    expect(cursor()).toBe(2); // stops at the last
    fireEvent.keyDown(document.body, { key: 'k' });
    fireEvent.keyDown(document.body, { key: 'k' });
    fireEvent.keyDown(document.body, { key: 'k' });
    expect(cursor()).toBe(0);
    fireEvent.keyDown(document.body, { key: 'a' });
    expect(document.activeElement).toBe(screen.getByLabelText('Your answer'));
  });

  it('opens the task with Enter, and leaves keys alone while you type an answer', async () => {
    serveApi();
    const { router } = renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    const box = screen.getByLabelText('Your answer');
    box.focus();
    fireEvent.keyDown(box, { key: 'j' });
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(cards()[0]?.getAttribute('data-cursor')).toBe('true');
    expect(router.state.location.pathname).toBe('/needs-you');
    (box as HTMLElement).blur();
    fireEvent.keyDown(document.body, { key: 'Enter' });
    await waitFor(() => expect(router.state.location.pathname).toBe(`/runs/${BATCH_A}`));
  });
});

describe('Notify me', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('says in-page notices still work when the browser cannot notify (plain http)', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const panel = (await screen.findByRole('heading', { name: 'Notify me' })).closest(
      'section',
    ) as HTMLElement;
    expect(within(panel).getByText(/need a secure page \(https, or localhost\)/)).toBeTruthy();
    expect(within(panel).queryByRole('button', { name: 'Turn on' })).toBeNull();
  });

  it('switches each kind of notice on and off, and remembers it', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const sw = await screen.findByRole('switch', { name: 'Batch finishes' });
    expect((sw as HTMLInputElement).checked).toBe(true);
    fireEvent.click(sw);
    expect(
      (screen.getByRole('switch', { name: 'Batch finishes' }) as HTMLInputElement).checked,
    ).toBe(false);
    expect(JSON.parse(localStorage.getItem('chargehand.console') ?? '{}').notify).toEqual({
      question: true,
      pull_request: true,
      batch: false,
      failure: true,
    });
    expect(screen.getAllByRole('switch')).toHaveLength(4);
  });

  it('asks the browser once, from the click, and turns browser notifications on when it agrees', async () => {
    const requestPermission = vi.fn(async () => 'granted' as const);
    const FakeNotification = Object.assign(vi.fn(), {
      permission: 'default' as NotificationPermission,
      requestPermission,
    });
    vi.stubGlobal('Notification', FakeNotification);
    Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
    serveApi();
    renderConsole('/console/needs-you');
    const panel = (await screen.findByRole('heading', { name: 'Notify me' })).closest(
      'section',
    ) as HTMLElement;
    expect(requestPermission).not.toHaveBeenCalled();
    FakeNotification.permission = 'default';
    fireEvent.click(within(panel).getByRole('button', { name: 'Turn on' }));
    await waitFor(() => expect(requestPermission).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(
        JSON.parse(localStorage.getItem('chargehand.console') ?? '{}').browserNotifications,
      ).toBe(true),
    );
    Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true });
  });
});

describe('Needs you model', () => {
  it('orders questions, then pull requests, then failures, newest first within a kind', () => {
    const [q, pr, f] = inboxItems();
    const newerFailure = {
      ...(f as NonNullable<typeof f>),
      id: 'f2',
      at: new Date(Date.now() + 1000).toISOString(),
    };
    expect(ordered([f, newerFailure, pr, q] as never, 'all').map((i) => i.id)).toEqual([
      (q as { id: string }).id,
      (pr as { id: string }).id,
      'f2',
      (f as { id: string }).id,
    ]);
    expect(ordered([f, pr, q] as never, 'question')).toHaveLength(1);
  });

  it('summarises the counts in words', () => {
    expect(summary({ questions: 1, pull_requests: 2, failures: 0, total: 3 })).toBe(
      '1 question, 2 PRs to merge, 0 failures',
    );
    expect(summary({ questions: 0, pull_requests: 0, failures: 0, total: 0 })).toBe('All clear');
  });

  it('says what a pull request waits for', () => {
    expect(waitingFor('policy_ask')).toMatch(/Policy is ask/);
    expect(waitingFor('test_integrity')).toMatch(/test files or build files/);
    expect(waitingFor('partial_draft')).toMatch(/\[partial\] draft/);
  });
});
