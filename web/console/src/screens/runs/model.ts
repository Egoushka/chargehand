import type { RunFilter, RunState, RunSummary } from '../../api/types';

export type StateFilter = 'all' | 'running' | 'attention' | 'done' | 'failed' | 'cancelled';
export type KindFilter = 'all' | 'batch' | 'single';
export type RangeFilter = '24h' | '7d' | '30d' | 'all';

/** What the toolbar holds. `all` in a field means "no filter on it". */
export interface RunFilters {
  q: string;
  state: StateFilter;
  kind: KindFilter;
  /** `all`, or the repository path exactly as the server names it. */
  repository: string;
  account: string;
  preset: string;
  range: RangeFilter;
  /** A batch's research and review calls are runs of their own; they hide unless asked for. */
  calls: boolean;
}

export const ANY = 'all';

export const DEFAULT_FILTERS: RunFilters = {
  q: '',
  state: 'all',
  kind: 'all',
  repository: ANY,
  account: ANY,
  preset: ANY,
  range: '7d',
  calls: false,
};

export const STATE_OPTIONS: Array<{ id: StateFilter; label: string }> = [
  { id: 'all', label: 'State: any' },
  { id: 'running', label: 'Running' },
  { id: 'attention', label: 'Needs attention' },
  { id: 'done', label: 'Done' },
  { id: 'failed', label: 'Failed' },
  { id: 'cancelled', label: 'Cancelled' },
];

export const RANGE_OPTIONS: Array<{ id: RangeFilter; label: string }> = [
  { id: '24h', label: 'Last 24 hours' },
  { id: '7d', label: 'Last 7 days' },
  { id: '30d', label: 'Last 30 days' },
  { id: 'all', label: 'All time' },
];

const RANGE_MS: Record<Exclude<RangeFilter, 'all'>, number> = {
  '24h': 24 * 3600_000,
  '7d': 7 * 24 * 3600_000,
  '30d': 30 * 24 * 3600_000,
};

/** The server returns at most this many runs per request; there is no cursor, so a longer history needs a narrower filter. */
export const PAGE = 100;
export const MAX_WINDOW = 500;

const isCancelled = (run: RunSummary) => run.status === 'failed' && run.error_code === 'cancelled';

/** The filter in the word the design uses; a state can match more than one server state, so this is a test and not a mapping. */
export function matchesState(run: RunSummary, state: StateFilter): boolean {
  switch (state) {
    case 'all':
      return true;
    case 'running':
      return run.status === 'running' || run.status === 'queued';
    case 'attention':
      return run.status === 'needs_input' || run.status === 'lost' || matchesState(run, 'failed');
    case 'done':
      return run.status === 'completed';
    case 'failed':
      return (run.status === 'failed' && !isCancelled(run)) || run.status === 'denied';
    case 'cancelled':
      return isCancelled(run);
  }
}

/** The one server state a filter is, when it is exactly one: the server then narrows the list before the 500-row cap. */
export function serverStatus(state: StateFilter): RunState | undefined {
  if (state === 'done') return 'completed';
  if (state === 'cancelled') return 'failed';
  return undefined;
}

/** The start of a range as an ISO time, rounded down to the minute so the query key holds still between polls. */
export function rangeSince(range: RangeFilter, now: number): string | undefined {
  if (range === 'all') return undefined;
  return new Date(Math.floor((now - RANGE_MS[range]) / 60_000) * 60_000).toISOString();
}

/** What GET /v1/runs is asked: the fields the server filters on. The rest (search, preset, state words) narrow the rows after. */
export function toRunFilter(filters: RunFilters, now: number, limit: number): RunFilter {
  return {
    limit,
    status: serverStatus(filters.state),
    kind: filters.kind === 'all' ? undefined : filters.kind,
    repository: filters.repository === ANY ? undefined : filters.repository,
    actor: filters.account === ANY ? undefined : filters.account,
    since: rangeSince(filters.range, now),
  };
}

const haystack = (run: RunSummary) =>
  [
    run.run_id,
    run.repository,
    run.branch,
    run.pr_url,
    run.actor,
    run.preset,
    run.error_code,
    run.commit,
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();

/** Whether a run passes every filter. The server narrows by kind, repository, account and date too; this repeats it so the rows are right whatever came back. */
export function matchesFilters(run: RunSummary, filters: RunFilters, now: number): boolean {
  const q = filters.q.trim().toLowerCase();
  if (q && !haystack(run).includes(q)) return false;
  if (!filters.calls && run.parent_run_id) return false;
  if (!matchesState(run, filters.state)) return false;
  if (filters.kind !== 'all' && run.kind !== filters.kind) return false;
  if (filters.repository !== ANY && run.repository !== filters.repository) return false;
  if (filters.account !== ANY && run.actor !== filters.account) return false;
  if (filters.preset !== ANY && run.preset !== filters.preset) return false;
  if (filters.range !== 'all' && Date.parse(run.started_at) < now - RANGE_MS[filters.range])
    return false;
  return true;
}

/** Filters that only the browser applies: the loaded window may then hold fewer matches than the rows asked for. */
export function narrowsInBrowser(filters: RunFilters): boolean {
  return (
    filters.q.trim() !== '' ||
    filters.preset !== ANY ||
    (filters.state !== 'all' && serverStatus(filters.state) === undefined) ||
    filters.state === 'cancelled'
  );
}

/** How many of the toolbar's filters are set, for the phone's button; the date range is not counted when it is the default. */
export function activeCount(filters: RunFilters): number {
  return (
    (filters.q.trim() ? 1 : 0) +
    (filters.state !== 'all' ? 1 : 0) +
    (filters.kind !== 'all' ? 1 : 0) +
    (filters.repository !== ANY ? 1 : 0) +
    (filters.account !== ANY ? 1 : 0) +
    (filters.preset !== ANY ? 1 : 0) +
    (filters.range !== DEFAULT_FILTERS.range ? 1 : 0) +
    (filters.calls ? 1 : 0)
  );
}

export const isActive = (run: RunSummary) => run.status === 'running' || run.status === 'queued';

/** A run that cancel can act on: one that has not ended. */
export const isCancelable = isActive;

export function distinct(
  runs: readonly RunSummary[],
  pick: (run: RunSummary) => string | null | undefined,
) {
  return [...new Set(runs.map(pick).filter((v): v is string => !!v))].sort();
}

/** The options of a select: what the loaded runs hold, plus the chosen value even when no loaded run has it. */
export function options(values: string[], chosen: string, extra: readonly string[] = []): string[] {
  const all = new Set([...values, ...extra]);
  if (chosen !== ANY) all.add(chosen);
  return [...all].sort();
}

/** The pull request number of a link such as https://host/owner/name/pull/412. */
export function pullNumber(url: string | null | undefined): string | null {
  return url ? (/\/pulls?\/(\d+)/.exec(url)?.[1] ?? null) : null;
}

export interface SavedView {
  id: string;
  name: string;
  filters: RunFilters;
}

export const BUILTIN_VIEWS: readonly SavedView[] = [
  { id: 'all', name: 'All runs', filters: DEFAULT_FILTERS },
  {
    id: 'active',
    name: 'Active batches',
    filters: { ...DEFAULT_FILTERS, state: 'running', kind: 'batch' },
  },
  { id: 'attention', name: 'Needs attention', filters: { ...DEFAULT_FILTERS, state: 'attention' } },
  {
    id: 'failed-week',
    name: 'Failed this week',
    filters: { ...DEFAULT_FILTERS, state: 'failed', range: '7d' },
  },
];

export const sameFilters = (a: RunFilters, b: RunFilters) =>
  (Object.keys(DEFAULT_FILTERS) as Array<keyof RunFilters>).every((k) => a[k] === b[k]);

const isOneOf = <T extends string>(value: unknown, list: readonly T[]): value is T =>
  typeof value === 'string' && (list as readonly string[]).includes(value);

/** A stored filter set made safe: unknown fields drop, and a wrong value falls back to the default for that field. */
export function cleanFilters(raw: unknown): RunFilters {
  const f = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const text = (key: keyof RunFilters, fallback: string) =>
    typeof f[key] === 'string' ? (f[key] as string) : fallback;
  return {
    q: text('q', ''),
    state: isOneOf(
      f.state,
      STATE_OPTIONS.map((o) => o.id),
    )
      ? f.state
      : 'all',
    kind: isOneOf(f.kind, ['all', 'batch', 'single'] as const) ? f.kind : 'all',
    repository: text('repository', ANY),
    account: text('account', ANY),
    preset: text('preset', ANY),
    range: isOneOf(
      f.range,
      RANGE_OPTIONS.map((o) => o.id),
    )
      ? f.range
      : DEFAULT_FILTERS.range,
    calls: f.calls === true,
  };
}

/** Saved views from the text localStorage holds; anything that is not a list of named views is dropped. */
export function parseViews(text: string | null): SavedView[] {
  if (!text) return [];
  try {
    const list: unknown = JSON.parse(text);
    if (!Array.isArray(list)) return [];
    return list.flatMap((item): SavedView[] => {
      const v = item as Partial<SavedView> | null;
      if (!v || typeof v.id !== 'string' || typeof v.name !== 'string' || !v.name.trim()) return [];
      return [{ id: v.id, name: v.name.trim(), filters: cleanFilters(v.filters) }];
    });
  } catch {
    return [];
  }
}
