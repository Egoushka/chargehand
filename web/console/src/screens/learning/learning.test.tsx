import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { BatchBody } from '../../api/batch';
import type { SystemBody } from '../../api/system';
import type { LearnGroup, LearnReport } from '../../api/types';
import { budgetsInForce, describeBudget, describeBudgetSource } from '../../lib/budgets';
import { me, system } from '../../testing/fixtures';
import { finishedBatch, finishedRuns, learnReport, RETRO_TEXT } from '../../testing/learning';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  DEFAULT_RULE,
  historyRule,
  learnCellSource,
  learnRows,
  lessons,
  MAX_LESSONS,
  MAX_PER_KIND,
  MAX_RETRO_LINES,
  parseRetro,
  pickLessons,
  rankLessons,
  retros,
} from './model';

const now = () => Date.now();

/** A group of the learning report with the figures that matter set and the rest at "nothing". */
const group = (over: Partial<LearnGroup> = {}): LearnGroup => ({
  repository: 'acme/ledger',
  size_class: 'M',
  tasks: 10,
  spend_known: 10,
  spend_median_usd: 0.9,
  spend_p80_usd: 1.2,
  completed: 8,
  partial: 1,
  completion_rate: 0.8,
  partial_rate: 0.1,
  checkpointed: 0,
  checkpoint_rate: 0,
  checkpointed_completed: 0,
  checkpointed_completed_rate: null,
  prs: { total: 0, merged_unchanged: 0, merged_edited: 0, merged: 0, closed: 0, open: 0 },
  merged_unchanged_rate: null,
  ...over,
});

const profileSystem = (history?: object, retro?: boolean): SystemBody =>
  ({
    ...system(),
    profile: {
      driven: {
        governor: history ? { history } : {},
        learning:
          retro === undefined
            ? undefined
            : { retro, retro_preset: 'cheap', pr_days: 14, pr_check_minutes: 60 },
      },
    },
  }) as unknown as SystemBody;

// ---------- the rule ----------

describe('historyRule', () => {
  it('is the documented default for a key that cannot read the profile, and says so', () => {
    expect(historyRule(null)).toEqual(DEFAULT_RULE);
    expect(historyRule(system())).toEqual({
      minTasks: 20,
      windowDays: 60,
      percentile: 80,
      from: 'default',
    });
  });

  it('is the profile’s own setting when the owner’s system view has it', () => {
    expect(historyRule(profileSystem({ min_tasks: 10, window_days: 30, percentile: 90 }))).toEqual({
      minTasks: 10,
      windowDays: 30,
      percentile: 90,
      from: 'profile',
    });
  });
});

// ---------- the table ----------

describe('learnRows', () => {
  it('says in words whether a group has enough priced tasks for a budget from history, and which percentile it would use', () => {
    const [enough, thin] = learnRows(
      [
        group({ spend_known: 45, tasks: 48 }),
        group({ size_class: 'L', spend_known: 11, tasks: 12 }),
      ],
      DEFAULT_RULE,
    );
    expect(enough?.enough).toBe(true);
    expect(enough?.history).toBe('p80 of 45 priced tasks');
    expect(thin?.enough).toBe(false);
    expect(thin?.history).toBe('11 of 20 priced tasks: flat budget');
  });

  it('formats the shares with their counts, and a dash for what has no denominator', () => {
    const [row] = learnRows(
      [
        group({
          tasks: 48,
          completed: 30,
          completion_rate: 0.625,
          partial_rate: 0.104,
          checkpointed: 4,
          checkpoint_rate: 0.083,
          checkpointed_completed: 2,
          prs: { total: 40, merged_unchanged: 30, merged_edited: 8, merged: 1, closed: 0, open: 1 },
          merged_unchanged_rate: 0.769,
        }),
      ],
      DEFAULT_RULE,
    );
    expect(row?.completed).toBe('63% (30)');
    expect(row?.partial).toBe('10%');
    expect(row?.checkpoint).toBe('8.3%');
    expect(row?.thenDone).toBe('2 of 4');
    expect(row?.merged).toBe('77% (39)');
    expect(row?.median).toBe('≈ $0.90');
    const [none] = learnRows(
      [group({ spend_median_usd: null, spend_p80_usd: null })],
      DEFAULT_RULE,
    );
    expect(none?.median).toBe('—');
    expect(none?.p80).toBe('—');
    expect(none?.thenDone).toBe('—');
    expect(none?.merged).toBe('—');
  });

  it('names the route and the field of a cell', () => {
    expect(learnCellSource(group(), 'spend_p80_usd')).toBe(
      'GET /v1/learn · groups[acme/ledger, M] · spend_p80_usd',
    );
  });
});

// ---------- budgets in force ----------

describe('budgets in force', () => {
  const t = Date.parse('2026-10-13T12:00:00Z');
  const budgetOf = (batches: BatchBody[]) => budgetsInForce(batches);

  it('takes, per size class, the budget of the newest task that had one, and says which batch and task it is read from', () => {
    const older = finishedBatch('run-old', t, {
      minutesAgo: 600,
      sources: { M: { kind: 'flat' } },
    });
    const newer = finishedBatch('run-new', t, { minutesAgo: 30 });
    const found = budgetOf([older, newer]);
    expect(found.map((b) => b.size)).toEqual(['S', 'M', 'L']);
    const m = found.find((b) => b.size === 'M');
    expect(m).toMatchObject({ usd: 1.12, runId: 'run-new', taskId: 't2-m' });
    expect(m?.source).toEqual({ kind: 'history', n: 23, percentile: 80, scope: 'repository' });
  });

  it('leaves out a task with no governor and a size class no task shows', () => {
    const batch = finishedBatch('run-a', t);
    const [s, , l] = batch.tasks;
    const bare = {
      ...batch,
      tasks: [s ? { ...s, budget: undefined } : s, l].filter(Boolean),
    } as BatchBody;
    const found = budgetOf([bare]);
    expect(found.map((b) => b.size)).toEqual(['L']);
    expect(budgetOf([])).toEqual([]);
  });

  it('describes where a budget came from in the words of ADR 0045', () => {
    expect(
      describeBudgetSource({ kind: 'history', n: 23, percentile: 80, scope: 'repository' }),
    ).toBe('p80 of 23 past tasks, this repository');
    expect(describeBudgetSource({ kind: 'history', n: 31, percentile: 80, scope: 'all' })).toBe(
      'p80 of 31 past tasks, all repositories',
    );
    expect(describeBudgetSource({ kind: 'history' })).toBe('a percentile');
    expect(describeBudgetSource({ kind: 'flat' })).toBe('flat budget of the size class');
    expect(describeBudgetSource({ kind: 'task' })).toBe('set by the request');
    expect(describeBudgetSource({ kind: 'none' })).toBe('no governor');
    expect(describeBudget({ usd: 4, source: { kind: 'flat' } })).toBe(
      '≈ $4.00 · flat budget of the size class',
    );
  });
});

// ---------- retros ----------

describe('retros', () => {
  const t = Date.now();
  const written = (id: string, text: string, minutesAgo = 60) =>
    finishedBatch(id, t, { minutesAgo, retro: { status: 'written', tasks: 3, text } });

  it('reads only the written and skipped artifacts, and ignores anything else a batch holds under retro', () => {
    expect(parseRetro({ status: 'written', tasks: 3, text: 'a' })).toEqual({
      status: 'written',
      reason: null,
      tasks: 3,
      text: 'a',
    });
    expect(parseRetro({ status: 'skipped', reason: 'no tasks' })).toMatchObject({
      status: 'skipped',
      reason: 'no tasks',
      text: '',
    });
    for (const bad of [null, undefined, 'written', ['written'], { status: 'running' }, {}])
      expect(parseRetro(bad)).toBeNull();
  });

  it('keeps the lines of a written retro as the model wrote them, at most twelve, with the blank ones gone', () => {
    const many = Array.from({ length: 20 }, (_, i) => `  line ${i + 1}  `).join('\n\n');
    const found = retros([written('run-a', many)]);
    expect(found.written).toHaveLength(1);
    expect(found.written[0]?.lines).toHaveLength(MAX_RETRO_LINES);
    expect(found.written[0]?.lines[0]).toBe('line 1');
    expect(found.written[0]).toMatchObject({
      runId: 'run-a',
      repository: 'payments-api',
      tasks: 3,
    });
  });

  it('counts a skipped retro apart with its reason, and a written one with nothing in it as neither', () => {
    const skipped = finishedBatch('run-b', t, {
      retro: { status: 'skipped', reason: 'budget used up' },
    });
    const empty = written('run-c', ' \n \n');
    const none = finishedBatch('run-d', t);
    const found = retros([written('run-a', 'one'), skipped, empty, none]);
    expect(found.written.map((r) => r.runId)).toEqual(['run-a']);
    expect(found.skipped).toEqual([{ runId: 'run-b', reason: 'budget used up' }]);
  });
});

// ---------- lessons ----------

describe('lessons', () => {
  const ids = (groups: LearnGroup[]) =>
    lessons(groups, DEFAULT_RULE).map((l) => l.id.split(':')[0]);

  it('say nothing about a group too small to say anything about', () => {
    const small = group({ tasks: 4, completed: 0, completion_rate: 0, spend_known: 30 });
    expect(ids([small])).toEqual([]);
  });

  it('notice a size class that rarely completes, with the counts and the interval the claim stands on', () => {
    const [lesson] = lessons(
      [group({ tasks: 10, completed: 3, partial: 2, completion_rate: 0.3, spend_known: 30 })],
      DEFAULT_RULE,
    );
    expect(lesson?.text).toBe('Tasks of size M in acme/ledger complete 30% of the time.');
    expect(lesson?.evidence).toMatch(/^3 of 10 tasks completed \(95% CI \d+–\d+%\), 2 partial\.$/);
    expect(lesson?.source).toBe('GET /v1/learn · groups[acme/ledger, M] · completed, tasks');
  });

  it('notice checkpoints that were wrong, and pull requests that people rewrite', () => {
    const wrongStops = group({ spend_known: 30, checkpointed: 6, checkpointed_completed: 4 });
    const rewritten = group({
      size_class: 'S',
      spend_known: 30,
      prs: { total: 8, merged_unchanged: 1, merged_edited: 5, merged: 0, closed: 2, open: 0 },
      merged_unchanged_rate: 0.125,
    });
    const found = lessons([wrongStops, rewritten], DEFAULT_RULE);
    expect(found.map((l) => l.id.split(':')[0]).sort()).toEqual([
      'early-checkpoint',
      'edited-merges',
    ]);
    expect(found.find((l) => l.id.startsWith('early-checkpoint'))?.evidence).toContain(
      '4 of 6 checkpointed tasks completed',
    );
    expect(found.find((l) => l.id.startsWith('edited-merges'))?.evidence).toContain(
      '1 of 8 resolved pull requests',
    );
  });

  it('say how many groups have too short a history for a budget of their own', () => {
    const [thin] = lessons(
      [group({ spend_known: 3 }), group({ size_class: 'L', spend_known: 25 })],
      DEFAULT_RULE,
    );
    expect(thin?.id).toBe('thin-history');
    expect(thin?.text).toBe('1 of 2 repository and size groups have fewer than 20 priced tasks.');
  });

  it('are ranked with the tasks that fail first and the history last, and a kind never fills the panel', () => {
    const many = Array.from({ length: 6 }, (_, i) =>
      group({
        repository: `acme/r${i}`,
        spend_known: 30,
        checkpointed: 4,
        checkpointed_completed: 3,
      }),
    );
    const low = group({
      repository: 'acme/low',
      tasks: 10,
      completed: 2,
      completion_rate: 0.2,
      spend_known: 30,
    });
    const ranked = rankLessons(
      lessons([...many, low, group({ size_class: 'L', spend_known: 2 })], DEFAULT_RULE),
    );
    expect(ranked[0]?.id.startsWith('low-completion')).toBe(true);
    expect(ranked[ranked.length - 1]?.id).toBe('thin-history');

    const { shown, more } = pickLessons(ranked);
    expect(shown.length).toBeLessThanOrEqual(MAX_LESSONS);
    const checkpoints = shown.filter((l) => l.id.startsWith('early-checkpoint'));
    expect(checkpoints).toHaveLength(MAX_PER_KIND);
    expect(shown.some((l) => l.id === 'thin-history')).toBe(true);
    expect(more).toBe(ranked.length - shown.length);
    expect(more).toBe(4);
  });
});

// ---------- the screen ----------

/** The finished batches the screen reads for the budgets in force and the retros; the newest two have a retro. */
const batchesFor = (
  retro: BatchBody['retro'] = { status: 'written', tasks: 3, text: RETRO_TEXT },
) => {
  const t = now();
  const runs = finishedRuns(t);
  return {
    runs,
    batches: Object.fromEntries(
      runs.map((r, i) => [
        r.run_id,
        finishedBatch(r.run_id, t, { retro: i < 2 ? retro : undefined, minutesAgo: 60 + i * 120 }),
      ]),
    ),
  };
};

const panel = async (title: string) => {
  const section = (await screen.findByRole('heading', { name: title })).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

describe('Learning', () => {
  it('lists every repository and size class with what its tasks cost and how they ended, and where each number is read from', async () => {
    const report = learnReport('60d', now());
    serveApi({ learn: report, ...batchesFor() });
    renderConsole('/console/learning');
    expect(await screen.findByRole('heading', { level: 1, name: 'Learning' })).toBeTruthy();

    const table = await screen.findByRole('table', {
      name: 'Budgets and outcomes by repository and size class',
    });
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(report.groups.length + 1);
    const first = report.groups[0] as LearnGroup;
    const cells = within(rows[1] as HTMLElement).getAllByRole('cell');
    expect(cells[0]?.textContent).toBe(first.repository);
    expect(cells[0]?.getAttribute('title')).toBe(first.repository);
    expect(cells[2]?.textContent).toBe(String(first.tasks));
    expect(cells[2]?.getAttribute('title')).toBe(
      `GET /v1/learn · groups[${first.repository}, ${first.size_class}] · tasks`,
    );
    expect(cells[4]?.textContent).toBe(`≈ $${(first.spend_p80_usd ?? 0).toFixed(2)}`);
    expect(cells[4]?.getAttribute('title')).toMatch(/spend_p80_usd$/);
    expect(
      screen.getByText(`${report.tasks} finished tasks in the last 60 days`, { exact: false }),
    ).toBeTruthy();
  });

  it('asks for the range that was picked', async () => {
    const api = serveApi({ ...batchesFor() });
    renderConsole('/console/learning');
    await screen.findByRole('table');
    expect(api.last('/v1/learn')?.path).toBe('/v1/learn?since=60d');
    expect(screen.getByRole('button', { name: '60 d' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: '7 d' }));
    await waitFor(() => expect(api.last('/v1/learn')?.path).toBe('/v1/learn?since=7d'));
    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    await waitFor(() => expect(api.last('/v1/learn')?.path).toBe('/v1/learn?since=36500d'));
  });

  it('says how a budget is chosen with the defaults when the key cannot read the profile, and with the profile’s numbers when it can', async () => {
    serveApi({ system: null, ...batchesFor() });
    const first = renderConsole('/console/learning');
    const note = await screen.findByText(/How budgets are chosen/);
    const banner = note.closest('.ch-banner') as HTMLElement;
    expect(banner.textContent).toContain('p80 of past spend of its size class in its repository');
    expect(banner.textContent).toContain(
      'The documented defaults (only the owner can read the profile)',
    );
    expect(banner.textContent).toContain('min_tasks 20, window_days 60, percentile 80');
    first.unmount();

    serveApi({
      system: profileSystem({ min_tasks: 10, window_days: 30, percentile: 90 }),
      ...batchesFor(),
    });
    renderConsole('/console/learning');
    await waitFor(() => expect(screen.getByText(/Settings from the profile/)).toBeTruthy());
    const owner = screen.getByText(/How budgets are chosen/).closest('.ch-banner') as HTMLElement;
    expect(owner.textContent).toContain('min_tasks 10, window_days 30, percentile 90');
    expect(owner.textContent).toContain('p90 of past spend');
  });

  it('shows the budget in force for each size class with its rule, the task it was read from, and a link to the batch', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/learning');
    const section = await panel('Budgets in force');
    expect(await section.findByText(/flat budget of the size class/)).toBeTruthy();
    expect(section.getByText(/p80 of 23 past tasks, this repository/)).toBeTruthy();
    expect(section.getByText(/p80 of 31 past tasks, all repositories/)).toBeTruthy();
    expect(section.getByText(/task t2-m of batch/)).toBeTruthy();
    const links = section.getAllByRole('link', { name: 'Open' });
    expect(links).toHaveLength(3);
    expect(links[0]?.getAttribute('href')).toMatch(/^\/console\/runs\/run-/);
  });

  it('shows a retro as plain text under a label that says it is a model’s advice, and never as markup', async () => {
    const hostile = [
      '<img src=x onerror=alert(1)> check the <b>retry</b> policy',
      '<script>window.pwned = 1</script>',
    ].join('\n');
    serveApi({ ...batchesFor({ status: 'written', tasks: 3, text: hostile }) });
    renderConsole('/console/learning');
    const section = await panel('Retros');
    expect(await section.findAllByText(/check the <b>retry<\/b> policy/)).toHaveLength(2);
    expect(section.getByText("a model's advice")).toBeTruthy();
    expect(section.getAllByText('<script>window.pwned = 1</script>')).toHaveLength(2);
    expect(document.querySelector('.ln-retro img, .ln-retro b, .ln-retro script')).toBeNull();
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
    expect(section.getAllByText('batch/v1 · retro.text')).toHaveLength(2);
  });

  it('shows each retro with its batch, repository and the number of task records', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/learning');
    const section = await panel('Retros');
    await section.findAllByText(/cost 1\.4x its budget/);
    const links = section.getAllByRole('link');
    expect(links).toHaveLength(2);
    expect(links[0]?.getAttribute('href')).toMatch(/^\/console\/runs\/run-/);
    expect(section.getAllByText('payments-api')).toHaveLength(2);
    expect(section.getAllByText('3 task records')).toHaveLength(2);
    expect(section.getAllByRole('listitem').length).toBeGreaterThanOrEqual(8);
  });

  it('says why there is no retro: the profile has it off, has it on, or the owner alone can tell', async () => {
    const none = { runs: finishedRuns(now()), batches: {} };
    const noRetro = (system: SystemBody | null) => {
      const t = now();
      const runs = finishedRuns(t);
      return {
        system,
        runs,
        batches: Object.fromEntries(runs.map((r) => [r.run_id, finishedBatch(r.run_id, t)])),
      };
    };
    expect(none.runs.length).toBeGreaterThan(0);

    serveApi(noRetro(profileSystem(undefined, false)));
    const off = renderConsole('/console/learning');
    expect(
      await screen.findByText(/The retro is off \(driven\.learning\.retro, the default\)/),
    ).toBeTruthy();
    off.unmount();

    serveApi(noRetro(profileSystem(undefined, true)));
    const on = renderConsole('/console/learning');
    expect(await screen.findByText(/The retro is on \(driven\.learning\.retro\)/)).toBeTruthy();
    on.unmount();

    serveApi(noRetro(null));
    renderConsole('/console/learning');
    expect(
      await screen.findByText(
        /A retro is written only when the profile turns driven\.learning\.retro on/,
      ),
    ).toBeTruthy();
  });

  it('lists the patterns a reader would see in the table, each with its evidence and where it is read from, and counts the rest', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/learning');
    const section = await panel('Lessons');
    const items = await section.findAllByRole('article');
    expect(items.length).toBeGreaterThan(0);
    expect(items.length).toBeLessThanOrEqual(MAX_LESSONS);
    for (const item of items) {
      expect(within(item).getByText(/^Evidence:/)).toBeTruthy();
      expect(within(item).getByText('Try:')).toBeTruthy();
      expect(item.querySelector('.ln-src')?.textContent).toMatch(/^GET \/v1\/learn · groups/);
    }
    expect(section.getByText(/more patterns of the same kinds/)).toBeTruthy();
    expect(section.getByText('read off the table, not stored by the server')).toBeTruthy();
  });

  it('says there is nothing to learn from yet, and what the budget rule needs', async () => {
    const empty: LearnReport = { since: null, tasks: 0, groups: [] };
    serveApi({ learn: empty, ...batchesFor() });
    renderConsole('/console/learning');
    expect(await screen.findByText('Nothing learned yet')).toBeTruthy();
    expect(
      screen.getByText(
        /A size class needs 20 finished tasks in 60 days before its budget comes from history/,
      ),
    ).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('shows a skeleton while loading and, when the server fails, the call, the answer and a retry', async () => {
    serveApi({ hang: ['/v1/learn'] });
    const first = renderConsole('/console/learning');
    expect(await screen.findByText('Loading the learning report')).toBeTruthy();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(0);
    first.unmount();

    const api = serveApi({
      replies: { '/v1/learn': [403, { errors: ['this key may not read the report'] }] },
      ...batchesFor(),
    });
    renderConsole('/console/learning');
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Cannot read the learning report');
    expect(alert.textContent).toContain('GET /v1/learn?since=60d');
    expect(alert.textContent).toContain('this key may not read the report');
    const before = api.calls.filter((c) => c.path.startsWith('/v1/learn')).length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(api.calls.filter((c) => c.path.startsWith('/v1/learn')).length).toBeGreaterThan(
        before,
      ),
    );
  });

  it('is a list of cards, not a table, on a phone, with the same figures', async () => {
    viewport(true);
    const report = learnReport('60d', now());
    serveApi({ learn: report, ...batchesFor() });
    renderConsole('/console/learning');
    await screen.findByText('How budgets are chosen');
    await waitFor(() =>
      expect(screen.getAllByText(/ tasks · median /).length).toBe(report.groups.length),
    );
    expect(screen.queryByRole('table')).toBeNull();
    expect(document.querySelectorAll('.ch-seg--touch')).toHaveLength(1);
  });

  it('an account key sees the screen without the owner-only profile', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false }), system: null, ...batchesFor() });
    renderConsole('/console/learning');
    await screen.findByRole('table');
    expect(screen.getByText(/only the owner can read the profile/)).toBeTruthy();
  });
});
