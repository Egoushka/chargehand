import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  accountOverview,
  BATCH_A,
  BATCH_B,
  batch,
  inbox,
  me,
  overview,
  pulls,
  REPO,
  run,
} from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { activeBatch, batchProgress, capRow, kpis, recentMerges, spendRows } from './model';

const running = [run(BATCH_A), run(BATCH_B, { tokens: 205_000 })];

/** The panel with this title. */
const panel = (title: string) => {
  const section = screen.getByRole('heading', { name: title }).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

/** The KPI tile with this label. */
const kpi = (label: string) => {
  const name = [...document.querySelectorAll('.ch-kpi__label')].find(
    (el) => el.textContent === label,
  );
  if (!name) throw new Error(`no KPI ${label}`);
  return within(name.closest('.ch-kpi') as HTMLElement);
};

describe('Overview with data', () => {
  it('shows the four figures: sessions against the limit, spend today, what needs you, merges', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');

    expect(kpi('Sessions running').getByText('5')).toBeTruthy();
    expect(kpi('Sessions running').getByText('of 6')).toBeTruthy();
    expect(kpi('Sessions running').getByText('2 batches')).toBeTruthy();
    expect(kpi('Spend today').getByText('$26.14')).toBeTruthy();
    expect(kpi('Spend today').getByText('5.10M tokens · 3 accounts')).toBeTruthy();
    expect(kpi('Needs you').getByText('3')).toBeTruthy();
    expect(kpi('Needs you').getByText('1 question · 1 PR · 1 failure')).toBeTruthy();
    await waitFor(() => expect(kpi('Merged').getByText('2')).toBeTruthy());
    expect(kpi('Merged').getByText('in the last 24 hours')).toBeTruthy();
  });

  it('lists the running batches with progress, slots, spend against its cap, and a link to each', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    const batches = await screen.findByRole('heading', { name: 'Active batches' });
    const section = within(batches.closest('section') as HTMLElement);

    const link = await section.findByRole('link', { name: /1fdf01/ });
    expect(link.getAttribute('href')).toBe(`/console/runs/${BATCH_A}`);
    expect(within(link).getByText('Running')).toBeTruthy();
    expect(within(link).getByText('payments-api').getAttribute('title')).toBe(REPO);
    expect(within(link).getByText('@a91c0ff')).toBeTruthy();
    expect(
      await within(link).findByRole('img', {
        name: '2 done, 2 running, 0 need attention, 2 waiting',
      }),
    ).toBeTruthy();
    expect(within(link).getByText('2 of 6')).toBeTruthy();
    expect(within(link).getByText('960k')).toBeTruthy();
    expect(within(link).getByText('≈ $5.40')).toBeTruthy();
    expect(within(link).getByText(/of \$15\.00/)).toBeTruthy();
    expect(link.querySelectorAll('.slot')).toHaveLength(4);
    expect(link.querySelectorAll('.slot[data-on="true"]')).toHaveLength(2);
    expect(section.getAllByRole('link', { name: /^Running/ })).toHaveLength(2);
    expect(
      batches.closest('section')?.querySelector('.ch-panel__head .ch-count')?.textContent,
    ).toBe('2');
  });

  it('summarises what needs you, with the one action that takes you there', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    const needs = await screen.findByRole('heading', { name: 'Needs you', level: 3 });
    const section = within(needs.closest('section') as HTMLElement);
    const rows = await section.findAllByText(/payments-api/);
    expect(rows.length).toBeGreaterThanOrEqual(3);

    expect(section.getByText('Question')).toBeTruthy();
    expect(section.getByText('Asked')).toBeTruthy();
    expect(section.getByText('Failed')).toBeTruthy();
    expect(section.getByRole('link', { name: 'Answer' }).getAttribute('href')).toBe(
      '/console/needs-you',
    );
    const pr = section.getByRole('link', { name: 'Open PR' });
    expect(pr.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/412');
    expect(pr.getAttribute('target')).toBe('_blank');
    expect(pr.getAttribute('rel')).toBe('noreferrer noopener');
    expect(section.getByRole('link', { name: 'Open run' }).getAttribute('href')).toBe(
      '/console/runs/run-20261013-124402-77bd0a',
    );
    expect(section.getByText(/verification_failed/)).toBeTruthy();
    expect(section.getByRole('link', { name: 'Open inbox' }).getAttribute('href')).toBe(
      '/console/needs-you',
    );
  });

  it('never links a pull request whose address is not http or https', async () => {
    const body = inbox();
    const prItem = body.items.find((i) => i.kind === 'pull_request');
    if (prItem?.pull_request) prItem.pull_request.url = 'javascript:alert(1)';
    serveApi({ runs: running, inbox: body });
    renderConsole('/console/');
    const needs = await screen.findByRole('heading', { name: 'Needs you', level: 3 });
    const section = within(needs.closest('section') as HTMLElement);
    await section.findByText('Asked');
    expect(section.queryByRole('link', { name: 'Open PR' })).toBeNull();
  });

  it('shows spend today against each account’s cap, and says "no cap" for one without', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    const spend = panel('Spend today vs caps');
    expect(spend.getByText('resets', { exact: false }).textContent).toMatch(
      /resets \d\d:\d\d Kyiv/,
    );
    const meters = spend.getAllByRole('meter');
    expect(meters.map((m) => m.getAttribute('aria-label'))).toEqual([
      'owner, spend today',
      'ci-bot, spend today',
      'reviewer, spend today',
    ]);
    expect(meters[0]?.getAttribute('data-empty')).toBe('true');
    expect(spend.getByText('$12.40 · no cap')).toBeTruthy();
    expect(meters[1]?.getAttribute('data-level')).toBe('danger');
    expect(spend.getByText('$9.40 of $10.00 · 94%')).toBeTruthy();
    expect(meters[2]?.getAttribute('data-level')).toBeNull();
    expect(spend.getByText('$4.34 of $25.00 · 17%')).toBeTruthy();
  });

  it('shows what admission looks at: memory with its reserve, load, session slots, and the pause', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    const admission = panel('Admission');
    const memory = admission.getByRole('meter', { name: 'Memory available' });
    expect(memory.getAttribute('data-level')).toBe('warn'); // 2.2 of 31.4 GiB is under 20 %
    expect(admission.getByText('2.2 GiB of 31.4 GiB')).toBeTruthy();
    expect(memory.querySelector('.ch-meter__tick')).not.toBeNull();
    expect(admission.getByText(/1\.0 GiB reserve admission keeps free/)).toBeTruthy();
    expect(admission.getByRole('meter', { name: 'Load, 1 min' })).toBeTruthy();
    expect(admission.getByText('7.4 on 16 CPUs · 46% each')).toBeTruthy();
    expect(admission.getByText('5 of 6')).toBeTruthy();
    expect(admission.getByText('No pause')).toBeTruthy();
    expect(admission.getByText('Accepting runs; halt cancels every run')).toBeTruthy();
  });

  it('lists the system: version, uptime and runner for the owner', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    const system = panel('System');
    await waitFor(() => expect(system.getByText(/up 3d 2h/)).toBeTruthy());
    expect(system.getByText(/0\.9\.1/)).toBeTruthy();
    expect(system.getByText('reachable · 11 containers')).toBeTruthy();
  });

  it('lists the pull requests merged in the last 24 hours, newest first, with the time as a title', async () => {
    serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    const merges = panel('Recent merges');
    const first = await merges.findByRole('link', { name: 'example/payments-api #409' });
    expect(first.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/409');
    expect(merges.getByRole('link', { name: 'example/web-shop #407' })).toBeTruthy();
    expect(merges.queryByRole('link', { name: 'example/payments-api #412' })).toBeNull(); // open: not a merge
    expect(merges.getByText('1h 20m ago').getAttribute('title')).toMatch(/Oct 2026 \d\d:\d\d:\d\d/);
    expect(merges.getAllByText(/merged by the integrator/, { selector: '.li__meta' })).toHaveLength(
      2,
    );
  });
});

describe('Overview halted and paused', () => {
  it('asks before halting, says what stops, and halts only on the confirm', async () => {
    const { calls } = serveApi({ runs: running });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    fireEvent.click(panel('Admission').getByRole('button', { name: 'Halt server' }));

    const dialog = await screen.findByRole('alertdialog', {
      name: 'Halt the server?',
      hidden: true,
    });
    expect(within(dialog).getByText(/5 running sessions stop/)).toBeTruthy();
    expect(within(dialog).getByText(/Nothing is pushed/)).toBeTruthy();
    expect(calls.some((c) => c.path === '/v1/halt')).toBe(false);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep running', hidden: true }));
    expect(screen.queryByRole('alertdialog', { hidden: true })).toBeNull();
    expect(calls.some((c) => c.path === '/v1/halt')).toBe(false);

    fireEvent.click(panel('Admission').getByRole('button', { name: 'Halt server' }));
    fireEvent.click(
      within(await screen.findByRole('alertdialog', { hidden: true })).getByRole('button', {
        name: 'Halt server',
        hidden: true,
      }),
    );
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/v1/halt' && c.method === 'POST')).toBe(true),
    );
    expect(await screen.findByText('Server halted')).toBeTruthy();
  });

  it('shows the halted state to everyone: a banner, a Resume server button, no Halt button', async () => {
    const base = overview();
    const { calls } = serveApi({
      runs: running,
      overview: overview({ server: { ...base.server, halted: true } }),
      me: me({ halted: true }),
    });
    renderConsole('/console/');
    const banner = await screen.findByText('Server is halted');
    const alert = banner.closest('[role="alert"]') as HTMLElement;
    expect(alert.getAttribute('data-tone')).toBe('danger');
    expect(panel('Admission').queryByRole('button', { name: 'Halt server' })).toBeNull();
    expect(panel('Admission').getByText('Halted: nothing starts')).toBeTruthy();

    fireEvent.click(within(alert).getByRole('button', { name: 'Resume server' }));
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/v1/resume' && c.method === 'POST')).toBe(true),
    );
    expect(await screen.findByText('Server resumed')).toBeTruthy();
  });

  it('tells an account that the server is halted, with nothing it can do about it', async () => {
    const base = accountOverview();
    serveApi({
      overview: { ...base, server: { ...base.server, halted: true } },
      me: me({ name: 'ci-bot', owner: false, halted: true }),
    });
    renderConsole('/console/');
    const alert = (await screen.findByText('Server is halted')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(alert).queryByRole('button')).toBeNull();
  });

  it('shows a rate-limit pause as a banner and in the panel', async () => {
    // A pause 15 minutes ahead would print a date, not a clock, when it falls after local midnight.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(Date.parse('2026-10-14T12:00:00Z'));
    const base = overview();
    const resets = new Date(Date.now() + 15 * 60_000).toISOString();
    serveApi({
      runs: running,
      overview: overview({
        driven: {
          ...base.driven,
          rate_limit: {
            paused: true,
            resets_at: resets,
            pauses: [
              {
                batch_run_id: BATCH_A,
                task_id: 't3',
                observed_at: new Date().toISOString(),
                resets_at: resets,
              },
            ],
          },
        },
      }),
    });
    renderConsole('/console/');
    const banner = await screen.findByText(/New starts are paused until about/);
    expect(banner.closest('.ch-banner')?.getAttribute('data-tone')).toBe('warn');
    expect(panel('Admission').getByText(/Paused until \d\d:\d\d/)).toBeTruthy();
    vi.useRealTimers();
  });
});

describe('Overview states', () => {
  it('shows skeletons with a busy panel while the first answer is on its way', async () => {
    serveApi({ hang: ['/v1/overview', '/v1/inbox'] });
    renderConsole('/console/');
    const head = await screen.findByRole('heading', { name: 'Overview', level: 1 });
    expect(head).toBeTruthy();
    expect(screen.getByText('Loading')).toBeTruthy();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(5);
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('names the failing call, the status and offers Retry when the server cannot be reached', async () => {
    const { calls } = serveApi({
      replies: { '/v1/overview': [503, { errors: ['timed out after 10 s'] }] },
    });
    renderConsole('/console/');
    const alert = await screen.findByText('Cannot reach the server');
    const box = alert.closest('[role="alert"]') as HTMLElement;
    expect(within(box).getByText('GET /v1/overview').className).toContain('mono');
    expect(box.textContent).toContain('timed out after 10 s');
    const before = calls.filter((c) => c.path === '/v1/overview').length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/v1/overview').length).toBeGreaterThan(before),
    );
  });

  it('keeps the last data on screen, and says how old it is, when a refresh fails', async () => {
    serveApi({ runs: running });
    const { client } = renderConsole('/console/');
    await screen.findByText('$26.14');
    serveApi({
      runs: running,
      replies: { '/v1/overview': [503, { errors: ['gateway timeout'] }] },
    });
    await act(() => client.invalidateQueries({ queryKey: ['overview'] }));
    const box = (await screen.findByText('Cannot reach the server')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(box.textContent).toMatch(/Showing the last data, from \d\d:\d\d:\d\d\./);
    expect(screen.getByText('$26.14')).toBeTruthy();
  });

  it('says why each panel is empty, and offers the one action that fills it', async () => {
    const base = overview();
    serveApi({
      runs: [],
      pulls: { ...pulls(), pulls: [] },
      inbox: inbox([]),
      overview: overview({
        driven: { ...base.driven, sessions: { running: 0, max_parallel_total: 6, batches: 0 } },
        inbox: { questions: 0, pull_requests: 0, failures: 0, total: 0 },
      }),
    });
    renderConsole('/console/');
    expect(await screen.findByText('No batch is running')).toBeTruthy();
    expect(screen.getByText(/label a GitHub issue chargehand/)).toBeTruthy();
    expect(screen.getAllByRole('link', { name: 'Start a batch' }).length).toBeGreaterThanOrEqual(1);
    expect(await screen.findByText('Nothing needs you')).toBeTruthy();
    expect(screen.getByText(/no run failed in the last 24 hours/)).toBeTruthy();
    expect(await screen.findByText('Nothing merged')).toBeTruthy();
    expect(kpi('Sessions running').getByText('idle')).toBeTruthy();
    expect(kpi('Needs you').getByText('all clear')).toBeTruthy();
  });

  it('lets long names truncate and keeps the full text in the title', async () => {
    const long = '/srv/work/chargehand-experiments-with-a-very-long-repository-name';
    const long1 = run(BATCH_A, { repository: long });
    serveApi({
      runs: [long1],
      batches: {
        [BATCH_A]: { ...batch(BATCH_A), repository: { path: long, commit: 'a91c0ffe21b7' } },
      },
    });
    renderConsole('/console/');
    const badge = await screen.findByText(
      'chargehand-experiments-with-a-very-long-repository-name',
    );
    expect(badge.getAttribute('title')).toBe(long);
    expect(badge.className).toContain('ch-badge');
  });
});

describe('Overview for an account', () => {
  it('shows its own spend and sessions, and nothing about the host, the runner or other accounts', async () => {
    const { calls } = serveApi({
      overview: accountOverview(),
      me: me({ name: 'ci-bot', owner: false, limits: { max_usd_per_day: 10 } }),
      runs: [],
      inbox: inbox([]),
      system: null,
    });
    renderConsole('/console/');
    await screen.findByText('Sessions running');
    expect(kpi('Sessions running').getByText('1')).toBeTruthy();
    expect(kpi('Sessions running').queryByText(/of \d/)).toBeNull();
    const spend = panel('Spend today vs caps');
    expect(spend.getAllByRole('meter')).toHaveLength(1);
    expect(spend.getByRole('meter').getAttribute('aria-label')).toBe('ci-bot, spend today');
    const admission = panel('Admission');
    expect(admission.queryByRole('meter', { name: 'Memory available' })).toBeNull();
    expect(admission.queryByRole('button', { name: 'Halt server' })).toBeNull();
    expect(panel('System').queryByText('Runner')).toBeNull();
    expect(panel('System').queryByRole('link', { name: 'Details' })).toBeNull();
    expect(calls.some((c) => c.path === '/v1/system')).toBe(false);
  });
});

describe('Overview model', () => {
  const tasks = batch(BATCH_A).tasks;

  it('groups a batch’s tasks into the segments of its bar, and lists the counts', () => {
    const progress = batchProgress([
      ...tasks,
      {
        ...(tasks[0] as (typeof tasks)[number]),
        id: 'p',
        state: 'completed',
        pull_request: { partial: true } as never,
      },
      { ...(tasks[0] as (typeof tasks)[number]), id: 'q', state: 'needs_input' },
      { ...(tasks[0] as (typeof tasks)[number]), id: 'f', state: 'failed' },
    ]);
    expect(progress).toMatchObject({
      total: 9,
      done: 2,
      running: 2,
      question: 1,
      partial: 1,
      failed: 1,
      waiting: 2,
    });
    expect(progress.segments.map((s) => [s.tone, s.n])).toEqual([
      ['ok', 2],
      ['info', 2],
      ['accent', 1],
      ['warn', 1],
      ['danger', 1],
      ['neutral', 2],
    ]);
    expect(progress.label).toBe('2 done, 2 running, 3 need attention, 2 waiting');
  });

  it('sums a batch’s spend as the dollar equivalent, marked ≈, and shows no cap when there is none', () => {
    const model = activeBatch({ ...batch(BATCH_A), limits: { max_parallel: 3 } });
    expect(model.usd).toBe('≈ $5.40');
    expect(model.tokens).toBe('960k');
    expect(model.cap).toBe('no cap');
    expect(model.slots).toEqual([true, true, false]);
  });

  it('picks dollars, else tokens, else "no cap", for a cap row, and marks the level', () => {
    const standing = me({}, Date.now());
    expect(
      capRow({
        ...standing,
        limits: { max_usd_per_day: 10 },
        spend: { ...standing.spend, day: { ...standing.spend.day, usd: 9.4 } },
      }),
    ).toMatchObject({ cap: 10, level: 'danger', text: '$9.40 of $10.00 · 94%' });
    expect(capRow({ ...standing, limits: { max_tokens_per_day: 8_000_000 } })).toMatchObject({
      cap: 8_000_000,
      text: '5.10M of 8M tokens · 64%',
    });
    expect(capRow({ ...standing, limits: {} })).toMatchObject({
      cap: null,
      text: '$26.14 · no cap',
    });
  });

  it('lists every account for the owner, and the caller alone for an account', () => {
    expect(spendRows(overview()).map((r) => r.name)).toEqual(['owner', 'ci-bot', 'reviewer']);
    expect(spendRows(accountOverview()).map((r) => r.name)).toEqual(['ci-bot']);
  });

  it('counts the merges of the last 24 hours only, by when the outcome was read, newest first', () => {
    const now = Date.parse('2026-10-13T12:00:00Z');
    const pr = (n: number, state: string, at: string) =>
      ({ ...pulls().pulls[2], number: n, url: `u${n}`, state, state_at: at }) as never;
    const merged = recentMerges(
      [
        pr(1, 'merged', '2026-10-13T06:00:00Z'),
        pr(2, 'merged_edited', '2026-10-13T09:00:00Z'),
        pr(3, 'open', '2026-10-13T10:00:00Z'),
        pr(4, 'merged_unchanged', '2026-10-11T09:00:00Z'),
      ],
      now,
    );
    expect(merged.map((p) => p.number)).toEqual([2, 1]);
  });

  it('writes the figures in the design’s words', () => {
    const model = kpis(overview(), []);
    expect(model.sessions).toEqual({ running: 5, max: 6, sub: '2 batches' });
    expect(model.needs.sub).toBe('1 question · 1 PR · 1 failure');
    expect(model.merged).toEqual({ value: 0, sub: 'in the last 24 hours' });
    expect(kpis(accountOverview(), []).spend.sub).toBe('5.10M tokens');
  });
});
