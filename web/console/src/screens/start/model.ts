// The composer's logic, with no React: the form's state, the request/v1 it becomes and comes back from, the checks that mirror
// schemas/request/v1 and the server's driven rules (DrivenRules, Accounts), the dependency graph, and the budget estimate.
import type { RepositoryBody } from '../../api/repositories';
import type { LearnGroup, LearnReport, Me } from '../../api/types';

export const SIZES = ['S', 'M', 'L'] as const;
export type Size = (typeof SIZES)[number];

/** schemas/request/v1: `driven.tasks` holds 1 to 20 tasks and `max_parallel` is 1 to 16. */
export const MAX_TASKS = 20;
export const MAX_PARALLEL = 16;

export const ID_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;
export const COMMIT_PATTERN = /^[0-9a-f]{7,40}$/;
export const PRESET_PATTERN = /^[a-z][a-z0-9-]*$/;

/** What the server's governor uses when the profile names no flat budget for a size class (Profile.cs, `DrivenGovernor.DefaultBudgets`). */
export const DEFAULT_FLAT_BUDGETS: Readonly<Record<Size, number>> = { S: 1.5, M: 4, L: 10 };

export type PlanChoice = 'default' | 'on' | 'off';

export interface TaskDraft {
  /** Names the row for React and for focus; never sent. */
  key: string;
  id: string;
  goal: string;
  /** `''` leaves the size to the planner (or to M). Any other text than S, M or L is kept so that it can be reported. */
  size: string;
  /** `budget_usd` as typed. */
  budget: string;
  dependsOn: string[];
  /** Fields the form has no control for (`ref`, `max_turns`, `max_minutes`, `continue_from`, `answers`): kept so a round trip loses nothing. */
  extra: Record<string, unknown>;
}

export interface Draft {
  /** `text`: names the batch in lists. */
  text: string;
  preset: string;
  /** `context.repository.path`. */
  repository: string;
  /** `context.repository.commit`. */
  commit: string;
  tasks: TaskDraft[];
  plan: PlanChoice;
  maxParallel: string;
  maxTokens: string;
  maxUsd: string;
  /** What the form has no control for, per object of the request (`inputs`, `caller_blocks`, `context.verify`, ...). */
  extra: {
    root: Record<string, unknown>;
    context: Record<string, unknown>;
    driven: Record<string, unknown>;
  };
}

export type Json = Record<string, unknown>;

let counter = 0;
export const newKey = (): string => `row${++counter}`;

export function emptyTask(over: Partial<TaskDraft> = {}): TaskDraft {
  return {
    key: newKey(),
    id: '',
    goal: '',
    size: 'M',
    budget: '',
    dependsOn: [],
    extra: {},
    ...over,
  };
}

export function emptyDraft(over: Partial<Draft> = {}): Draft {
  return {
    text: '',
    preset: 'driven',
    repository: '',
    commit: '',
    tasks: [emptyTask({ id: 'task-1' })],
    plan: 'on',
    maxParallel: '',
    maxTokens: '',
    maxUsd: '',
    extra: { root: {}, context: {}, driven: {} },
    ...over,
  };
}

/** `task-N` for the first N that no task uses. */
export function nextTaskId(tasks: readonly TaskDraft[]): string {
  const taken = new Set(tasks.map((t) => t.id));
  for (let n = 1; ; n++) if (!taken.has(`task-${n}`)) return `task-${n}`;
}

/** A task id made of a goal's first words, unique among `taken`. */
export function slugify(goal: string, taken: ReadonlySet<string>): string {
  const words = goal
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  // At most 40 characters, cut between words so an id never ends in half of one.
  const cut =
    words.length <= 40 ? words : words.slice(0, 41).replace(/-?[^-]*$/, '') || words.slice(0, 40);
  const base = cut.replace(/-+$/, '') || 'task';
  let id = base;
  for (let n = 2; taken.has(id); n++) id = `${base.slice(0, 60)}-${n}`;
  return id;
}

// ---------- editing the task list ----------

/** Gives a task a new id; tasks that waited for the old one wait for the new one. */
export function renameTask(tasks: readonly TaskDraft[], index: number, id: string): TaskDraft[] {
  const old = tasks[index]?.id ?? '';
  const unique = old !== '' && tasks.filter((t) => t.id === old).length === 1;
  return tasks.map((t, i) =>
    i === index
      ? { ...t, id }
      : unique && t.dependsOn.includes(old)
        ? { ...t, dependsOn: t.dependsOn.map((d) => (d === old ? id : d)) }
        : t,
  );
}

/** Takes a task out, and its id out of whatever waited for it. */
export function removeTask(tasks: readonly TaskDraft[], index: number): TaskDraft[] {
  const gone = tasks[index]?.id ?? '';
  const unique = tasks.filter((t) => t.id === gone).length === 1;
  return tasks
    .filter((_, i) => i !== index)
    .map((t) =>
      unique && t.dependsOn.includes(gone)
        ? { ...t, dependsOn: t.dependsOn.filter((d) => d !== gone) }
        : t,
    );
}

/** Moves a task `by` places up (negative) or down; the list is returned as it was when it would leave the list. */
export function moveTask(tasks: readonly TaskDraft[], index: number, by: number): TaskDraft[] {
  const to = index + by;
  if (to < 0 || to >= tasks.length || by === 0) return [...tasks];
  const next = [...tasks];
  const [moved] = next.splice(index, 1);
  next.splice(to, 0, moved as TaskDraft);
  return next;
}

// ---------- numbers typed into the caps ----------

/** A whole number of tokens: `2000000`, `2,000,000`, `500k` or `2m`. Null for anything else. */
export function parseCount(text: string): number | null {
  const match = /^(\d[\d,_ ]*)\s*([km]?)$/i.exec(text.trim());
  if (!match) return null;
  const digits = Number((match[1] ?? '').replace(/[,_ ]/g, ''));
  const unit = (match[2] ?? '').toLowerCase();
  const value = unit === 'k' ? digits * 1e3 : unit === 'm' ? digits * 1e6 : digits;
  return Number.isSafeInteger(value) ? value : null;
}

/** Dollars: `12`, `12.5`, `$12.00`. Null for anything else. */
export function parseMoney(text: string): number | null {
  const match = /^\$?\s*(\d[\d,]*(?:\.\d+)?|\.\d+)$/.exec(text.trim());
  if (!match) return null;
  const value = Number((match[1] ?? '').replace(/,/g, ''));
  return Number.isFinite(value) ? value : null;
}

// ---------- the request ----------

const blank = (text: string) => text.trim() === '';

/** What the Draft becomes on the wire. A number that does not parse stays as typed, so the JSON view shows what the checks object to. */
export function toRequest(d: Draft): Json {
  const driven: Json = {};
  if (d.plan !== 'default') driven.plan = d.plan === 'on';
  if (!blank(d.maxParallel))
    driven.max_parallel = parseCount(d.maxParallel) ?? d.maxParallel.trim();
  if (!blank(d.maxTokens)) driven.max_tokens_total = parseCount(d.maxTokens) ?? d.maxTokens.trim();
  if (!blank(d.maxUsd)) driven.max_usd_total = parseMoney(d.maxUsd) ?? d.maxUsd.trim();
  driven.draft_pr = true;
  Object.assign(driven, d.extra.driven);
  driven.tasks = d.tasks.map(taskRequest);

  const repository: Json = {};
  if (!blank(d.repository) || !blank(d.commit)) {
    repository.path = d.repository.trim();
    if (!blank(d.commit)) repository.commit = d.commit.trim();
  }
  const context: Json = { interactive: false, preset: d.preset.trim() };
  if (Object.keys(repository).length > 0) context.repository = repository;
  Object.assign(context, d.extra.context);

  return { contract_version: 'request/v1', text: d.text.trim(), context, driven, ...d.extra.root };
}

function taskRequest(t: TaskDraft): Json {
  const out: Json = { id: t.id.trim() };
  // A task with a ref or a continue_from takes its goal from there; a task with nothing shows its empty goal, which the checks name.
  if (!blank(t.goal) || !hasOtherSource(t)) out.goal = t.goal;
  if (!blank(t.size)) out.size = t.size;
  if (!blank(t.budget)) out.budget_usd = parseMoney(t.budget) ?? t.budget.trim();
  if (t.dependsOn.length > 0) out.depends_on = [...t.dependsOn];
  return Object.assign(out, t.extra);
}

const hasOtherSource = (t: Pick<TaskDraft, 'extra'>) =>
  t.extra.ref !== undefined || t.extra.continue_from !== undefined;

export const toJson = (d: Draft): string => JSON.stringify(toRequest(d), null, 2);

const isObject = (v: unknown): v is Json =>
  typeof v === 'object' && v !== null && !Array.isArray(v);
const asText = (v: unknown): string =>
  typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : '';

function rest(source: Json, known: readonly string[]): Json {
  return Object.fromEntries(Object.entries(source).filter(([key]) => !known.includes(key)));
}

/**
 * A request/v1 (whole, or the part a caller had) as a Draft. It is forgiving about what is missing and keeps what it cannot show;
 * what it cannot make sense of comes back as notes, which the checks list with the rest.
 */
export function fromRequest(value: unknown): { draft: Draft; notes: Issue[] } {
  const notes: Issue[] = [];
  const draft = emptyDraft({ tasks: [] });
  if (!isObject(value)) {
    notes.push({
      path: 'json',
      level: 'warning',
      message: 'The request was not a JSON object, so the form starts empty.',
    });
    return { draft: emptyDraft(), notes };
  }
  draft.text = asText(value.text);
  draft.extra.root = rest(value, ['contract_version', 'text', 'context', 'driven']);
  if (value.contract_version !== undefined && value.contract_version !== 'request/v1')
    notes.push({
      path: 'json',
      level: 'warning',
      message: 'contract_version is sent as "request/v1".',
    });

  const context = isObject(value.context) ? value.context : {};
  if (value.context !== undefined && !isObject(value.context))
    notes.push({
      path: 'json',
      level: 'warning',
      message: 'context is not an object and was left out.',
    });
  if (context.preset !== undefined) draft.preset = asText(context.preset);
  if (context.interactive === true)
    notes.push({
      path: 'json',
      level: 'warning',
      message: 'context.interactive is sent as false: a batch never waits for a question.',
    });
  const repository = isObject(context.repository) ? context.repository : {};
  draft.repository = asText(repository.path);
  draft.commit = asText(repository.commit);
  draft.extra.context = rest(context, ['interactive', 'preset', 'repository']);
  const extraRepository = rest(repository, ['path', 'commit']);
  if (Object.keys(extraRepository).length > 0)
    notes.push({
      path: 'json',
      level: 'warning',
      message: `context.repository has no field named ${Object.keys(extraRepository).join(', ')}; it was left out.`,
    });

  const driven = isObject(value.driven) ? value.driven : {};
  if (value.driven !== undefined && !isObject(value.driven))
    notes.push({
      path: 'json',
      level: 'warning',
      message: 'driven is not an object and was left out.',
    });
  draft.plan = driven.plan === true ? 'on' : driven.plan === false ? 'off' : 'default';
  draft.maxParallel = asText(driven.max_parallel);
  draft.maxTokens = asText(driven.max_tokens_total);
  draft.maxUsd = asText(driven.max_usd_total);
  if (driven.draft_pr !== undefined && driven.draft_pr !== true)
    notes.push({
      path: 'draft_pr',
      level: 'warning',
      message:
        'draft_pr can only be true: pull requests always open as drafts, so it is sent as true.',
    });
  draft.extra.driven = rest(driven, [
    'tasks',
    'plan',
    'max_parallel',
    'max_tokens_total',
    'max_usd_total',
    'draft_pr',
  ]);
  if (driven.tasks !== undefined && !Array.isArray(driven.tasks))
    notes.push({
      path: 'tasks',
      level: 'warning',
      message: 'driven.tasks is not a list and was left out.',
    });
  for (const raw of Array.isArray(driven.tasks) ? driven.tasks : []) {
    if (!isObject(raw)) {
      notes.push({
        path: 'tasks',
        level: 'warning',
        message: 'A task that is not an object was left out.',
      });
      continue;
    }
    draft.tasks.push(
      emptyTask({
        id: asText(raw.id),
        goal: asText(raw.goal),
        size: raw.size === undefined ? '' : asText(raw.size),
        budget: asText(raw.budget_usd),
        dependsOn: Array.isArray(raw.depends_on)
          ? raw.depends_on.filter((d): d is string => typeof d === 'string')
          : [],
        extra: rest(raw, ['id', 'goal', 'size', 'budget_usd', 'depends_on']),
      }),
    );
  }
  return { draft, notes };
}

export type ParsedJson = { ok: true; draft: Draft; notes: Issue[] } | { ok: false; error: string };

/** The JSON view's text as a Draft, or the parser's complaint. */
export function parseDraftJson(text: string): ParsedJson {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Not valid JSON.' };
  }
  if (!isObject(value)) return { ok: false, error: 'The request must be a JSON object.' };
  return { ok: true, ...fromRequest(value) };
}

// ---------- checks ----------

export interface Issue {
  /**
   * Where it shows: `text`, `preset`, `repository`, `commit`, `tasks`, `tasks.<index>.<id|goal|size|budget|depends_on>`,
   * `max_parallel`, `max_tokens_total`, `max_usd_total`, `caps`, `account`, `draft_pr` or `json`.
   */
  path: string;
  message: string;
  /** An error stops the start; a warning says what the server will probably do. */
  level: 'error' | 'warning';
  /** Something still to fill in: shown once a start was tried, so an empty form does not open red. */
  late?: boolean;
}

export interface CheckEnv {
  me?: Me | null;
  /** The checkouts the server lists for this account; absent while unknown. */
  repositories?: readonly RepositoryBody[] | null;
  /** The presets a batch may use; absent while unknown. */
  presets?: readonly string[] | null;
}

/** The ids of one dependency cycle, the first repeated at the end, or empty (the server's DrivenRules.Cycle). */
export function findCycle(tasks: readonly Pick<TaskDraft, 'id' | 'dependsOn'>[]): string[] {
  const graph = new Map<string, readonly string[]>();
  for (const t of tasks) if (!graph.has(t.id)) graph.set(t.id, t.dependsOn);
  const mark = new Map<string, 1 | 2>();
  const path: string[] = [];
  const visit = (id: string): string[] => {
    mark.set(id, 1);
    path.push(id);
    for (const next of graph.get(id) ?? []) {
      if (!graph.has(next)) continue;
      if (mark.get(next) === 1) return [...path.slice(path.indexOf(next)), next];
      if (!mark.has(next)) {
        const found = visit(next);
        if (found.length > 0) return found;
      }
    }
    path.pop();
    mark.set(id, 2);
    return [];
  };
  for (const id of graph.keys()) {
    if (mark.has(id)) continue;
    const found = visit(id);
    if (found.length > 0) return found;
  }
  return [];
}

/** Whether `id` depends on `target`, directly or down the chain. */
export function dependsOnAnyDepth(
  tasks: readonly Pick<TaskDraft, 'id' | 'dependsOn'>[],
  id: string,
  target: string,
): boolean {
  const seen = new Set<string>();
  const stack = [id];
  while (stack.length > 0) {
    const current = stack.pop() as string;
    if (seen.has(current)) continue;
    seen.add(current);
    for (const t of tasks)
      if (t.id === current)
        for (const dep of t.dependsOn) {
          if (dep === target) return true;
          stack.push(dep);
        }
  }
  return false;
}

/** Whether `task` depending on `dependency` would close a loop. */
export const wouldCycle = (
  tasks: readonly Pick<TaskDraft, 'id' | 'dependsOn'>[],
  task: string,
  dependency: string,
): boolean => task === dependency || dependsOnAnyDepth(tasks, dependency, task);

const usdText = (value: number) => `$${value.toFixed(2)}`;

/** What is left of an account's caps in the day and the month, the tightest of each unit. */
export function remaining(me: Me | null | undefined): {
  usd: number | null;
  tokens: number | null;
} {
  if (!me) return { usd: null, tokens: null };
  const left = (caps: Array<[number | undefined, number]>) => {
    const values = caps
      .filter(([cap]) => cap !== undefined)
      .map(([cap, used]) => (cap as number) - used);
    return values.length === 0 ? null : Math.max(0, Math.min(...values));
  };
  return {
    usd: left([
      [me.limits.max_usd_per_day, me.spend.day.usd],
      [me.limits.max_usd_per_month, me.spend.month.usd],
    ]),
    tokens: left([
      [me.limits.max_tokens_per_day, me.spend.day.tokens],
      [me.limits.max_tokens_per_month, me.spend.month.tokens],
    ]),
  };
}

/** Every problem of the draft: schemas/request/v1, the rules the server adds (DrivenRules), and what the account may do. */
export function check(d: Draft, env: CheckEnv = {}): Issue[] {
  const issues: Issue[] = [];
  const error = (path: string, message: string, late = false) =>
    issues.push({ path, message, level: 'error', ...(late ? { late } : {}) });
  const warning = (path: string, message: string) =>
    issues.push({ path, message, level: 'warning' });

  const { me } = env;
  if (me && !me.driven)
    error(
      'account',
      `Account ${me.name} may not start a batch. Ask the owner to allow driven runs.`,
    );

  if (blank(d.text))
    error('text', 'The request needs text: a label that names the batch in lists.', true);

  if (blank(d.preset)) error('preset', 'Choose a preset.', true);
  else if (!PRESET_PATTERN.test(d.preset.trim()))
    error(
      'preset',
      'A preset name is lowercase letters, digits and hyphens, starting with a letter.',
    );
  else if (env.presets && env.presets.length > 0 && !env.presets.includes(d.preset.trim()))
    error(
      'preset',
      `The server has no batch preset named ${d.preset.trim()}. It offers ${env.presets.join(', ')}.`,
    );

  if (blank(d.repository))
    error(
      'repository',
      'A batch needs a repository: choose the checkout the sessions work in.',
      true,
    );
  else if (
    env.repositories &&
    env.repositories.length > 0 &&
    !env.repositories.some((r) => r.path === d.repository.trim())
  )
    warning(
      'repository',
      'The server did not list this path. It refuses a path outside its repository roots.',
    );

  if (blank(d.commit))
    error('commit', 'Give the commit the sessions start from, 7 to 40 hex digits.', true);
  else if (!COMMIT_PATTERN.test(d.commit.trim()))
    error('commit', 'A commit is 7 to 40 lowercase hex digits, such as the start of a SHA.');

  if (d.tasks.length === 0) error('tasks', 'Add at least one task.', true);
  if (d.tasks.length > MAX_TASKS) error('tasks', `A batch has at most ${MAX_TASKS} tasks.`);

  const ids = d.tasks.map((t) => t.id.trim());
  const idSet = new Set(ids);
  // Like the server, look for a cycle only when every depends_on names a task of the batch once, so one mistake is reported once.
  let dependencyProblem = false;
  d.tasks.forEach((t, i) => {
    const at = (field: string) => `tasks.${i}.${field}`;
    const id = t.id.trim();
    if (id === '') error(at('id'), 'Give the task an id.', true);
    else if (!ID_PATTERN.test(id))
      error(
        at('id'),
        'An id uses lowercase letters, digits, - and _, starts with a letter or digit, and has at most 64 characters.',
      );
    else if (ids.filter((other) => other === id).length > 1)
      error(at('id'), `Another task uses the id ${id}.`);

    if (blank(t.goal) && !hasOtherSource(t))
      error(at('goal'), 'Describe what the change should do.', true);

    if (!blank(t.size) && !(SIZES as readonly string[]).includes(t.size))
      error(at('size'), 'Size must be S, M or L.');

    if (!blank(t.budget)) {
      const budget = parseMoney(t.budget);
      if (budget === null || budget <= 0)
        error(at('budget'), 'Budget is a dollar amount above 0, such as 1.50.');
    }

    const wrong = (message: string) => {
      dependencyProblem = true;
      error(at('depends_on'), message);
    };
    if (t.dependsOn.length > MAX_TASKS) wrong(`A task depends on at most ${MAX_TASKS} others.`);
    const seen = new Set<string>();
    for (const dep of t.dependsOn) {
      if (dep === id) wrong('A task cannot depend on itself.');
      else if (!idSet.has(dep)) wrong(`${dep} is not a task of this batch.`);
      if (seen.has(dep)) wrong(`${dep} is listed more than once.`);
      seen.add(dep);
    }
    if (t.dependsOn.length > 0 && t.extra.continue_from !== undefined)
      wrong('depends_on and continue_from start from different work; use one.');
  });
  const cycle = dependencyProblem
    ? []
    : findCycle(d.tasks.map((t) => ({ id: t.id.trim(), dependsOn: t.dependsOn })));
  d.tasks.forEach((t, i) => {
    if (cycle.includes(t.id.trim()))
      error(
        `tasks.${i}.depends_on`,
        `Tasks depend on each other in a cycle: ${cycle.join(' → ')}.`,
      );
  });

  if (!blank(d.maxParallel)) {
    const n = parseCount(d.maxParallel);
    if (n === null || n < 1 || n > MAX_PARALLEL)
      error('max_parallel', `Use a whole number from 1 to ${MAX_PARALLEL}.`);
  }
  if (!blank(d.maxTokens)) {
    const n = parseCount(d.maxTokens);
    if (n === null || n < 1)
      error('max_tokens_total', 'Use a whole number of tokens above 0: 500000, 2,000,000 or 2m.');
  }
  if (!blank(d.maxUsd)) {
    const n = parseMoney(d.maxUsd);
    if (n === null || n <= 0) error('max_usd_total', 'Use a dollar amount above 0, such as 12.00.');
  }
  if (blank(d.maxTokens) && blank(d.maxUsd))
    error(
      'caps',
      'Set a token cap (a subscription) or a dollar cap (an API key). The server refuses a batch with neither.',
      true,
    );

  if (me) {
    const left = remaining(me);
    const usd = parseMoney(d.maxUsd);
    const tokens = parseCount(d.maxTokens);
    if (left.usd !== null && left.usd <= 0)
      warning(
        'account',
        'The account has used its dollar cap for now. The server refuses new batches until it resets.',
      );
    else if (usd !== null && left.usd !== null && usd > left.usd)
      warning(
        'max_usd_total',
        `The account has ${usdText(left.usd)} left. The server cuts the cap to that.`,
      );
    if (left.tokens !== null && left.tokens <= 0)
      warning(
        'account',
        'The account has used its token cap for now. The server refuses new batches until it resets.',
      );
    else if (tokens !== null && left.tokens !== null && tokens > left.tokens)
      warning(
        'max_tokens_total',
        `The account has ${left.tokens.toLocaleString('en-US')} tokens left. The server cuts the cap to that.`,
      );
    const most = me.limits.max_concurrent_runs;
    if (most !== undefined && me.unfinished_runs >= most)
      warning(
        'account',
        `The account has ${me.unfinished_runs} unfinished runs, its limit. The server answers 429 until one ends.`,
      );
  }
  return issues;
}

/** Issues at a path (or below it: `tasks.2` holds `tasks.2.id`). */
export function issuesAt(issues: readonly Issue[], path: string, showLate: boolean): Issue[] {
  return issues.filter(
    (i) => (i.path === path || i.path.startsWith(`${path}.`)) && (showLate || !i.late),
  );
}

export const errorsOf = (issues: readonly Issue[]) => issues.filter((i) => i.level === 'error');

// ---------- the dependency graph ----------

export const NODE_W = 140;
export const NODE_H = 32;
const STEP_X = 170;
const STEP_Y = 40;
const PAD = 12;

export interface GraphNode {
  key: string;
  id: string;
  size: string;
  layer: number;
  x: number;
  y: number;
}

export interface GraphEdge {
  from: string;
  to: string;
  /** An SVG path from the right side of the dependency to the left side of the task that waits for it. */
  d: string;
}

export interface GraphLayout {
  nodes: GraphNode[];
  edges: GraphEdge[];
  width: number;
  height: number;
  /** The longest chain of tasks that wait for each other. */
  chain: number;
  /** The most tasks that could run at once: the widest layer. */
  widest: number;
  cycle: string[];
}

/** Tasks in layers: a task sits one step right of the last task it waits for. A cycle is cut where it closes, so the graph still draws. */
export function layoutGraph(
  tasks: readonly Pick<TaskDraft, 'key' | 'id' | 'size' | 'dependsOn'>[],
): GraphLayout {
  const first = new Map<string, (typeof tasks)[number]>();
  for (const t of tasks) if (t.id !== '' && !first.has(t.id)) first.set(t.id, t);
  const layer = new Map<string, number>();
  const visiting = new Set<string>();
  const depth = (id: string): number => {
    const known = layer.get(id);
    if (known !== undefined) return known;
    if (visiting.has(id)) return 0;
    visiting.add(id);
    let level = 0;
    for (const dep of first.get(id)?.dependsOn ?? [])
      if (first.has(dep) && dep !== id) level = Math.max(level, depth(dep) + 1);
    visiting.delete(id);
    layer.set(id, level);
    return level;
  };
  const rows = new Map<number, number>();
  const nodes: GraphNode[] = tasks.map((t) => {
    const level = t.id === '' ? 0 : depth(t.id);
    const row = rows.get(level) ?? 0;
    rows.set(level, row + 1);
    return {
      key: t.key,
      id: t.id,
      size: t.size,
      layer: level,
      x: PAD + level * STEP_X,
      y: PAD + row * STEP_Y,
    };
  });
  const at = new Map<string, GraphNode>();
  for (const n of nodes) if (n.id !== '' && !at.has(n.id)) at.set(n.id, n);
  const edges: GraphEdge[] = [];
  for (const t of tasks) {
    const to = at.get(t.id);
    for (const dep of new Set(t.dependsOn)) {
      const from = at.get(dep);
      if (!from || !to || from === to) continue;
      const x1 = from.x + NODE_W;
      const y1 = from.y + NODE_H / 2;
      const x2 = to.x;
      const y2 = to.y + NODE_H / 2;
      const mid = (x1 + x2) / 2;
      edges.push({ from: dep, to: t.id, d: `M${x1} ${y1}C${mid} ${y1} ${mid} ${y2} ${x2} ${y2}` });
    }
  }
  const layers = Math.max(0, ...[...layer.values()].map((l) => l + 1));
  const widest = Math.max(0, ...rows.values());
  return {
    nodes,
    edges,
    width: PAD * 2 + Math.max(0, layers - 1) * STEP_X + NODE_W,
    height: PAD * 2 + Math.max(0, widest - 1) * STEP_Y + NODE_H,
    chain: layers,
    widest,
    cycle: findCycle(tasks),
  };
}

// ---------- the estimate ----------

export interface EstimateRules {
  /** Fewest past tasks of a size class before the server trusts its own history over the flat figure. */
  minTasks: number;
  percentile: number;
  windowDays: number;
  flat: Readonly<Record<Size, number>>;
  /** The numbers above are the profile's own (the owner reads them); false: the server's defaults, which a profile may change. */
  fromProfile: boolean;
}

export const DEFAULT_RULES: EstimateRules = {
  minTasks: 5,
  percentile: 80,
  windowDays: 60,
  flat: DEFAULT_FLAT_BUDGETS,
  fromProfile: false,
};

export type EstimateSource = 'task' | 'repository' | 'all' | 'flat';

export interface TaskEstimate {
  usd: number;
  source: EstimateSource;
  /** Past tasks behind a history figure. */
  n?: number;
  /** Repositories pooled into an `all` figure. */
  repositories?: number;
  /** The task named no size, so M is assumed. */
  sizeAssumed: boolean;
  size: Size;
  /** Where the number came from, in words. */
  label: string;
}

export interface BatchEstimate {
  tasks: TaskEstimate[];
  total: number;
  chain: number;
  widest: number;
  /** The history could not be read, so every figure is the flat one. */
  historyMissing: boolean;
}

const known = (g: LearnGroup) => g.spend_known ?? g.tasks;
const figure = (g: LearnGroup, percentile: number) =>
  percentile === 50 ? g.spend_median_usd : g.spend_p80_usd;

/**
 * A task's budget as the server will choose it when the task starts (BudgetHistory.Choose): its own `budget_usd`; else the
 * percentile of this repository's past tasks of the size class when there are enough; else the same over every repository; else
 * the flat figure. /v1/learn gives each repository's figures, not the pooled ones, so "every repository" is the average of the
 * repositories' figures weighted by their task counts. The server counts only finished tasks with a fully priced spend and
 * /v1/learn counts every task with a known spend, so a history figure is the nearest the console can read, not the same number.
 */
export function estimateTask(
  task: Pick<TaskDraft, 'size' | 'budget'>,
  learn: LearnReport | undefined,
  rules: EstimateRules,
  repositoryNames: readonly string[],
): TaskEstimate {
  const named = (SIZES as readonly string[]).includes(task.size) ? (task.size as Size) : null;
  const size: Size = named ?? 'M';
  const sizeAssumed = named === null;
  const own = parseMoney(task.budget);
  if (own !== null && own > 0)
    return { usd: own, source: 'task', sizeAssumed, size, label: "the task's own budget_usd" };

  const groups = (learn?.groups ?? []).filter(
    (g) => g.size_class === size && figure(g, rules.percentile) != null,
  );
  const here = groups.find((g) => repositoryNames.includes(g.repository));
  if (here && known(here) >= rules.minTasks)
    return {
      usd: figure(here, rules.percentile) as number,
      source: 'repository',
      n: known(here),
      sizeAssumed,
      size,
      label: `p${rules.percentile} of ${known(here)} past ${size} tasks in this repository`,
    };
  const pooled = groups.reduce((sum, g) => sum + known(g), 0);
  if (pooled >= rules.minTasks) {
    const usd =
      groups.reduce((sum, g) => sum + (figure(g, rules.percentile) as number) * known(g), 0) /
      pooled;
    return {
      usd,
      source: 'all',
      n: pooled,
      repositories: groups.length,
      sizeAssumed,
      size,
      label: `about p${rules.percentile} of ${pooled} past ${size} tasks in ${groups.length === 1 ? '1 repository' : `${groups.length} repositories`}`,
    };
  }
  const seen =
    pooled > 0
      ? `only ${pooled} past ${size} task${pooled === 1 ? '' : 's'}`
      : `no past ${size} tasks`;
  return {
    usd: rules.flat[size],
    source: 'flat',
    sizeAssumed,
    size,
    label: `the flat ${size} budget; ${seen}, the server needs ${rules.minTasks}`,
  };
}

export function estimateBatch(
  d: Pick<Draft, 'tasks'>,
  learn: LearnReport | undefined,
  rules: EstimateRules,
  repositoryNames: readonly string[],
): BatchEstimate {
  const tasks = d.tasks.map((t) => estimateTask(t, learn, rules, repositoryNames));
  const graph = layoutGraph(d.tasks);
  return {
    tasks,
    total: tasks.reduce((sum, t) => sum + t.usd, 0),
    chain: graph.chain,
    widest: graph.widest,
    historyMissing: learn === undefined,
  };
}

// ---------- tasks from pasted text ----------

export interface Imported {
  tasks: TaskDraft[];
  /** Lines or items that did not fit under the limit of 20 tasks. */
  skipped: number;
  /** The text could not be read. */
  error?: string;
}

const BULLET = /^\s*(?:[-*+•]|\d+[.)])\s+(?:\[[ xX]\]\s+)?/;
const SIZE_PREFIX = /^(?:\[([SMLsml])\]|\(([SMLsml])\)|([SMLsml]):)\s+/;

function fromObject(raw: unknown, taken: Set<string>): TaskDraft | null {
  if (typeof raw === 'string') return fromLine(raw, taken);
  if (!isObject(raw)) return null;
  const goal = asText(raw.goal).trim();
  const id = asText(raw.id).trim() || (goal ? slugify(goal, taken) : '');
  if (id === '' && goal === '' && raw.ref === undefined) return null;
  taken.add(id);
  return emptyTask({
    id,
    goal,
    size: raw.size === undefined ? 'M' : asText(raw.size).toUpperCase(),
    budget: asText(raw.budget_usd),
    dependsOn: Array.isArray(raw.depends_on)
      ? raw.depends_on.filter((x): x is string => typeof x === 'string')
      : [],
    extra: rest(raw, ['id', 'goal', 'size', 'budget_usd', 'depends_on']),
  });
}

function fromLine(line: string, taken: Set<string>): TaskDraft | null {
  let text = line.replace(BULLET, '').trim();
  let size = 'M';
  const mark = SIZE_PREFIX.exec(text);
  if (mark) {
    size = (mark[1] ?? mark[2] ?? mark[3] ?? 'M').toUpperCase();
    text = text.slice(mark[0].length).trim();
  }
  if (text === '') return null;
  const id = slugify(text, taken);
  taken.add(id);
  return emptyTask({ id, goal: text, size });
}

/**
 * Tasks from text a person pasted: one task per line (a leading `-`, `*` or `1.` is dropped, and `[S]`, `(L)` or `M:` sets the
 * size), or a JSON list of goals or task objects, or a request/v1 with a `driven.tasks`. Ids are made from the goal and avoid `existing`.
 */
export function parseImport(
  input: string,
  existing: readonly string[] = [],
  room = MAX_TASKS,
): Imported {
  const text = input.trim();
  if (text === '') return { tasks: [], skipped: 0 };
  const taken = new Set(existing);
  let items: unknown[];
  let value: unknown;
  let parsed = false;
  try {
    value = JSON.parse(text);
    parsed = true;
  } catch (e) {
    // "[S] Fix the login" starts like a JSON list and is not one: only text that opens like JSON is reported as broken JSON.
    if (/^[[{]\s*(?:["{[\]}]|$)/.test(text))
      return {
        tasks: [],
        skipped: 0,
        error: `This looks like JSON but does not parse: ${e instanceof Error ? e.message : 'invalid'}`,
      };
  }
  if (parsed && (Array.isArray(value) || isObject(value))) {
    const list = Array.isArray(value)
      ? value
      : Array.isArray(value.tasks)
        ? value.tasks
        : isObject(value.driven) && Array.isArray(value.driven.tasks)
          ? value.driven.tasks
          : null;
    if (list === null)
      return {
        tasks: [],
        skipped: 0,
        error: 'Expected a list of tasks, or an object with a tasks list.',
      };
    items = list;
  } else {
    items = text.split(/\r?\n/).filter((line) => line.trim() !== '');
  }
  const tasks = items
    .map((item) => fromObject(item, taken))
    .filter((t): t is TaskDraft => t !== null);
  const dropped = items.length - tasks.length;
  return {
    tasks: tasks.slice(0, Math.max(0, room)),
    skipped: Math.max(0, tasks.length - room),
    ...(dropped > 0 && tasks.length === 0
      ? { error: 'Nothing in that text looks like a task.' }
      : {}),
  };
}
