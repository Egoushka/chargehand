import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AnalyticsBody, PrCounts, SeriesPoint } from '../../api/analytics';
import { SlotMemory } from '../../charts/colors';
import { formatInterval, wilson } from '../../charts/stats';
import type { BudgetInForce } from '../../lib/budgets';
import { me, run } from '../../testing/fixtures';
import {
  finishedBatch,
  finishedRuns,
  analyticsBody as generated,
  learnReport,
  REPOS,
} from '../../testing/learning';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  axisShare,
  axisUsd,
  bucketAxis,
  bucketLabel,
  bucketStart,
  bucketTitle,
  budgetAxisMax,
  budgetRows,
  budgetTable,
  cacheSeries,
  cacheShare,
  ciFixerNote,
  continuationView,
  durationBinLabel,
  durations,
  figures,
  groupLabel,
  learnSource,
  modeRows,
  passRows,
  stackSeries,
  stackView,
  sumByBucket,
  topDecisions,
} from './model';

// ---------- builders ----------

const NO_PRS: PrCounts = {
  total: 0,
  merged_unchanged: 0,
  merged_edited: 0,
  merged: 0,
  closed: 0,
  open: 0,
};

const point = (over: Partial<SeriesPoint> = {}): SeriesPoint => ({
  bucket_start: '2026-10-13T00:00:00Z',
  group: null,
  tasks: 0,
  completed: 0,
  partial: 0,
  failed: 0,
  needs_input: 0,
  pass_rate: null,
  usd_estimate: 0,
  usd_estimate_known: 0,
  input_tokens: 0,
  output_tokens: 0,
  cache_read_tokens: 0,
  cache_write_tokens: 0,
  turns: 0,
  continuations: 0,
  checkpointed: 0,
  checkpointed_completed: 0,
  prs: NO_PRS,
  ...over,
});

const withTotal = (total: Partial<SeriesPoint>): AnalyticsBody => {
  const body = generated({ since: '30d' });
  return { ...body, tasks: { ...body.tasks, total: point(total) } };
};

const day = (d: number) => Date.UTC(2026, 9, d);
const NOW = '2026-10-13T12:00:00Z';

// ---------- time ----------

describe('buckets', () => {
  it('start at midnight UTC, on the Monday of a week, on the first of a month', () => {
    const wednesday = Date.parse('2026-10-14T15:30:00Z');
    expect(new Date(bucketStart(wednesday, 'day')).toISOString()).toBe('2026-10-14T00:00:00.000Z');
    expect(new Date(bucketStart(wednesday, 'week')).toISOString()).toBe('2026-10-12T00:00:00.000Z');
    expect(new Date(bucketStart(wednesday, 'month')).toISOString()).toBe(
      '2026-10-01T00:00:00.000Z',
    );
    // A Sunday belongs to the week that began the Monday before.
    expect(new Date(bucketStart(Date.parse('2026-10-18T23:59:59Z'), 'week')).toISOString()).toBe(
      '2026-10-12T00:00:00.000Z',
    );
  });

  it('are named by the day or month, and by the long form on hover', () => {
    expect(bucketLabel(day(13), 'day')).toBe('13 Oct');
    expect(bucketLabel(day(12), 'week')).toBe('12 Oct');
    expect(bucketLabel(day(1), 'month')).toBe('Oct 2026');
    expect(bucketTitle(day(13), 'day')).toBe('Tue, 13 Oct 2026');
    expect(bucketTitle(day(12), 'week')).toBe('Week of Mon, 12 Oct 2026');
    expect(bucketTitle(day(1), 'month')).toBe('October 2026');
  });

  it('fill the axis from the start of the range to now, so a day with no task is a gap and not a missing bar', () => {
    const points = [
      { bucket_start: '2026-10-10T00:00:00Z' },
      { bucket_start: '2026-10-13T00:00:00Z' },
    ];
    const axis = bucketAxis(points, 'day', NOW, 7);
    expect(axis).toHaveLength(8);
    expect(axis[0]).toBe(day(6));
    expect(axis[axis.length - 1]).toBe(day(13));
    // Consecutive days: nothing skipped.
    expect(axis.every((t, i) => i === 0 || t - (axis[i - 1] ?? 0) === 86_400_000)).toBe(true);
  });

  it('start at the first record when the range is all time, and are empty with no record', () => {
    const points = [
      { bucket_start: '2026-10-10T00:00:00Z' },
      { bucket_start: '2026-10-13T00:00:00Z' },
    ];
    expect(bucketAxis(points, 'day', NOW, null)).toEqual([day(10), day(11), day(12), day(13)]);
    expect(bucketAxis([], 'day', NOW, 30)).toEqual([]);
  });

  it('step by week and by month', () => {
    const points = [{ bucket_start: '2026-08-31T00:00:00Z' }];
    expect(bucketAxis(points, 'week', NOW, 28)).toEqual([
      Date.UTC(2026, 7, 31),
      Date.UTC(2026, 8, 7),
      Date.UTC(2026, 8, 14),
      Date.UTC(2026, 8, 21),
      Date.UTC(2026, 8, 28),
      day(5),
      day(12),
    ]);
    expect(bucketAxis([{ bucket_start: '2026-08-01T00:00:00Z' }], 'month', NOW, null)).toEqual([
      Date.UTC(2026, 7, 1),
      Date.UTC(2026, 8, 1),
      Date.UTC(2026, 9, 1),
    ]);
  });
});

// ---------- stacked series ----------

describe('stackSeries', () => {
  const axis = [day(11), day(12), day(13)];
  const points = [
    point({ bucket_start: '2026-10-11T00:00:00Z', group: '/srv/a/payments', usd_estimate: 2 }),
    point({ bucket_start: '2026-10-11T00:00:00Z', group: '/srv/a/ledger', usd_estimate: 5 }),
    point({ bucket_start: '2026-10-13T00:00:00Z', group: '/srv/a/payments', usd_estimate: 4 }),
    point({ bucket_start: '2026-10-12T00:00:00Z', group: '/srv/a/idle', usd_estimate: 0 }),
  ];
  const spend = (p: SeriesPoint) => p.usd_estimate;

  it('makes one series per group aligned to the axis, the largest first, and leaves out a group that adds nothing', () => {
    const series = stackSeries(points, axis, 'repository', spend, new SlotMemory());
    expect(series.map((s) => s.label)).toEqual(['payments', 'ledger']);
    const byLabel = Object.fromEntries(series.map((s) => [s.label, s]));
    expect(byLabel.payments?.values).toEqual([2, 0, 4]);
    expect(byLabel.ledger?.values).toEqual([5, 0, 0]);
    expect(byLabel.payments?.total).toBe(6);
    // The hue follows the group: payments was seen first among the larger, and keeps its slot.
    expect(byLabel.payments?.slot).toBeGreaterThan(0);
    expect(byLabel.payments?.slot).not.toBe(byLabel.ledger?.slot);
  });

  it('keeps a group on its hue when a filter drops its neighbour', () => {
    const memory = new SlotMemory();
    const both = stackSeries(points, axis, 'repository', spend, memory);
    const ledger = both.find((s) => s.label === 'ledger')?.slot;
    const only = stackSeries(
      points.filter((p) => p.group !== '/srv/a/payments'),
      axis,
      'repository',
      spend,
      memory,
    );
    expect(only).toHaveLength(1);
    expect(only[0]?.slot).toBe(ledger);
  });

  it('folds the tail into one grey "Other" when there are more groups than hues', () => {
    const many = Array.from({ length: 11 }, (_, i) =>
      point({
        bucket_start: '2026-10-13T00:00:00Z',
        group: `g${String(i).padStart(2, '0')}`,
        usd_estimate: 11 - i,
      }),
    );
    const series = stackSeries(many, axis, 'account', spend, new SlotMemory());
    expect(series).toHaveLength(8);
    const other = series[series.length - 1];
    expect(other?.id).toBe('other');
    expect(other?.slot).toBe(0);
    expect(other?.label).toBe('Other (4 more)');
    // The four smallest (4 + 3 + 2 + 1 dollars) add up in the one series.
    expect(other?.total).toBe(10);
    const hues = series.slice(0, 7).map((s) => s.slot);
    expect(new Set(hues).size).toBe(7);
    expect(hues.every((h) => h >= 1 && h <= 8)).toBe(true);
  });

  it('names a group for a legend by what it is', () => {
    expect(groupLabel('/srv/work/payments-api', 'repository')).toBe('payments-api');
    expect(groupLabel('L', 'size')).toBe('Size L');
    expect(groupLabel('ci-bot', 'account')).toBe('ci-bot');
    expect(groupLabel(null, 'none')).toBe('All tasks');
    expect(groupLabel('', 'repository')).toBe('Unnamed');
  });

  it('adds every group per bucket for a figure that is not split', () => {
    expect(sumByBucket(points, axis, spend)).toEqual([7, 0, 4]);
  });
});

describe('stackView', () => {
  const axis = [day(12), day(13)];
  const series = [
    { id: 'a', label: 'payments', group: 'a', slot: 1, values: [3, 0], total: 3 },
    { id: 'b', label: 'ledger', group: 'b', slot: 2, values: [1.5, 2], total: 3.5 },
  ];
  const view = stackView({
    axis,
    bucket: 'day',
    series,
    format: (v) => `$${v.toFixed(2)}`,
    field: 'usd_estimate',
    name: 'Spend',
  });

  it('says in a hover what each series was, the sum, and the route and field the number is read from', () => {
    const tip = view.tip(0);
    expect(tip.title).toBe('Mon, 12 Oct 2026 (UTC)');
    expect(tip.rows).toEqual([
      { label: 'payments', value: '$3.00', slot: 1 },
      { label: 'ledger', value: '$1.50', slot: 2 },
    ]);
    expect(tip.total).toEqual({ label: 'Total', value: '$4.50' });
    expect(tip.source).toBe('analytics/v1 tasks.points · usd_estimate · bucket_start 2026-10-12');
    // A series that was nothing in the bucket is not listed.
    expect(view.tip(1).rows.map((r) => r.label)).toEqual(['ledger']);
  });

  it('has the same numbers as a table, one row per bucket', () => {
    expect(view.table.columns).toEqual(['Bucket (UTC)', 'payments', 'ledger', 'Total']);
    expect(view.table.rows).toEqual([
      ['12 Oct', '$3.00', '$1.50', '$4.50'],
      ['13 Oct', '$0.00', '$2.00', '$2.00'],
    ]);
  });
});

// ---------- cache ----------

describe('cache read share', () => {
  it('is the cache reads over input, reads and writes: the share of input the cache served', () => {
    expect(cacheShare(100, 800, 100)).toBeCloseTo(0.8, 10);
    expect(cacheShare(0, 0, 0)).toBeNull();
    expect(cacheShare(50, 0, 0)).toBe(0);
  });

  it('leaves a gap in a bucket with no input, never a zero', () => {
    const axis = [day(12), day(13)];
    const series = cacheSeries(
      [
        point({
          bucket_start: '2026-10-13T00:00:00Z',
          input_tokens: 100,
          cache_read_tokens: 300,
          cache_write_tokens: 100,
        }),
      ],
      axis,
    );
    expect(series[0]).toBeNull();
    expect(series[1]).toBeCloseTo(0.6, 10);
  });
});

// ---------- the four figures ----------

describe('figures', () => {
  const total = {
    tasks: 20,
    completed: 14,
    usd_estimate: 12.5,
    usd_estimate_known: 18,
    input_tokens: 1_000_000,
    output_tokens: 200_000,
    checkpointed: 4,
    checkpointed_completed: 1,
    prs: { total: 10, merged_unchanged: 5, merged_edited: 2, merged: 1, closed: 1, open: 1 },
  };

  it('are sums and shares of the total, each with the counts it stands on and its source field', () => {
    const f = figures(withTotal(total));
    expect(f.spend.value).toBe('≈ $12.50');
    expect(f.spend.sub).toBe('1.20M tokens in and out · 18 of 20 tasks priced');
    expect(f.spend.source).toBe('tasks.total.usd_estimate');
    expect(f.pass.value).toBe('70%');
    const ci = wilson(14, 20);
    expect(f.pass.sub).toBe(`14 of 20 tasks completed · 95% CI ${ci ? formatInterval(ci) : ''}`);
    expect(f.falseStop.value).toBe('25%');
    expect(f.falseStop.sub).toBe('1 of 4 checkpointed tasks still completed');
    expect(f.merged.value).toBe('8');
    expect(f.merged.sub).toBe('56% of resolved merged as chargehand left them · 1 open');
  });

  it('say so, with an em dash, when nothing could be priced or nothing happened', () => {
    const unpriced = figures(withTotal({ ...total, usd_estimate: 0, usd_estimate_known: 0 }));
    expect(unpriced.spend.value).toBe('—');
    expect(unpriced.spend.sub).toBe('1.20M tokens in and out · no task could be priced');
    const none = figures(withTotal({}));
    expect(none.spend.value).toBe('—');
    expect(none.spend.sub).toBe('no finished task');
    expect(none.pass.value).toBe('—');
    expect(none.falseStop.value).toBe('—');
    expect(none.falseStop.sub).toBe('no task hit a checkpoint');
    expect(none.merged.value).toBe('—');
    expect(none.merged.sub).toBe('no pull request opened');
  });
});

// ---------- pass rate by repository and size ----------

describe('passRows', () => {
  const group = (repository: string, size: string, tasks: number, completed: number) => ({
    ...(learnReport('60d').groups[0] as ReturnType<typeof learnReport>['groups'][number]),
    repository,
    size_class: size,
    tasks,
    completed,
  });

  it('puts the groups with the most tasks first, then by name, and counts what the limit leaves out', () => {
    const groups = [
      group('b', 'S', 5, 4),
      group('a', 'M', 20, 15),
      group('a', 'S', 5, 1),
      group('c', 'L', 0, 0),
    ];
    const { rows, more } = passRows(groups, 2);
    expect(rows.map((r) => `${r.repository}/${r.size}`)).toEqual(['a/M', 'a/S']);
    // A group with no task has no rate and is not drawn; of the other three, one is past the limit.
    expect(more).toBe(1);
    expect(rows[0]?.p.rate).toBe(0.75);
    expect(rows[0]?.p.lo).toBeLessThan(0.75);
    expect(rows[0]?.p.hi).toBeGreaterThan(0.75);
  });

  it('names where the rate comes from', () => {
    expect(learnSource(group('a', 'M', 20, 15))).toBe(
      'GET /v1/learn · groups[a, M] · completed over tasks',
    );
  });
});

// ---------- duration ----------

describe('durations', () => {
  const ended = Date.parse('2026-10-13T10:00:00Z');
  const batchRun = (minutes: number, endedAt = ended, over: Record<string, unknown> = {}) =>
    run(
      `run-${minutes}`,
      {
        status: 'completed',
        started_at: new Date(endedAt - minutes * 60_000).toISOString(),
        finished_at: new Date(endedAt).toISOString(),
        ...over,
      },
      endedAt,
    );

  it('bins wall time into minute and hour ranges, with the median and the 80th percentile of the batches', () => {
    const d = durations(
      [3, 10, 20, 45, 150].map((m) => batchRun(m)),
      null,
    );
    expect(d.n).toBe(5);
    expect(d.bins.map(durationBinLabel)).toEqual([
      '<5 m',
      '5–15 m',
      '15–30 m',
      '30–60 m',
      '1–2 h',
      '2–4 h',
      '4 h+',
    ]);
    expect(d.bins.map((b) => b.count)).toEqual([1, 1, 1, 1, 0, 1, 0]);
    expect(d.median).toBe(20);
    expect(d.p80).toBe(45);
  });

  it('drops a batch that ended before the range began, one with no end, and one that ends before it starts', () => {
    const old = batchRun(30, ended - 10 * 86_400_000);
    const running = batchRun(30, ended, { finished_at: null });
    const backwards = batchRun(30, ended, { started_at: new Date(ended + 60_000).toISOString() });
    const d = durations([batchRun(5), old, running, backwards], ended - 86_400_000);
    expect(d.n).toBe(1);
    expect(durations([], null)).toMatchObject({ n: 0, median: null, p80: null });
  });
});

// ---------- budget against actual ----------

describe('budgetRows', () => {
  const groups = learnReport('60d').groups;
  const budget = (size: 'S' | 'M' | 'L', usd: number): BudgetInForce => ({
    size,
    usd,
    source: { kind: 'flat' },
    runId: 'run-1',
    taskId: 't1',
    at: '2026-10-13T10:00:00Z',
  });

  it('pairs each size class with its budget and the p80 spend of the repositories, the dearest first', () => {
    const rows = budgetRows(groups, [budget('S', 1.5), budget('M', 1.12)]);
    expect(rows.map((r) => r.size)).toEqual(['S', 'M', 'L']);
    const s = rows.find((r) => r.size === 'S');
    expect(s?.budget?.usd).toBe(1.5);
    expect(s?.repos.map((r) => r.repository).sort()).toEqual([...REPOS].sort());
    const p80s = s?.repos.map((r) => r.p80 ?? 0) ?? [];
    expect(p80s).toEqual([...p80s].sort((a, b) => b - a));
    // L has spend but no budget seen: still a row, with nothing to compare.
    expect(rows.find((r) => r.size === 'L')?.budget).toBeNull();
  });

  it('leaves out a size class with neither a budget nor a spend', () => {
    expect(budgetRows([], [budget('M', 2)]).map((r) => r.size)).toEqual(['M']);
    expect(budgetRows([], [])).toEqual([]);
  });

  it('scales the axis to the worst repository, at least to the budget', () => {
    const rows = budgetRows(groups, [budget('S', 0.1)]);
    const worst = Math.max(...(rows[0]?.repos.map((r) => (r.p80 ?? 0) / 0.1) ?? [0]));
    expect(budgetAxisMax(rows)).toBe(Math.max(1, worst));
    expect(budgetAxisMax([])).toBe(1);
  });

  it('has the same numbers as a table, with a dash where there is no budget', () => {
    const table = budgetTable(budgetRows(groups, [budget('S', 1.5)]));
    expect(table.columns).toEqual([
      'Size',
      'Repository',
      'Priced tasks',
      'Median',
      'p80',
      'Budget',
    ]);
    const first = table.rows.find((r) => r[0] === 'S');
    expect(first?.[5]).toBe('≈ $1.50');
    expect(table.rows.find((r) => r[0] === 'L')?.[5]).toBe('—');
  });
});

// ---------- governor and continuations ----------

describe('the governor', () => {
  it('shows a mode with its false-stop rate and interval, and none that had no checkpointed task', () => {
    const rows = modeRows([
      {
        mode: 'observe',
        tasks: 30,
        checkpointed: 10,
        checkpointed_completed: 7,
        false_stop_rate: 0.7,
      },
      {
        mode: 'enforce',
        tasks: 8,
        checkpointed: 0,
        checkpointed_completed: 0,
        false_stop_rate: null,
      },
    ]);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ mode: 'observe', tasks: 30, checkpointed: 10, completed: 7 });
    expect(rows[0]?.p.rate).toBe(0.7);
  });

  it('lists the decisions most frequent first and drops the ones that never happened', () => {
    const top = topDecisions(
      [
        { signal: 'turns', action: 'warn', count: 2, acted: 0 },
        { signal: 'spend', action: 'checkpoint', count: 9, acted: 3 },
        { signal: 'idle', action: 'stop', count: 0, acted: 0 },
        { signal: 'cache', action: 'warn', count: 2, acted: 1 },
      ],
      3,
    );
    expect(top.map((d) => `${d.signal}/${d.action}`)).toEqual([
      'spend/checkpoint',
      'cache/warn',
      'turns/warn',
    ]);
  });
});

describe('continuations', () => {
  it('recovers the counts under each completion rate, so the interval is the one for that many tasks', () => {
    const body = generated({ since: '30d' });
    const view = continuationView({
      ...body,
      tasks: {
        ...body.tasks,
        tasks: 100,
        continuations: {
          tasks_with_continuation: 4,
          continuations: 6,
          completion_rate: 0.5,
          completion_rate_without: 0.75,
        },
      },
    });
    expect(view).toMatchObject({ withCount: 4, withoutCount: 96, sessions: 6 });
    expect(view.withRate).toMatchObject({ k: 2, n: 4 });
    expect(view.withoutRate).toMatchObject({ k: 72, n: 96 });
  });

  it('has no rate for a side with no task', () => {
    const body = generated({ since: '30d' });
    const view = continuationView({
      ...body,
      tasks: {
        ...body.tasks,
        tasks: 5,
        continuations: { tasks_with_continuation: 0, continuations: 0 },
      },
    });
    expect(view.withRate).toBeNull();
    expect(view.withoutRate?.n).toBe(5);
  });

  it('takes the CI fixer line from what the server says is not recorded, else says so itself', () => {
    expect(ciFixerNote(['x', 'The CI fixer was not recorded.'])).toBe(
      'The CI fixer was not recorded.',
    );
    expect(ciFixerNote([])).toMatch(/not recorded/);
  });
});

describe('axis formats', () => {
  it('are short', () => {
    expect(axisUsd(0)).toBe('$0');
    expect(axisUsd(2.5)).toBe('$2.50');
    expect(axisUsd(12.4)).toBe('$12');
    expect(axisUsd(1500)).toBe('$1.5k');
    expect(axisShare(0.5)).toBe('50%');
  });
});

// ---------- the screen ----------

const now = () => Date.now();

/** The finished batches a screen reads for durations and budgets in force. */
const batchesFor = () => {
  const t = now();
  const runs = finishedRuns(t);
  return {
    runs,
    batches: Object.fromEntries(runs.map((r) => [r.run_id, finishedBatch(r.run_id, t)])),
  };
};

const panel = (title: string) => {
  const section = screen.getByRole('heading', { name: title }).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

const kpi = (label: string) => {
  const name = [...document.querySelectorAll('.ch-kpi__label')].find(
    (el) => el.textContent === label,
  );
  if (!name) throw new Error(`no KPI ${label}`);
  return within(name.closest('.ch-kpi') as HTMLElement);
};

const pressed = (name: string) =>
  screen.getByRole('button', { name }).getAttribute('aria-pressed') === 'true';

describe('Analytics', () => {
  it('shows spend, pass rate, false stops and merges as figures, each with the field it is read from', async () => {
    const t = now();
    const body = generated({ since: '30d', bucket: 'day', group_by: 'repository' }, t);
    serveApi({ analytics: body, ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');

    const total = body.tasks.total;
    expect(screen.getByRole('heading', { level: 1, name: 'Analytics' })).toBeTruthy();
    expect(
      screen.getByText(
        new RegExp(`${body.tasks.tasks} finished tasks in the last 30 days · buckets are UTC days`),
      ),
    ).toBeTruthy();
    const spend = kpi('Spend');
    expect(
      spend.getByText(`≈ $${total.usd_estimate.toFixed(2)}`.replace(/(\d)(?=(\d{3})+\.)/g, '$1,')),
    ).toBeTruthy();
    expect(spend.getByText('tasks.total.usd_estimate')).toBeTruthy();
    expect(spend.getByText(/of \d+ tasks priced/).textContent).toContain(
      `${total.usd_estimate_known} of ${total.tasks} tasks priced`,
    );
    expect(
      kpi('Pass rate').getByText(`${Math.round((total.completed / total.tasks) * 100)}%`),
    ).toBeTruthy();
    expect(kpi('Pass rate').getByText(/95% CI \d+–\d+%/)).toBeTruthy();
    expect(kpi('Governor false stops').getByText(/still completed/)).toBeTruthy();
    expect(kpi('Pull requests merged').getByText('tasks.total.prs')).toBeTruthy();
  });

  it('draws the spend over time stacked by repository, with a legend and a data table whose rows add up', async () => {
    const t = now();
    const body = generated({ since: '30d', bucket: 'day', group_by: 'repository' }, t);
    serveApi({ analytics: body, ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');

    const spend = panel('Spend over time');
    expect(spend.getByRole('img', { name: 'Spend per day, stacked by repository' })).toBeTruthy();
    for (const repo of REPOS) expect(spend.getAllByText(repo).length).toBeGreaterThan(0);
    expect(spend.getByText(/not a bill/)).toBeTruthy();

    fireEvent.click(spend.getByRole('button', { name: 'Show data' }));
    const table = await spend.findByRole('table', { name: 'Spend per day' });
    // The box that scrolls it takes focus, so the keyboard can scroll it.
    expect(
      spend.getByRole('region', { name: 'Spend per day, scrollable' }).getAttribute('tabindex'),
    ).toBe('0');
    const rows = within(table).getAllByRole('row');
    // A header, and a row for every day of the range: the empty ones are zeros, not missing.
    expect(rows.length).toBeGreaterThan(30);
    const head = within(rows[0] as HTMLElement)
      .getAllByRole('columnheader')
      .map((c) => c.textContent);
    expect(head).toEqual(['Bucket (UTC)', ...REPOS, 'Total']);
    // The total column is the sum of the others, to the cent.
    let checked = 0;
    for (const row of rows.slice(1)) {
      const cells = within(row)
        .getAllByRole('cell')
        .map((c) => Number((c.textContent ?? '').replace(/[^0-9.]/g, '')));
      if (cells.length !== 5) continue;
      expect(cells[4]).toBeCloseTo((cells[1] ?? 0) + (cells[2] ?? 0) + (cells[3] ?? 0), 1);
      checked += 1;
    }
    expect(checked).toBeGreaterThan(20);
    fireEvent.click(spend.getByRole('button', { name: 'Hide data' }));
    expect(spend.queryByRole('table')).toBeNull();
  });

  it('asks the server for the range, the bucket and the grouping that were picked, and says which is pressed', async () => {
    const api = serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    expect(api.last('/v1/analytics')?.path).toBe(
      '/v1/analytics?since=30d&bucket=day&group_by=repository',
    );
    expect(pressed('30 d')).toBe(true);
    expect(pressed('Repository')).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: '90 d' }));
    await waitFor(() =>
      expect(api.last('/v1/analytics')?.path).toBe(
        '/v1/analytics?since=90d&bucket=week&group_by=repository',
      ),
    );
    expect(pressed('Week')).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Month' }));
    await waitFor(() =>
      expect(api.last('/v1/analytics')?.path).toBe(
        '/v1/analytics?since=90d&bucket=month&group_by=repository',
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Size' }));
    await waitFor(() =>
      expect(api.last('/v1/analytics')?.path).toBe(
        '/v1/analytics?since=90d&bucket=month&group_by=size',
      ),
    );
    await waitFor(() => expect(screen.getAllByText('Size S').length).toBeGreaterThan(0));

    // A range picks its own bucket again.
    fireEvent.click(screen.getByRole('button', { name: '7 d' }));
    await waitFor(() =>
      expect(api.last('/v1/analytics')?.path).toBe(
        '/v1/analytics?since=7d&bucket=day&group_by=size',
      ),
    );
    // The same range is read from /v1/learn for the groups.
    await waitFor(() => expect(api.last('/v1/learn')?.path).toBe('/v1/learn?since=7d'));
  });

  it('offers grouping by account to the owner only', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const groupBy = within(screen.getByRole('group', { name: 'Group by' }));
    expect(groupBy.getByRole('button', { name: 'Account' })).toBeTruthy();
  });

  it('says it is your own tasks, and has no account grouping, for an account key', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false }), ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const groupBy = within(screen.getByRole('group', { name: 'Group by' }));
    expect(groupBy.queryByRole('button', { name: 'Account' })).toBeNull();
    expect(screen.getByText(/your own tasks/)).toBeTruthy();
  });

  it('gives a mark of each SVG chart a name that holds its numbers and where they come from, and shows it on focus', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');

    const pass = await screen.findByRole('list', {
      name: 'Pass rate by repository and size, with 95% intervals',
    });
    const marks = within(pass).getAllByRole('img');
    expect(marks.length).toBeGreaterThan(5);
    const first = marks[0] as HTMLElement;
    expect(first.getAttribute('aria-label')).toMatch(
      /^acme\/ledger · size S: pass rate \d+%, completed \d+ of \d+ tasks, 95% Wilson interval \d+–\d+%\. Source: GET \/v1\/learn · groups\[acme\/ledger, S\]/,
    );
    expect(first.getAttribute('tabindex')).toBe('0');
    fireEvent.focus(first);
    const tip = await screen.findAllByText(/95% Wilson interval/);
    expect(tip.length).toBeGreaterThan(0);
  });

  it('compares each size class with the budget it was last held to, and says where that budget came from', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    const budgets = await screen.findByRole('heading', { name: 'Budget against actual spend' });
    const section = within(budgets.closest('section') as HTMLElement);
    expect(
      await section.findByText(/budget ≈ \$1\.50 · flat budget of the size class/),
    ).toBeTruthy();
    expect(
      section.getByText(/budget ≈ \$1\.12 · p80 of 23 past tasks, this repository/),
    ).toBeTruthy();
    expect(
      section.getByText(/budget ≈ \$4\.90 · p80 of 31 past tasks, all repositories/),
    ).toBeTruthy();
    expect(section.getByText('100% = budget')).toBeTruthy();
  });

  it('bins the batch durations and says the median and the 80th percentile, and what the route cannot say', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    const durationsPanel = await screen.findByRole('heading', {
      name: 'Duration of finished batches',
    });
    const section = within(durationsPanel.closest('section') as HTMLElement);
    expect(await section.findByText(/batches · median/)).toBeTruthy();
    expect(section.getByText(/a split by size class, are not in the analytics route/)).toBeTruthy();
    fireEvent.click(section.getByRole('button', { name: 'Show data' }));
    expect(
      await section.findByRole('table', { name: 'Duration of finished batches' }),
    ).toBeTruthy();
  });

  it('shows the governor’s false-stop rate with its counts and the 5% line, and the continuations apart from the CI fixer', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const governor = panel('Governor false-stop rate');
    expect(governor.getByText('observe')).toBeTruthy();
    expect(governor.getByText(/checkpointed tasks completed · CI \d+–\d+%/)).toBeTruthy();
    expect(governor.getByText('5% to enforce')).toBeTruthy();
    expect(governor.getByText('Decisions')).toBeTruthy();

    const next = panel('Continuations and CI fixer');
    expect(next.getByText(/Completed after a continuation|With a continuation/)).toBeTruthy();
    expect(next.getByText('Not recorded yet')).toBeTruthy();
    expect(next.getByText(/The CI fixer's attempts and cost are not recorded apart/)).toBeTruthy();
  });

  it('lists what the server does not record, and says there is no benchmark comparison and why', async () => {
    const body = generated({ since: '30d' });
    serveApi({ analytics: body, ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const notes = panel('What is not recorded');
    for (const line of body.not_recorded) expect(notes.getByText(line)).toBeTruthy();
    expect(notes.getByText(/No route serves benchmark results/)).toBeTruthy();
  });

  it('shows pull requests merged by how they were merged, in the order of the hue table', async () => {
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const prs = panel('Pull requests merged');
    expect(prs.getByText('Merged as chargehand left it')).toBeTruthy();
    expect(prs.getByText('Merged after a person edited it')).toBeTruthy();
    expect(prs.getByRole('img', { name: /Pull requests merged per day, by outcome/ })).toBeTruthy();
  });

  it('says there is nothing, and what to do, when no task finished in the range', async () => {
    const body = generated({ since: '30d' });
    const empty: AnalyticsBody = {
      ...body,
      tasks: { ...body.tasks, tasks: 0, points: [], total: point() },
      runs: [],
    };
    serveApi({ analytics: empty, ...batchesFor() });
    renderConsole('/console/analytics');
    expect(await screen.findByText('No finished tasks in this range')).toBeTruthy();
    expect(screen.getByText(/Run a batch, or widen the range to all time/)).toBeTruthy();
    expect(screen.queryByText('Spend over time')).toBeNull();
    // The controls stay, so the range can be widened.
    expect(screen.getByRole('button', { name: 'All' })).toBeTruthy();
  });

  it('says nothing could be priced, not zero dollars, when tasks have tokens and no price', async () => {
    const body = generated({ since: '30d' });
    const points = body.tasks.points.map((p) => ({ ...p, usd_estimate: 0, usd_estimate_known: 0 }));
    const unpriced: AnalyticsBody = {
      ...body,
      tasks: {
        ...body.tasks,
        points,
        total: { ...body.tasks.total, usd_estimate: 0, usd_estimate_known: 0 },
      },
    };
    serveApi({ analytics: unpriced, ...batchesFor() });
    renderConsole('/console/analytics');
    expect(await screen.findByText('No task could be priced')).toBeTruthy();
    expect(kpi('Spend').getByText('—')).toBeTruthy();
  });

  it('shows a skeleton while the records load, and the call and the answer when the server fails, with a retry', async () => {
    serveApi({ hang: ['/v1/analytics'] });
    const first = renderConsole('/console/analytics');
    expect(await screen.findByText('Loading the learning records')).toBeTruthy();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(0);
    first.unmount();

    const api = serveApi({
      replies: { '/v1/analytics': [503, { errors: ['timed out after 10 s'] }] },
      ...batchesFor(),
    });
    renderConsole('/console/analytics');
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Cannot read the learning records');
    expect(alert.textContent).toContain('GET /v1/analytics');
    expect(alert.textContent).toContain('timed out after 10 s');
    const before = api.calls.filter((c) => c.path.startsWith('/v1/analytics')).length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(api.calls.filter((c) => c.path.startsWith('/v1/analytics')).length).toBeGreaterThan(
        before,
      ),
    );
  });

  it('keeps the other panels when the learning report fails: the one that needs it says so', async () => {
    serveApi({
      replies: { '/v1/learn': [500, { errors: ['no store'] }] },
      ...batchesFor(),
    });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    const errors = await screen.findAllByRole('alert');
    expect(errors.some((e) => e.textContent?.includes('GET /v1/learn'))).toBe(true);
    // The time charts do not wait for it.
    expect(panel('Spend over time').getByRole('img', { name: /Spend per day/ })).toBeTruthy();
  });

  it('works on a phone: touch-size controls and the same panels', async () => {
    viewport(true);
    serveApi({ ...batchesFor() });
    renderConsole('/console/analytics');
    await screen.findByText('Spend');
    expect(document.querySelectorAll('.ch-seg--touch')).toHaveLength(3);
    expect(panel('Spend over time')).toBeTruthy();
  });
});
