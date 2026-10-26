import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { PullRequestBody } from '../../api/common';
import type { IntakeTaskBody } from '../../api/intake';
import * as adm from '../../testing/admin';
import { me, pulls } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  filterRows,
  parseTrigger,
  policyRows,
  queueText,
  recentActions,
  reviewRounds,
  sourceRows,
  startsPerDay,
  triggerRows,
} from './model';

/** The panel with this title. */
const panel = (title: string) => {
  const section = screen.getByRole('heading', { name: title }).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

/** The Sources panel, once its answer is in. */
const sourcesPanel = async () => {
  await screen.findByRole('heading', { name: 'Sources' });
  return panel('Sources');
};

const open = async () => {
  const api = serveApi();
  renderConsole('/console/intake');
  await screen.findByRole('table', { name: 'Trigger log' });
  return api;
};

describe('intake model', () => {
  it('reads the trigger keys intake writes: label, comment and pull request', () => {
    expect(parseTrigger('label:example/web-shop#232:9981')).toEqual({
      kind: 'label',
      where: 'example/web-shop#232',
    });
    expect(parseTrigger('comment:example/payments-api:44102')).toEqual({
      kind: 'comment',
      where: 'example/payments-api, comment 44102',
    });
    expect(parseTrigger('example/payments-api#409')).toEqual({
      kind: 'pull request',
      where: 'example/payments-api#409',
    });
    expect(parseTrigger('example/payments-api#409:run-20261013-1')).toEqual({
      kind: 'pull request',
      where: 'example/payments-api#409',
    });
    expect(parseTrigger('something else')).toEqual({ kind: 'trigger', where: 'something else' });
  });

  it('lists the started tasks and waiting comments newest first, then the notices, which have no time', () => {
    const rows = triggerRows(adm.intake());
    expect(rows.map((r) => r.result)).toEqual([
      'waiting', // the two review comments, 5 and 8 minutes ago
      'waiting',
      'acted', // the five tasks, newest first
      'acted',
      'acted',
      'acted',
      'acted',
      'ignored', // the four notices, in the order the server gives them
      'waiting',
      'ignored',
      'ignored',
    ]);
    const timed = rows.filter((r) => r.at);
    expect(timed).toHaveLength(7);
    expect(timed.map((r) => Date.parse(r.at ?? ''))).toEqual(
      [...timed.map((r) => Date.parse(r.at ?? ''))].sort((a, b) => b - a),
    );
    expect(rows.slice(-4).every((r) => r.at === null)).toBe(true);
  });

  it('gives every notice the reason intake wrote on the issue, and calls "wait" and "busy" waiting', () => {
    const notices = triggerRows(adm.intake()).filter((r) => r.at === null);
    const byReason = (text: RegExp) => notices.find((r) => text.test(r.reason));
    expect(byReason(/cap of starts/)?.result).toBe('ignored');
    expect(byReason(/free slot/)?.result).toBe('waiting');
    expect(byReason(/written by an allowed author/)?.result).toBe('ignored');
    expect(byReason(/review rounds of this pull request/)?.result).toBe('ignored');
    const odd = triggerRows(
      adm.intake({ notices: [{ reason: 'busy', trigger: 'a/b#1:run-1' }] }),
    ).find((r) => r.at === null);
    expect(odd?.result).toBe('waiting');
  });

  it('says a reason it does not know instead of inventing one', () => {
    const [row] = triggerRows(
      adm.intake({
        tasks: [],
        pending_reviews: [],
        notices: [{ reason: 'novel', trigger: 'a/b#2' }],
      }),
    );
    expect(row?.reason).toBe('Reason “novel”.');
  });

  it('filters acted and not acted', () => {
    const rows = triggerRows(adm.intake());
    expect(filterRows(rows, 'all')).toHaveLength(rows.length);
    expect(filterRows(rows, 'acted').every((r) => r.result === 'acted')).toBe(true);
    expect(filterRows(rows, 'not-acted').every((r) => r.result !== 'acted')).toBe(true);
    expect(filterRows(rows, 'acted').length + filterRows(rows, 'not-acted').length).toBe(
      rows.length,
    );
  });

  it('groups review rounds by pull request with the latest round and the waiting comments', () => {
    const [round, ...rest] = reviewRounds(adm.intake());
    expect(rest).toEqual([]);
    expect(round).toMatchObject({
      repository: 'example/payments-api',
      pull: 412,
      round: 2,
      phase: 'done',
      pending: 2,
    });
    const queued = reviewRounds(adm.intake({ tasks: [] }));
    expect(queued[0]).toMatchObject({ round: 0, phase: 'queued', pending: 2 });
  });

  it('counts starts per day in Kyiv time over seven days, and marks a day at the cap', () => {
    const now = Date.UTC(2026, 9, 13, 12, 0, 0);
    const at = (iso: string): IntakeTaskBody =>
      ({
        ...adm.intake().tasks[0],
        started: iso,
      }) as IntakeTaskBody;
    const bars = startsPerDay(
      [
        at('2026-10-13T08:00:00Z'),
        at('2026-10-13T20:00:00Z'),
        at('2026-10-12T22:00:00Z'), // 01:00 on the 13th in Kyiv
        at('2026-10-12T10:00:00Z'),
        at('2026-10-01T10:00:00Z'), // outside the window
      ],
      3,
      now,
    );
    expect(bars).toHaveLength(7);
    expect(bars.map((b) => b.label)).toEqual(['7', '8', '9', '10', '11', '12', '13']);
    expect(bars.map((b) => b.count)).toEqual([0, 0, 0, 0, 0, 1, 3]);
    expect(bars.map((b) => b.atCap)).toEqual([false, false, false, false, false, false, true]);
  });

  it('gives every repository the profile names a policy: always_ask wins, listed modes hold, the rest is off', () => {
    const integrator = adm.systemFull().profile?.driven?.integrator;
    const rows = policyRows(integrator, ['Example/Payments-API', 'example/other'], []);
    expect(rows.map((r) => [r.repo, r.mode, r.alwaysAsk, r.watched])).toEqual([
      ['example/infra', 'ask', true, false],
      ['example/other', 'off', false, true],
      ['example/payments-api', 'merge', false, true],
      ['example/web-shop', 'ask', false, false],
    ]);
    expect(policyRows(null, [], [])).toEqual([]);
  });

  it('summarises what the integrator did from the pull requests it knows', () => {
    const base = pulls().pulls;
    const text = recentActions(base.map((p) => ({ ...p, repository: 'example/payments-api' })));
    expect(text).toContain('merged #409, #407');
    expect(text).toContain('waiting for you: #412');
    expect(recentActions([])).toBe('No pull request of chargehand’s in the last 14 days.');
    const none: PullRequestBody[] = [];
    expect(recentActions(none)).toMatch(/last 14 days/);
  });
});

describe('Intake with data', () => {
  it('shows the cap on starts in the last 24 hours and the seven days as bars', async () => {
    await open();
    const starts = panel('Starts per day');
    const meter = starts.getByRole('meter');
    expect(meter.getAttribute('aria-label')).toBe('Starts in the last 24 hours');
    expect(within(meter).getByText('7 of 10')).toBeTruthy();
    expect(starts.getByText(/3 starts left in this window/)).toBeTruthy();
    const chart = starts.getByRole('img', { name: /^Starts per day, last 7 days: / });
    expect(chart.getAttribute('aria-label')).toMatch(/; cap 10$/);
    expect(chart.querySelectorAll('rect')).toHaveLength(7);
    expect(starts.getByText(/Counted from the 5 tasks the state file lists/)).toBeTruthy();
    expect(screen.getByText(/Polling every 120 s/)).toBeTruthy();
    expect(screen.getByText(/state saved/)).toBeTruthy();
  });

  it('says when the cap is full and when the next start is possible', async () => {
    const now = Date.now();
    serveApi({
      intake: adm.intake(
        {
          starts: {
            last24h: 10,
            cap: 10,
            remaining: 0,
            next_slot_at: new Date(now + 3 * 3600_000).toISOString(),
          },
        },
        now,
      ),
    });
    renderConsole('/console/intake');
    await screen.findByRole('table', { name: 'Trigger log' });
    expect(panel('Starts per day').getByRole('status').textContent).toMatch(
      /The cap is full: the next start is possible at (\d{1,2} [A-Z][a-z]{2} )?\d\d:\d\d/,
    );
    expect(panel('Starts per day').getByRole('meter').getAttribute('data-level')).toBe('danger');
  });

  it('lists the trigger log with what intake did and why, and a dash where no time was recorded', async () => {
    await open();
    const log = within(screen.getByRole('table', { name: 'Trigger log' }));
    const rows = log.getAllByRole('row').slice(1);
    expect(rows).toHaveLength(11);
    expect(
      panel('Trigger log').getByText(/Handled so far: 48 \(20 labels, 18 comments/),
    ).toBeTruthy();

    const cap = rows.find((r) => /example\/web-shop#232/.test(r.textContent ?? ''));
    expect(cap).toBeTruthy();
    const cells = within(cap as HTMLElement).getAllByRole('cell');
    expect(cells).toHaveLength(4);
    expect(cells[0]?.textContent).toBe('—');
    expect(cells[1]?.textContent).toBe('labelexample/web-shop#232');
    expect(cells[2]?.textContent).toBe('Ignored');
    expect(cells[3]?.textContent).toMatch(/cap of starts in 24 hours was used/);

    const acted = rows.find((r) => /example\/payments-api#231/.test(r.textContent ?? ''));
    const actedCells = within(acted as HTMLElement).getAllByRole('cell');
    expect(actedCells[2]?.textContent).toBe('Acted');
    expect(actedCells[3]?.textContent).toMatch(/Started run 1fdf01, task issue-231/);

    const review = rows.find((r) =>
      /Comment by octocat on src\/Retry\.cs:41/.test(r.textContent ?? ''),
    );
    expect(review).toBeTruthy();
    expect(within(review as HTMLElement).getByText('Waiting')).toBeTruthy();
    expect(panel('Trigger log').getAllByText(/The text is not returned/)).toHaveLength(2);
  });

  it('filters the log to what intake acted on, and to what it did not', async () => {
    await open();
    const filter = screen.getByRole('group', { name: 'Filter the trigger log' });
    fireEvent.click(within(filter).getByRole('button', { name: 'Acted' }));
    expect(within(filter).getByRole('button', { name: 'Acted' }).getAttribute('aria-pressed')).toBe(
      'true',
    );
    let rows = within(screen.getByRole('table', { name: 'Trigger log' }))
      .getAllByRole('row')
      .slice(1);
    expect(rows).toHaveLength(5);
    fireEvent.click(within(filter).getByRole('button', { name: 'Not acted' }));
    rows = within(screen.getByRole('table', { name: 'Trigger log' }))
      .getAllByRole('row')
      .slice(1);
    expect(rows).toHaveLength(6);
    expect(
      within(screen.getByRole('table', { name: 'Trigger log' })).queryByText('Acted'),
    ).toBeNull();
  });

  it('shows an empty log with the way to fill it, and an empty filter with the way back', async () => {
    serveApi({
      intake: adm.intake({
        tasks: [],
        pending_reviews: [],
        notices: [],
        handled: { total: 0, labels: 0, comments: 0, reviews: 0, rounds: 0 },
      }),
    });
    renderConsole('/console/intake');
    expect(await screen.findByText('No trigger to show')).toBeTruthy();
    expect(screen.getByText('No trigger handled yet.')).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Trigger log' })).toBeNull();
  });

  it('shows the rules intake runs by, and says so when nobody may trigger it', async () => {
    await open();
    const rules = panel('Rules');
    expect(rules.getByText('chargehand (the person who applied it must be allowed)')).toBeTruthy();
    expect(rules.getByText('@chargehand (the comment author must be allowed)')).toBeTruthy();
    expect(rules.getByText('octocat')).toBeTruthy();
    expect(rules.getByText(/from the issue’s size:S, size:M or size:L label, else M/)).toBeTruthy();
    expect(
      rules.getByText('2M tokens, $5.00; 2 concurrent; 10 starts per day; 3 review rounds'),
    ).toBeTruthy();
    expect(rules.getByText('edit later')).toBeTruthy();
  });

  it('warns that an empty author list means nothing triggers', async () => {
    serveApi({ intake: adm.intake({ allowed_authors: [] }) });
    renderConsole('/console/intake');
    await screen.findByRole('table', { name: 'Trigger log' });
    expect(panel('Rules').getByText('none: nothing triggers')).toBeTruthy();
  });

  it('lists the watched repositories with their path and the integrator mode each has', async () => {
    await open();
    const watched = panel('Watched repositories');
    const items = watched.getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(within(items[0] as HTMLElement).getByText('example/payments-api')).toBeTruthy();
    expect(within(items[0] as HTMLElement).getByText('/srv/work/payments-api')).toBeTruthy();
    const modes = items.map((i) => i.querySelector('.ch-badge')?.getAttribute('data-mode'));
    await waitFor(() =>
      expect(
        panel('Watched repositories')
          .getAllByRole('listitem')
          .map((i) => i.querySelector('.ch-badge')?.getAttribute('data-mode')),
      ).toEqual(['merge', 'ask', 'ask']),
    );
    expect(modes).toHaveLength(3);
  });

  it('shows the integrator policy per repository with always_ask, the pull requests it acted on, and the CI fixer limits', async () => {
    await open();
    const table = await screen.findByRole('table', { name: 'Integrator policy' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    const byName = (name: string) => rows.find((r) => within(r).queryByText(name)) as HTMLElement;
    const infra = within(byName('example/infra')).getAllByRole('cell');
    expect(infra[1]?.querySelector('.ch-badge')?.getAttribute('data-mode')).toBe('ask');
    expect(infra[2]?.textContent).toBe('yes');
    expect(infra[3]?.textContent).toMatch(/No pull request of chargehand’s/);
    const payments = within(byName('example/payments-api')).getAllByRole('cell');
    expect(payments[1]?.querySelector('.ch-badge')?.getAttribute('data-mode')).toBe('merge');
    expect(payments[2]?.textContent).toBe('—');
    expect(payments[3]?.textContent).toBe('merged #409; waiting for you: #412');
    expect(within(byName('example/web-shop')).getAllByRole('cell')[3]?.textContent).toBe(
      'merged #407',
    );
    expect(within(byName('example/web-shop')).getByText('later')).toBeTruthy();
    const policy = panel('Integrator policy');
    expect(policy.getByText('max_attempts 3')).toBeTruthy();
    expect(policy.getByText('max_turns 20')).toBeTruthy();
    expect(policy.getByText('max_minutes 15')).toBeTruthy();
    expect(policy.getByText('wait_minutes 20')).toBeTruthy();
  });

  it('lists the review rounds with the comments waiting, the cap, and a link only to an http address', async () => {
    await open();
    const rounds = panel('Review rounds');
    const link = rounds.getByRole('link', { name: 'example/payments-api#412' });
    expect(link.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/412');
    expect(rounds.getByText('2 comments waiting')).toBeTruthy();
    expect(rounds.getByText('round 2 of 3')).toBeTruthy();
    expect(rounds.getByText(/Intake allows 3 rounds per pull request/)).toBeTruthy();
    await waitFor(() =>
      expect(
        panel('Review rounds').getByText(/shared with the CI fixer \(3 attempts in all\)/),
      ).toBeTruthy(),
    );
  });

  it('never links a pull request whose address is not http or https', async () => {
    const body = adm.intake();
    serveApi({
      intake: {
        ...body,
        tasks: body.tasks.map((t) =>
          t.round != null ? { ...t, pr_url: 'javascript:alert(1)' } : t,
        ),
      },
    });
    renderConsole('/console/intake');
    await screen.findByRole('table', { name: 'Trigger log' });
    expect(panel('Review rounds').queryByRole('link')).toBeNull();
    expect(panel('Review rounds').getByText('example/payments-api#412')).toBeTruthy();
  });

  it('lists what the server does not record, in its own words', async () => {
    await open();
    const section = panel('Not recorded by the server');
    expect(section.getAllByRole('listitem')).toHaveLength(3);
    expect(section.getByText(/Handled keys and notices are not timestamped/)).toBeTruthy();
  });

  it('puts each reason on its own line on a phone, with no table', async () => {
    viewport(true);
    serveApi();
    renderConsole('/console/intake');
    await screen.findByText(/Handled so far: 48/);
    const log = screen.getByRole('heading', { name: 'Trigger log' });
    const items = within(log.closest('section') as HTMLElement).getAllByRole('listitem');
    expect(items).toHaveLength(11);
    expect(screen.queryByRole('table', { name: 'Trigger log' })).toBeNull();
    expect(items.some((i) => /cap of starts in 24 hours was used/.test(i.textContent ?? ''))).toBe(
      true,
    );
  });
});

describe('Intake states', () => {
  it('shows skeletons while the first answer is on its way', async () => {
    serveApi({ hang: ['/v1/intake'] });
    renderConsole('/console/intake');
    expect(
      await screen.findByRole('heading', { name: 'Intake and automation', level: 1 }),
    ).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(3);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('names the failing call and offers Retry', async () => {
    const { calls } = serveApi({
      replies: { '/v1/intake': [503, { errors: ['the state file is locked'] }] },
    });
    renderConsole('/console/intake');
    const box = (await screen.findByText('Cannot read intake')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/intake').className).toContain('mono');
    expect(box.textContent).toContain('the state file is locked');
    const before = calls.filter((c) => c.path === '/v1/intake').length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/v1/intake').length).toBeGreaterThan(before),
    );
  });

  it('says intake is off when the profile has no automation.github', async () => {
    serveApi({ intake: adm.intakeOff() });
    renderConsole('/console/intake');
    expect(await screen.findByText('GitHub intake is off')).toBeTruthy();
    expect(screen.getByText('automation.github', { exact: false })).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Trigger log' })).toBeNull();
    expect(screen.getByText('Off')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Not recorded by the server' })).toBeTruthy();
  });

  it('keeps the rest of the screen when the integrator policy cannot be read', async () => {
    serveApi({ replies: { '/v1/system': [503, { errors: ['runner timed out'] }] } });
    renderConsole('/console/intake');
    await screen.findByRole('table', { name: 'Trigger log' });
    const box = (await screen.findByText('Cannot read the integrator policy')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/system')).toBeTruthy();
    expect(screen.getByRole('table', { name: 'Trigger log' })).toBeTruthy();
    expect(panel('Rules').getByText('octocat')).toBeTruthy();
  });

  it('says when the profile has no integrator, so a draft pull request is left alone', async () => {
    const full = adm.systemFull();
    serveApi({
      system: {
        ...full,
        profile: full.profile && {
          ...full.profile,
          driven: full.profile.driven && { ...full.profile.driven, integrator: null },
        },
      },
    });
    renderConsole('/console/intake');
    await screen.findByRole('table', { name: 'Trigger log' });
    expect(
      await screen.findByText(
        (_, el) =>
          el?.tagName === 'P' && /has no driven\.integrator block/.test(el.textContent ?? ''),
      ),
    ).toBeTruthy();
    const rows = within(await screen.findByRole('table', { name: 'Integrator policy' }))
      .getAllByRole('row')
      .slice(1);
    expect(
      rows.map((r) =>
        within(r).getAllByRole('cell')[1]?.querySelector('.ch-badge')?.getAttribute('data-mode'),
      ),
    ).toEqual(['off', 'off', 'off']);
  });
});

describe('Intake for an account', () => {
  it('says the screen is owner only, and never calls the routes that would answer 403', async () => {
    const { calls } = serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/intake');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Intake and automation', level: 1 })).toBeTruthy();
    expect(screen.getByText('GET /v1/intake')).toBeTruthy();
    expect(screen.getByText('ci-bot')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Go to Overview' }).getAttribute('href')).toBe(
      '/console/',
    );
    expect(screen.queryByRole('table', { name: 'Trigger log' })).toBeNull();
    expect(calls.some((c) => c.path === '/v1/intake')).toBe(false);
    expect(calls.some((c) => c.path === '/v1/system')).toBe(false);
    expect(calls.some((c) => c.path === '/v1/intake/sources')).toBe(false);
  });

  it('shows the same state when the server answers 403 to a key it took for the owner’s', async () => {
    serveApi({ replies: { '/v1/intake': [403, { errors: ['owner only'] }] } });
    renderConsole('/console/intake');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('leaves the screen out of the navigation', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/intake');
    await screen.findByText('This screen needs the owner key');
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(within(nav).queryByRole('link', { name: 'Intake and automation' })).toBeNull();
    expect(within(nav).queryByRole('link', { name: 'Accounts and keys' })).toBeNull();
    expect(within(nav).queryByRole('link', { name: 'System' })).toBeNull();
    expect(within(nav).getByRole('link', { name: 'Repositories' })).toBeTruthy();
  });
});

describe('Intake sources', () => {
  it('lists GitHub repositories, each with its queue', () => {
    const rows = sourceRows(adm.intakeSources());
    expect(rows.map((r) => [r.name, r.enabled])).toEqual([
      ['example/payments-api', true],
      ['example/web-shop', true],
      ['example/notes', false],
    ]);
    expect(rows.map(queueText)).toEqual([
      '1 running, 2 queued',
      '0 running, 0 queued',
      '0 running, 0 queued',
    ]);
    expect(rows[2]?.detail).toContain('discovered');
  });

  it('reads a source with no queue recorded as 0', () => {
    const rows = sourceRows({
      contract_version: 'intake-sources/v1',
      configured: true,
      discover: false,
      sources: [{ repository: 'example/a', enabled: true, origin: 'listed', path: '/srv/a' }],
    });
    expect(rows.map(queueText)).toEqual(['0 running, 0 queued']);
  });

  it('shows each source with a switch, its queue and links to its recent runs', async () => {
    await open();
    const sources = await sourcesPanel();
    const table = await sources.findByRole('table', { name: 'Sources' });
    expect(within(table).getByText('1 running, 2 queued')).toBeTruthy();
    expect(
      (within(table).getByRole('switch', { name: 'Watch example/notes' }) as HTMLInputElement)
        .checked,
    ).toBe(false);
    const run = within(table).getByRole('link', { name: /#231 running/ });
    expect(run.getAttribute('href')).toBe('/console/runs/run-20261013-124239-1fdf01');
  });

  it('switches a repository off with one PUT', async () => {
    const api = await open();
    const sources = await sourcesPanel();
    const repo = (await sources.findByRole('switch', {
      name: 'Watch example/web-shop',
    })) as HTMLInputElement;
    fireEvent.click(repo);
    await waitFor(() => expect(repo.checked).toBe(false));
    expect(api.calls.filter((c) => c.method === 'PUT').map((c) => [c.path, c.body])).toEqual([
      ['/v1/intake/sources', { sources: [{ repository: 'example/web-shop', enabled: false }] }],
    ]);
  });

  it('says when a switch was not set and leaves it as it was', async () => {
    serveApi({
      replies: {
        'PUT /v1/intake/sources': [503, { errors: ['the switches could not be written'] }],
      },
    });
    renderConsole('/console/intake');
    const sources = await sourcesPanel();
    const repo = (await sources.findByRole('switch', {
      name: 'Watch example/web-shop',
    })) as HTMLInputElement;
    fireEvent.click(repo);
    const alert = await sources.findByRole('alert');
    expect(alert.textContent).toContain('the switches could not be written');
    expect(repo.checked).toBe(true);
  });

  it('puts each source on its own card on a phone, with no table', async () => {
    viewport(true);
    serveApi();
    renderConsole('/console/intake');
    const sources = await sourcesPanel();
    expect(await sources.findByRole('switch', { name: 'Watch example/payments-api' })).toBeTruthy();
    expect(sources.queryByRole('table')).toBeNull();
    expect(sources.getAllByRole('listitem')).toHaveLength(3);
  });

  it('leaves the panel out when GitHub intake is not configured', async () => {
    serveApi({
      intake: adm.intakeOff(),
      intakeSources: adm.intakeSources({ configured: false, sources: [] }),
    });
    renderConsole('/console/intake');
    await screen.findByText('GitHub intake is off');
    expect(screen.queryByRole('heading', { name: 'Sources' })).toBeNull();
  });
});
