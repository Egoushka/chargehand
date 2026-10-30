// What the Learning screen computes from /v1/learn and the newest finished batches: the table of tasks by repository and size
// class, the budget rule's settings, retros as the server wrote them, and the patterns that can be read off the numbers. Pure.

import type { BatchBody } from '../../api/batch';
import type { SystemBody } from '../../api/system';
import type { LearnGroup } from '../../api/types';
import { formatInterval, wilson } from '../../charts/stats';
import { baseName, DASH, formatCount, formatPercent, formatUsd } from '../../lib/format';

// ---------- range ----------

export type LearnRangeId = '7d' | '30d' | '60d' | 'all';

export const LEARN_RANGES: ReadonlyArray<{
  id: LearnRangeId;
  label: string;
  since: string;
  phrase: string;
}> = [
  { id: '7d', label: '7 d', since: '7d', phrase: 'the last 7 days' },
  { id: '30d', label: '30 d', since: '30d', phrase: 'the last 30 days' },
  { id: '60d', label: '60 d', since: '60d', phrase: 'the last 60 days' },
  // The server takes days or a time, not "all": a hundred years reaches the first record.
  { id: 'all', label: 'All', since: '36500d', phrase: 'all time' },
];

export const learnRangeOf = (id: LearnRangeId) =>
  LEARN_RANGES.find((r) => r.id === id) ?? (LEARN_RANGES[2] as (typeof LEARN_RANGES)[number]);

// ---------- the budget rule ----------

/** The settings of `driven.governor.history` (ADR 0045): a size class gets a budget from history after `minTasks` finished tasks in `windowDays`. */
export interface HistoryRule {
  minTasks: number;
  windowDays: number;
  percentile: number;
  /** `profile` when the owner's system view said so; `default` when it is the documented default (an account cannot read the profile). */
  from: 'profile' | 'default';
}

export const DEFAULT_RULE: HistoryRule = {
  minTasks: 5,
  windowDays: 60,
  percentile: 80,
  from: 'default',
};

export function historyRule(system: SystemBody | null | undefined): HistoryRule {
  const h = system?.profile?.driven?.governor?.history;
  return h
    ? {
        minTasks: h.min_tasks,
        windowDays: h.window_days,
        percentile: h.percentile,
        from: 'profile',
      }
    : DEFAULT_RULE;
}

// ---------- the table ----------

export interface LearnRow {
  key: string;
  repository: string;
  size: string;
  tasks: number;
  median: string;
  p80: string;
  completed: string;
  partial: string;
  checkpoint: string;
  thenDone: string;
  merged: string;
  /** Whether there are enough priced tasks for a budget from history, in words. */
  history: string;
  enough: boolean;
  group: LearnGroup;
}

const share = (rate: number | null | undefined) =>
  rate == null ? DASH : formatPercent(rate * 100);

export function learnRows(groups: readonly LearnGroup[], rule: HistoryRule): LearnRow[] {
  return groups.map((g) => {
    const enough = g.spend_known >= rule.minTasks;
    const resolved = g.prs.merged_unchanged + g.prs.merged_edited + g.prs.merged + g.prs.closed;
    return {
      key: `${g.repository}\u0000${g.size_class}`,
      repository: g.repository,
      size: g.size_class,
      tasks: g.tasks,
      median: formatUsd(g.spend_median_usd, true),
      p80: formatUsd(g.spend_p80_usd, true),
      completed: `${share(g.completion_rate)} (${formatCount(g.completed)})`,
      partial: share(g.partial_rate),
      checkpoint: share(g.checkpoint_rate),
      thenDone:
        g.checkpointed > 0
          ? `${formatCount(g.checkpointed_completed)} of ${formatCount(g.checkpointed)}`
          : DASH,
      merged:
        g.merged_unchanged_rate == null
          ? DASH
          : `${share(g.merged_unchanged_rate)} (${formatCount(resolved)})`,
      history: enough
        ? `p${rule.percentile} of ${formatCount(g.spend_known)} priced tasks`
        : `${formatCount(g.spend_known)} of ${rule.minTasks} priced tasks: flat budget`,
      enough,
      group: g,
    };
  });
}

/** `GET /v1/learn · groups[repo, S] · field`: where a cell comes from. */
export const learnCellSource = (g: LearnGroup, field: string) =>
  `GET /v1/learn · groups[${g.repository}, ${g.size_class}] · ${field}`;

// ---------- retros ----------

export interface RetroEntry {
  runId: string;
  repository: string | null;
  at: string | null;
  tasks: number;
  /** At most 12 lines of plain text, as the model wrote them. */
  lines: string[];
}

export interface RetroSkip {
  runId: string;
  reason: string;
}

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

/** The `retro` artifact of a batch (`status` written or skipped); anything else is not a retro. Text is untrusted: it is only ever shown as text. */
export function parseRetro(
  value: unknown,
): { status: 'written' | 'skipped'; reason: string | null; tasks: number; text: string } | null {
  if (!isRecord(value)) return null;
  const status = value.status;
  if (status !== 'written' && status !== 'skipped') return null;
  return {
    status,
    reason: typeof value.reason === 'string' ? value.reason : null,
    tasks: typeof value.tasks === 'number' ? value.tasks : 0,
    text: typeof value.text === 'string' ? value.text : '',
  };
}

export const MAX_RETRO_LINES = 12;

/** Written retros (newest batch first, as given) and the reasons the others were skipped. */
export function retros(batches: readonly BatchBody[]): {
  written: RetroEntry[];
  skipped: RetroSkip[];
} {
  const written: RetroEntry[] = [];
  const skipped: RetroSkip[] = [];
  for (const batch of batches) {
    const retro = parseRetro(batch.retro);
    if (!retro) continue;
    if (retro.status === 'skipped') {
      skipped.push({ runId: batch.run.run_id, reason: retro.reason ?? 'no reason given' });
      continue;
    }
    const lines = retro.text
      .split('\n')
      .map((l) => l.trim())
      .filter(Boolean)
      .slice(0, MAX_RETRO_LINES);
    if (lines.length === 0) continue;
    written.push({
      runId: batch.run.run_id,
      repository: batch.repository?.path ? baseName(batch.repository.path) : null,
      at: batch.run.finished_at ?? batch.run.started_at,
      tasks: retro.tasks,
      lines,
    });
  }
  return { written, skipped };
}

// ---------- lessons: patterns read off the numbers ----------

export interface Lesson {
  id: string;
  text: string;
  evidence: string;
  act: string;
  /** Where the numbers come from. */
  source: string;
}

/** A group needs this many tasks (or resolved pull requests) before a pattern is worth saying aloud. */
export const MIN_FOR_PATTERN = 5;

/** How many patterns the panel lists, and how many of one kind: a repeated finding must not crowd out the others. The rest are counted. */
export const MAX_LESSONS = 5;
export const MAX_PER_KIND = 2;

/**
 * The server stores no lessons. These are what a reader would notice in the table, said once with the counts they stand on:
 * a size class that rarely completes, checkpoints that were wrong, pull requests people rewrite, histories too short for a
 * budget. Each is a rule on `GET /v1/learn` and nothing else, so it can be checked against the table above it.
 */
export function lessons(groups: readonly LearnGroup[], rule: HistoryRule): Lesson[] {
  const out: Lesson[] = [];
  for (const g of groups) {
    const where = `${g.repository}, size ${g.size_class}`;
    const src = `GET /v1/learn · groups[${g.repository}, ${g.size_class}]`;
    if (g.tasks >= MIN_FOR_PATTERN && g.completion_rate < 0.5) {
      const p = wilson(g.completed, g.tasks);
      out.push({
        id: `low-completion:${where}`,
        text: `Tasks of size ${g.size_class} in ${g.repository} complete ${share(g.completion_rate)} of the time.`,
        evidence: `${formatCount(g.completed)} of ${formatCount(g.tasks)} tasks completed${p ? ` (95% CI ${formatInterval(p)})` : ''}, ${formatCount(g.partial)} partial.`,
        act: 'Split the goals, or give them a larger size class, before raising any budget.',
        source: `${src} · completed, tasks`,
      });
    }
    if (g.checkpointed >= 3 && g.checkpointed_completed / g.checkpointed >= 0.5) {
      out.push({
        id: `early-checkpoint:${where}`,
        text: `In ${where}, most tasks the governor checkpointed went on to complete.`,
        evidence: `${formatCount(g.checkpointed_completed)} of ${formatCount(g.checkpointed)} checkpointed tasks completed, so a stop would have cut work that finished.`,
        act: 'Keep the governor in observe mode, or raise the budget of this size class.',
        source: `${src} · checkpointed_completed, checkpointed`,
      });
    }
    const resolved = g.prs.merged_unchanged + g.prs.merged_edited + g.prs.merged + g.prs.closed;
    if (
      resolved >= MIN_FOR_PATTERN &&
      g.merged_unchanged_rate != null &&
      g.merged_unchanged_rate < 0.5
    ) {
      out.push({
        id: `edited-merges:${where}`,
        text: `People change most pull requests from ${where} before they merge them.`,
        evidence: `${formatCount(g.prs.merged_unchanged)} of ${formatCount(resolved)} resolved pull requests merged as chargehand left them.`,
        act: 'Read the edits to see what the goals leave out, and put it in the goal or the verify command.',
        source: `${src} · prs, merged_unchanged_rate`,
      });
    }
  }
  const thin = groups.filter((g) => g.spend_known < rule.minTasks);
  if (groups.length > 0 && thin.length > 0) {
    out.push({
      id: 'thin-history',
      text: `${formatCount(thin.length)} of ${formatCount(groups.length)} repository and size groups have fewer than ${rule.minTasks} priced tasks.`,
      evidence: `Their budget stays the flat one of the size class until ${rule.minTasks} finished tasks fall inside ${rule.windowDays} days.`,
      act: 'Nothing to do: the budget moves to the history percentile as tasks finish.',
      source: 'GET /v1/learn · groups[].spend_known',
    });
  }
  return out;
}

const PRIORITY = ['low-completion', 'edited-merges', 'early-checkpoint', 'thin-history'];

/** Lessons in the order worth reading: tasks that fail first, then rewritten pull requests, then checkpoints, then thin history. */
export function rankLessons(all: readonly Lesson[]): Lesson[] {
  const rank = (l: Lesson) => PRIORITY.indexOf(l.id.split(':')[0] ?? '');
  return [...all].sort((a, b) => rank(a) - rank(b));
}

/** The patterns to list: in rank order, at most `MAX_PER_KIND` of a kind and `MAX_LESSONS` in all; `more` counts the ones left out. */
export function pickLessons(ranked: readonly Lesson[]): { shown: Lesson[]; more: number } {
  const perKind = new Map<string, number>();
  const shown: Lesson[] = [];
  for (const lesson of ranked) {
    const kind = lesson.id.split(':')[0] ?? '';
    const taken = perKind.get(kind) ?? 0;
    if (taken >= MAX_PER_KIND || shown.length >= MAX_LESSONS) continue;
    perKind.set(kind, taken + 1);
    shown.push(lesson);
  }
  return { shown, more: ranked.length - shown.length };
}
