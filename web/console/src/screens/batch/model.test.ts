import { describe, expect, it } from 'vitest';
import type { BatchBody, BatchTaskBody } from '../../api/batch';
import type { RunStatus } from '../../api/types';
import { BATCH_A } from '../../testing/fixtures';
import { batchEvents, finishedBatch, richBatch } from '../../testing/fixtures-runs';
import { batchArtifacts, changeTotals, testIntegrityPaths, verificationFacts } from './artifacts';
import {
  batchElapsed,
  batchPulls,
  batchTotals,
  cacheShare,
  cutGoals,
  edgeState,
  eventDetail,
  eventKey,
  eventTone,
  extent,
  filterEvents,
  governorRows,
  governorSummary,
  graphEdges,
  graphShape,
  layerLayout,
  mergeEvents,
  nodeProgress,
  notesAbout,
  packLanes,
  prState,
  rebuildRequest,
  spendRows,
  spendShares,
  spendTotal,
  taskElapsed,
  taskMeta,
  taskShare,
  taskTokens,
  taskUsd,
  taskWord,
  timelineModel,
} from './model';
import { isTab, TABS } from './tabs';

/** A value the fixture has: a test that spreads one would fail loudly if it did not. */
const must = <T>(value: T | null | undefined): T => {
  if (value == null) throw new Error('the fixture lacks this value');
  return value;
};

const NOW = Date.parse('2026-10-13T13:00:00Z');
const batch = (): BatchBody => richBatch(BATCH_A, NOW);
const byId = (b: BatchBody, id: string) => b.tasks.find((t) => t.id === id) as BatchTaskBody;

describe('the tabs', () => {
  it('names the eight sections, and tells a section from anything else in the address', () => {
    expect(TABS.map((t) => t.id)).toEqual([
      'tasks',
      'timeline',
      'events',
      'governor',
      'spend',
      'prs',
      'artifacts',
      'request',
    ]);
    expect(isTab('spend')).toBe(true);
    expect(isTab('SPEND')).toBe(false);
    expect(isTab(undefined)).toBe(false);
    expect(isTab(3)).toBe(false);
  });
});

describe('a task as the design words it', () => {
  const b = batch();
  const word = (id: string) => taskWord(byId(b, id), b.tasks);

  it('reads the state, the error code and the pull request together', () => {
    expect(word('t1-schema')).toBe('merged');
    expect(word('t2-retry-policy')).toBe('done');
    expect(word('t3-memory-ttl')).toBe('question');
    expect(word('t4-metrics')).toBe('running');
    expect(word('t7-guards')).toBe('rate_limited');
    expect(word('t8-announce')).toBe('not_started');
  });

  it('tells a task that waits for a dependency from one that is only queued', () => {
    expect(word('t5-docs')).toBe('waiting');
    const open: BatchTaskBody = { ...byId(b, 't5-docs'), depends_on: ['t1-schema'] };
    expect(taskWord(open, b.tasks)).toBe('queued');
    expect(taskWord({ ...open, depends_on: [] }, b.tasks)).toBe('queued');
    expect(taskWord({ ...open, depends_on: ['gone'] }, b.tasks)).toBe('waiting');
  });

  it('knows a cancelled task and a kept draft', () => {
    const t = byId(b, 't7-guards');
    expect(
      taskWord({ ...t, state: 'cancelled', error: { ...must(t.error), code: 'cancelled' } }, [t]),
    ).toBe('cancelled');
    const done = byId(b, 't2-retry-policy');
    expect(
      taskWord({ ...done, pull_request: { ...must(done.pull_request), partial: true } }, [done]),
    ).toBe('partial');
  });

  it('says in a line where a task is, or why it is not', () => {
    const meta = (id: string) => taskMeta(byId(b, id), b.tasks);
    expect(meta('t1-schema')).toBe('12 turns · 1 session');
    expect(meta('t2-retry-policy')).toBe('18 turns · 2 sessions · 1 continuation');
    const none = { ...byId(b, 't2-retry-policy') };
    none.spend = { ...must(none.spend), continuations: 0 };
    expect(taskMeta(none, b.tasks)).toBe('18 turns · 2 sessions');
    const many = { ...none, spend: { ...must(none.spend), continuations: 3 } };
    expect(taskMeta(many, b.tasks)).toBe('18 turns · 2 sessions · 3 continuations');
    expect(meta('t3-memory-ttl')).toBe('asked 2 questions');
    expect(meta('t4-metrics')).toBe('stage test · 22 turns');
    expect(meta('t5-docs')).toBe('waits for t4-metrics');
    expect(meta('t7-guards')).toBe('usage limit');
    expect(meta('t8-announce')).toBe('a dependency did not finish');
    expect(taskMeta({ ...byId(b, 't4-metrics'), live: null }, b.tasks)).toBe('starting');
  });

  it('counts tokens and dollars from the record, or from the live counters of a running task', () => {
    const t4 = byId(b, 't4-metrics');
    expect(taskTokens(t4)).toBe(232_000);
    expect(taskTokens({ ...t4, spend: undefined })).toBe(412_000);
    expect(taskTokens(byId(b, 't5-docs'))).toBeNull();
    expect(taskUsd(t4)).toBe(0.96);
    expect(taskUsd({ ...t4, spend: undefined })).toBe(0.96);
    expect(taskUsd(byId(b, 't5-docs'))).toBeNull();
  });

  it('draws a node bar from spend against budget, or done and not done', () => {
    expect(taskShare(byId(b, 't4-metrics'))).toBeCloseTo((0.96 / 2.05) * 100);
    expect(nodeProgress(byId(b, 't4-metrics'))).toBe(47);
    expect(
      nodeProgress({
        ...byId(b, 't7-guards'),
        spend: { ...must(byId(b, 't7-guards').spend), usd_estimate: 9 },
      }),
    ).toBe(100);
    expect(nodeProgress({ ...byId(b, 't1-schema'), budget: undefined })).toBe(100);
    expect(nodeProgress(byId(b, 't5-docs'))).toBe(0);
    expect(taskShare(byId(b, 't5-docs'))).toBeNull();
  });

  it('times a task to its end, to now while it runs, and not at all before it starts', () => {
    expect(taskElapsed(byId(b, 't1-schema'), NOW)).toBe(8 * 60);
    expect(taskElapsed(byId(b, 't4-metrics'), NOW)).toBe(33 * 60);
    expect(taskElapsed(byId(b, 't5-docs'), NOW)).toBeNull();
  });
});

describe('the task graph', () => {
  const b = batch();

  it('keeps the edges whose two ends are in the batch, with the state of each', () => {
    const edges = graphEdges(b.tasks);
    expect(edges.map((e) => e.id).sort()).toEqual([
      't1-schema->t2-retry-policy',
      't1-schema->t4-metrics',
      't1-schema->t7-guards',
      't3-memory-ttl->t6-cleanup',
      't4-metrics->t5-docs',
      't7-guards->t8-announce',
    ]);
    const state = (id: string) => edges.find((e) => e.id === id)?.state;
    expect(state('t1-schema->t4-metrics')).toBe('active');
    expect(state('t1-schema->t2-retry-policy')).toBe('done');
    expect(state('t4-metrics->t5-docs')).toBe('waiting');
    expect(state('t7-guards->t8-announce')).toBe('blocked');
    expect(state('t3-memory-ttl->t6-cleanup')).toBe('blocked');
    const loose = { ...byId(b, 't5-docs'), depends_on: ['nowhere'] };
    expect(graphEdges([loose])).toEqual([]);
    expect(edgeState(undefined, loose)).toBe('waiting');
  });

  it('keeps one shape while only the states change, and a new one when the links do', () => {
    const shape = graphShape(b.tasks);
    expect(graphShape(finishedBatch(BATCH_A, NOW).tasks)).toBe(shape);
    const moved = b.tasks.map((t) => (t.id === 't5-docs' ? { ...t, depends_on: [] } : t));
    expect(graphShape(moved)).not.toBe(shape);
  });

  it('lays the tasks in columns by the longest chain, and the unlinked ones in a grid under them', () => {
    const p = layerLayout(b.tasks);
    expect(p['t1-schema']?.x).toBe(0);
    expect(p['t2-retry-policy']?.x).toBeGreaterThan(0);
    expect(p['t5-docs']?.x).toBeGreaterThan(p['t4-metrics']?.x ?? 0);
    expect(p['t8-announce']?.x).toBeGreaterThan(p['t7-guards']?.x ?? 0);
    expect(Object.keys(p)).toHaveLength(b.tasks.length);
    const loose = layerLayout(
      ['a', 'b', 'c', 'd'].map((id) => ({ ...byId(b, 't5-docs'), id, depends_on: [] })),
      3,
    );
    expect(loose.a?.y).toBe(loose.b?.y);
    expect(loose.d?.y).toBeGreaterThan(loose.a?.y ?? 0);
    expect(extent(loose).width).toBeGreaterThan(0);
    expect(extent({})).toEqual({ width: 0, height: 0 });
  });

  it('survives a cycle: nothing loops', () => {
    const a = { ...byId(b, 't5-docs'), id: 'a', depends_on: ['b'] };
    const c = { ...byId(b, 't5-docs'), id: 'b', depends_on: ['a'] };
    expect(Object.keys(layerLayout([a, c]))).toEqual(['a', 'b']);
  });
});

describe('the totals of a batch', () => {
  it('adds what the tasks have used, against the caps of the batch', () => {
    const totals = batchTotals(batch());
    expect(totals.tokens.cap).toBe(4_000_000);
    expect(totals.usd.cap).toBe(15);
    expect(totals.usd.estimated).toBe(true);
    expect(totals.usd.used).toBeCloseTo(0.42 + 1.12 + 0.9 + 0.96 + 2.94);
    expect(totals.tokens.used).toBe(111_000 + 284_000 + 160_000 + 232_000 + 820_000);
  });

  it('reads the result of a finished batch when it says more', () => {
    const totals = batchTotals(finishedBatch(BATCH_A, NOW));
    expect(totals.tokens.used).toBe(2_100_000);
  });

  it('falls back to the run’s own token count before any task has spent', () => {
    const empty = { ...batch(), tasks: [] };
    expect(batchTotals(empty).tokens.used).toBe(2_900_000);
    expect(batchTotals(empty).usd.used).toBeNull();
  });

  it('times a batch to its end, or to now', () => {
    expect(batchElapsed(batch(), NOW)).toBe(42 * 60);
    expect(batchElapsed(finishedBatch(BATCH_A, NOW), NOW)).toBe(39 * 60);
  });
});

describe('the timeline', () => {
  it('draws a bar per session in the first free slot, with a continuation marked', () => {
    const model = timelineModel(batch(), NOW);
    expect(model).not.toBeNull();
    const bars = model?.lanes.flatMap((l) => l.bars) ?? [];
    expect(bars.map((b) => b.task).sort()).toEqual([
      't1-schema',
      't2-retry-policy',
      't2-retry-policy',
      't3-memory-ttl',
      't4-metrics',
      't7-guards',
    ]);
    const second = bars.find((b) => b.id === 't2-retry-policy#2');
    expect(second?.continuation).toBe(true);
    expect(bars.find((b) => b.id === 't2-retry-policy#1')?.status).toBe('partial');
    expect(model?.fromEvents).toBe(true);
    expect(model?.lanes.length).toBe(4);
    expect(model?.busy).toBe(1);
    expect(model?.slots).toBe(4);
    expect(model?.nowLeft).not.toBeNull();
    expect(model?.pauses).toHaveLength(1);
    expect(model?.ticks.length).toBeGreaterThan(2);
  });

  it('puts no two bars of one slot on top of each other', () => {
    const model = timelineModel(batch(), NOW);
    for (const lane of model?.lanes ?? [])
      lane.bars.forEach((bar, i) => {
        const next = lane.bars[i + 1];
        if (next) expect(next.start).toBeGreaterThanOrEqual(bar.end);
      });
  });

  it('shows no marker for now once the batch has ended, and one bar per task without events', () => {
    const done = finishedBatch(BATCH_A, NOW);
    const model = timelineModel(done, NOW);
    expect(model?.nowLeft).toBeNull();
    expect(model?.fromEvents).toBe(false);
    expect(
      model?.lanes.flatMap((l) => l.bars).filter((b) => b.task === 't2-retry-policy'),
    ).toHaveLength(1);
  });

  it('has no model before a task has started', () => {
    const idle = {
      ...batch(),
      tasks: batch().tasks.filter((t) => !t.started_at),
      rate_limits: [],
    };
    expect(timelineModel(idle, NOW)).toBeNull();
  });

  it('packs bars into as many slots as it needs, and at least the minimum', () => {
    const bar = (id: string, start: number, end: number) => ({
      id,
      task: id,
      session: 1,
      start,
      end,
      status: 'running' as const,
      continuation: false,
      title: id,
    });
    const lanes = packLanes([bar('a', 0, 10), bar('b', 5, 15), bar('c', 10, 20)], 4);
    expect(lanes.map((l) => l.bars.map((b) => b.id))).toEqual([['a', 'c'], ['b'], [], []]);
    expect(lanes.map((l) => l.label)).toEqual(['Slot A', 'Slot B', 'Slot C', 'Slot D']);
  });
});

describe('the events', () => {
  const events = batchEvents(NOW);

  it('merges what the batch recorded with what the stream brought, once each, oldest first', () => {
    const streamed: RunStatus[] = [events[3] as RunStatus, events[0] as RunStatus];
    const merged = mergeEvents(events.slice(0, 5), streamed);
    expect(merged).toHaveLength(5);
    const times = merged.map((e) => Date.parse(e.at ?? ''));
    expect(times).toEqual([...times].sort((a, b) => a - b));
    expect(new Set(merged.map(eventKey)).size).toBe(5);
  });

  it('filters by kind and by task', () => {
    expect(filterEvents(events, 'all', 'all')).toHaveLength(events.length);
    expect(filterEvents(events, 'governor', 'all').every((e) => e.event === 'governor')).toBe(true);
    expect(filterEvents(events, 'governor', 'all')).toHaveLength(2);
    expect(filterEvents(events, 'needs', 'all').map((e) => e.event)).toContain('question');
    expect(filterEvents(events, 'prs', 't2-retry-policy').map((e) => e.event)).toEqual([
      'pushed',
      'pr_opened',
    ]);
    expect(filterEvents(events, 'progress', 'nobody')).toEqual([]);
  });

  it('words an event from its detail, or from the numbers it carries', () => {
    expect(eventDetail(events[1] as RunStatus)).toBe('session container is up');
    expect(
      eventDetail({
        contract_version: 'run-status/v1',
        run_id: 'r',
        status: 'running',
        stage: 'test',
        turns: 3,
        tokens: 12_345,
        usd_estimate: 0.5,
        branch: 'b',
      }),
    ).toBe('stage test · 3 turns · 12,345 tokens · ≈ $0.50 · branch b');
  });

  it('marks a question, a checkpoint and a failure', () => {
    const q = events.find((e) => e.event === 'question') as RunStatus;
    const cp = events.find((e) => e.detail?.includes('checkpoint')) as RunStatus;
    const fail = events.find((e) => e.status === 'failed') as RunStatus;
    expect(eventTone(q)).toBe('warn');
    expect(eventTone(cp)).toBe('warn');
    expect(eventTone(fail)).toBe('danger');
    expect(eventTone(events[1] as RunStatus)).toBeUndefined();
  });
});

describe('the governor', () => {
  const b = batch();

  it('sums the decisions by rung, the tasks with a checkpoint, and where budgets came from', () => {
    const s = governorSummary(b.tasks);
    expect(s).toMatchObject({
      decisions: 4,
      warn: 2,
      checkpoint: 2,
      stop: 0,
      acted: 0,
      modes: ['observe'],
      checkpointedTasks: 2,
      checkpointedCompleted: 1,
    });
    expect(Object.fromEntries(s.sources)).toEqual({ history: 4, task: 1, flat: 2 });
  });

  it('lists a row per signal and rung, with the stop reason of its task', () => {
    const rows = governorRows(b.tasks);
    expect(rows).toHaveLength(3);
    expect(rows.find((r) => r.task === 't7-guards')).toMatchObject({
      signal: 'checkpoint_context',
      action: 'checkpoint',
      stopReason: 'checkpoint_context',
    });
    expect(rows.find((r) => r.signal === 'repeated_call')?.stopReason).toBeNull();
  });
});

describe('the spend', () => {
  const rows = spendRows(batch().tasks);

  it('has a row for each task that has spent, and a total', () => {
    expect(rows.map((r) => r.id)).toEqual([
      't1-schema',
      't2-retry-policy',
      't3-memory-ttl',
      't4-metrics',
      't7-guards',
    ]);
    const total = spendTotal(rows);
    expect(total.id).toBe('Total');
    expect(total.usd).toBeCloseTo(6.34);
    expect(total.cacheRead).toBe(400_000 + 1_900_000 + 0 + 1_100_000 + 5_400_000);
    expect(total.estimate).toBe(true);
    expect(spendTotal([]).usd).toBeNull();
  });

  it('says what share of the input the cache served', () => {
    const share = cacheShare(rows);
    expect(share).toBeGreaterThan(80);
    expect(share).toBeLessThan(100);
    expect(cacheShare([])).toBeNull();
  });

  it('draws the four costliest and the rest as one share', () => {
    const shares = spendShares(rows);
    expect(shares.map((s) => s.id)).toEqual([
      't7-guards',
      't2-retry-policy',
      't4-metrics',
      't3-memory-ttl',
      'other tasks',
    ]);
    expect(shares.at(-1)?.usd).toBeCloseTo(0.42);
    expect(spendShares(rows, 10).some((s) => s.id === 'other tasks')).toBe(false);
  });
});

describe('the pull requests', () => {
  const b = batch();

  it('lays what the forge said over the batch’s own list, newest first, and ignores another batch', () => {
    const merged = b.pull_requests.find((p) => p.number === 411);
    const rows = batchPulls(b, [
      { ...must(merged), state: 'merged_edited', integrator_last: null },
      { ...must(merged), url: 'https://example.com/other/pull/1', batch_run_id: 'other' },
    ]);
    expect(rows.map((r) => r.number)).toEqual([412, 411]);
    expect(rows[1]?.state).toBe('merged_edited');
    expect(rows[1]?.integrator_last).toBe('merged: required checks passed');
  });

  it('names the outcome of a pull request', () => {
    const open = must(b.pull_requests.find((p) => p.number === 412));
    expect(prState({ ...open, state: 'merged_unchanged' })).toEqual({
      status: 'merged',
      label: 'Merged',
    });
    expect(prState({ ...open, state: 'merged_edited' }).label).toBe('Merged, edited');
    expect(prState({ ...open, state: 'closed' }).label).toBe('Closed');
    expect(prState({ ...open, partial: true }).label).toBe('Partial draft');
    expect(prState(open).label).toBe('Waits for you');
    expect(prState({ ...open, awaiting_person: false }).label).toBe('Open');
    expect(prState({ ...open, state: 'unknown' }).label).toBe('Outcome unknown');
  });
});

describe('the request', () => {
  const b = batch();

  it('is rebuilt from what the batch view holds: caller sizes, task budgets and links, not the rest', () => {
    const request = rebuildRequest(b);
    expect(request).toMatchObject({
      contract_version: 'request/v1',
      text: `Clone of ${BATCH_A}`,
      context: { interactive: false, preset: 'driven', repository: { path: b.repository?.path } },
      driven: { plan: true, max_parallel: 4, max_tokens_total: 4_000_000, max_usd_total: 15 },
    });
    const tasks = request.driven.tasks;
    expect(tasks).toHaveLength(8);
    // t1 was sized by the plan, t2 by default; neither is the caller's, so neither carries a size.
    expect(tasks.find((t) => t.id === 't1-schema')).not.toHaveProperty('size');
    expect(tasks.find((t) => t.id === 't3-memory-ttl')).toMatchObject({ budget_usd: 2.05 });
    expect(tasks.find((t) => t.id === 't2-retry-policy')).not.toHaveProperty('budget_usd');
    expect(tasks.find((t) => t.id === 't5-docs')).toMatchObject({ depends_on: ['t4-metrics'] });
  });

  it('carries a continuation and the whole goal of one the view cut', () => {
    const cut: BatchBody = {
      ...b,
      tasks: [
        {
          ...byId(b, 't5-docs'),
          goal: 'cut…',
          goal_truncated: true,
          continues: { run_id: 'earlier', task_id: 't5-docs' },
        } as BatchTaskBody,
      ],
    };
    expect(cutGoals(cut)).toEqual(['t5-docs']);
    const { tasks } = rebuildRequest(cut, { 't5-docs': 'the whole goal' }).driven;
    expect(tasks[0]).toMatchObject({
      goal: 'the whole goal',
      continue_from: { run_id: 'earlier', task_id: 't5-docs' },
    });
    expect(cutGoals(b)).toEqual([]);
  });

  it('leaves out limits and a repository the batch did not have', () => {
    const bare = { ...b, repository: undefined, limits: {} } as unknown as BatchBody;
    const request = rebuildRequest(bare);
    expect(request.context).not.toHaveProperty('repository');
    expect(request.driven).not.toHaveProperty('plan');
    expect(request.driven).not.toHaveProperty('max_parallel');
  });
});

describe('the notes of what is not recorded', () => {
  it('picks the sentences that mention a word, whatever its case', () => {
    const notes = batch().not_recorded;
    expect(notesAbout(notes, 'GOVERNOR')).toHaveLength(1);
    expect(notesAbout(notes, 'transcript')).toHaveLength(1);
    // The governor sentence names the live events too.
    expect(notesAbout(notes, 'events', 'sessions')).toHaveLength(3);
    expect(notesAbout(notes, 'nothing here')).toEqual([]);
  });
});

describe('the artifacts', () => {
  it('lists the result, the plan and the references of a batch, with the plan and retro as content', () => {
    const items = batchArtifacts(finishedBatch(BATCH_A, NOW));
    expect(items.map((i) => i.id)).toEqual(['result', 'plan', 'driven-batch']);
    expect(items[0]?.view).toBe('json');
    expect(items[2]).toMatchObject({ view: 'ref', name: 'Tasks of the batch' });
    expect(batchArtifacts(batch()).map((i) => i.id)).toEqual(['driven-batch']);
  });

  it('adds up a changes artifact and reads a verification run', () => {
    expect(
      changeTotals({
        files_total: 5,
        files: [
          { added: 3, removed: 1 },
          { added: 2, removed: 0 },
        ],
      }),
    ).toEqual({ files: 5, added: 5, removed: 1 });
    expect(changeTotals({ files: [{ added: 1, removed: 2 }] })).toEqual({
      files: 1,
      added: 1,
      removed: 2,
    });
    expect(changeTotals(null)).toBeNull();
    expect(changeTotals({ nope: 1 })).toBeNull();
    const facts = verificationFacts({
      argv: ['dotnet', 'test'],
      passed: false,
      exit_code: 1,
      timed_out: false,
      duration_seconds: 4,
      test_integrity: { flagged: true, reason: 'only tests changed', test_paths: ['t.cs'] },
    });
    expect(facts).toMatchObject({
      command: 'dotnet test',
      passed: false,
      exitCode: 1,
      flagged: true,
      reason: 'only tests changed',
    });
    expect(verificationFacts('x')).toBeNull();
  });

  it('names the test and build files the integrity check found', () => {
    expect(
      testIntegrityPaths({
        test_integrity: { test_paths: ['a.cs'], build_paths: ['b.csproj', 3] },
      }),
    ).toEqual(['a.cs', 'b.csproj']);
    expect(testIntegrityPaths(null)).toEqual([]);
  });
});
