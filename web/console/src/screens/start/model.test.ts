import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { me } from '../../testing/fixtures';
import { learn } from '../../testing/start';
import {
  COMMIT_PATTERN,
  check,
  DEFAULT_RULES,
  type Draft,
  dependsOnAnyDepth,
  emptyDraft,
  emptyTask,
  errorsOf,
  estimateBatch,
  estimateTask,
  findCycle,
  fromRequest,
  ID_PATTERN,
  type Issue,
  issuesAt,
  layoutGraph,
  MAX_PARALLEL,
  MAX_TASKS,
  moveTask,
  nextTaskId,
  PRESET_PATTERN,
  parseCount,
  parseDraftJson,
  parseImport,
  parseMoney,
  remaining,
  removeTask,
  renameTask,
  slugify,
  toJson,
  toRequest,
} from './model';

const REPO = '/srv/work/payments-api';
const COMMIT = 'a91c0ffe21b7';

const task = (id: string, over: Partial<Draft['tasks'][number]> = {}) =>
  emptyTask({ id, goal: `Do ${id}`, size: 'M', ...over });

/** A draft that passes every check. */
const valid = (over: Partial<Draft> = {}): Draft =>
  emptyDraft({
    text: 'Intake aliases and peak memory',
    repository: REPO,
    commit: COMMIT,
    tasks: [task('alias', { size: 'S' }), task('docs', { dependsOn: ['alias'] })],
    maxUsd: '12',
    ...over,
  });

const paths = (issues: Issue[]) => issues.map((i) => i.path);
const messages = (issues: Issue[], path: string) =>
  issues.filter((i) => i.path === path).map((i) => i.message);

describe('the schema the checks mirror', () => {
  const schema = JSON.parse(
    // Vitest runs from web/console, two folders under the repository root.
    readFileSync(resolve(process.cwd(), '../../schemas/request/v1/request.schema.json'), 'utf8'),
  );
  const driven = schema.properties.driven.properties;
  const item = driven.tasks.items.properties;

  it('uses the same limits and patterns as schemas/request/v1', () => {
    expect(MAX_TASKS).toBe(driven.tasks.maxItems);
    expect(MAX_PARALLEL).toBe(driven.max_parallel.maximum);
    expect(ID_PATTERN.source).toBe(item.id.pattern);
    expect(ID_PATTERN.source).toBe(item.depends_on.items.pattern);
    expect(COMMIT_PATTERN.source).toBe(
      schema.properties.context.properties.repository.properties.commit.pattern,
    );
    expect(PRESET_PATTERN.source).toBe(schema.properties.context.properties.preset.pattern);
    expect(item.size.enum).toEqual(['S', 'M', 'L']);
    expect(driven.draft_pr.const).toBe(true);
  });

  it('builds a request with every required field of the schema', () => {
    const request = toRequest(valid());
    for (const key of schema.required) expect(request).toHaveProperty(key);
    const context = request.context as Record<string, unknown>;
    for (const key of schema.properties.context.required) expect(context).toHaveProperty(key);
    for (const key of Object.keys(request)) expect(schema.properties).toHaveProperty(key);
    for (const key of Object.keys(request.driven as object)) expect(driven).toHaveProperty(key);
  });
});

describe('numbers typed into the caps', () => {
  it('reads tokens with separators and k or m', () => {
    expect(parseCount('2000000')).toBe(2_000_000);
    expect(parseCount('2,000,000')).toBe(2_000_000);
    expect(parseCount('2m')).toBe(2_000_000);
    expect(parseCount('500K')).toBe(500_000);
    expect(parseCount(' 3 ')).toBe(3);
  });
  it('refuses what is not a whole number', () => {
    for (const text of ['', 'abc', '1.5', '-2', '2x', '1e6']) expect(parseCount(text)).toBeNull();
  });
  it('reads dollars with or without a sign', () => {
    expect(parseMoney('12')).toBe(12);
    expect(parseMoney('$12.50')).toBe(12.5);
    expect(parseMoney('.5')).toBe(0.5);
    expect(parseMoney('1,200.25')).toBe(1200.25);
    for (const text of ['', 'abc', '-1', '1.2.3', '$']) expect(parseMoney(text)).toBeNull();
  });
});

describe('the request a draft becomes', () => {
  it('is a batch: non-interactive, draft pull requests only, the repository and commit, the tasks last', () => {
    const request = toRequest(valid({ plan: 'on', maxParallel: '3', maxTokens: '2,000,000' }));
    expect(request).toEqual({
      contract_version: 'request/v1',
      text: 'Intake aliases and peak memory',
      context: { interactive: false, preset: 'driven', repository: { path: REPO, commit: COMMIT } },
      driven: {
        plan: true,
        max_parallel: 3,
        max_tokens_total: 2_000_000,
        max_usd_total: 12,
        draft_pr: true,
        tasks: [
          { id: 'alias', goal: 'Do alias', size: 'S' },
          { id: 'docs', goal: 'Do docs', size: 'M', depends_on: ['alias'] },
        ],
      },
    });
    expect(Object.keys((request.driven as object) ?? {}).at(-1)).toBe('tasks');
  });

  it('leaves out what is not set: the plan choice, caps, an empty size and budget, no dependencies', () => {
    const request = toRequest(
      valid({ plan: 'default', maxUsd: '', maxTokens: '5m', tasks: [task('only', { size: '' })] }),
    );
    const driven = request.driven as Record<string, unknown>;
    expect(driven).not.toHaveProperty('plan');
    expect(driven).not.toHaveProperty('max_parallel');
    expect(driven).not.toHaveProperty('max_usd_total');
    expect(driven.max_tokens_total).toBe(5_000_000);
    expect(driven.tasks).toEqual([{ id: 'only', goal: 'Do only' }]);
  });

  it('says plan false when planning is skipped, and a task budget as a number', () => {
    const request = toRequest(valid({ plan: 'off', tasks: [task('a', { budget: '$1.50' })] }));
    const driven = request.driven as { plan: boolean; tasks: Array<{ budget_usd: number }> };
    expect(driven.plan).toBe(false);
    expect(driven.tasks[0]?.budget_usd).toBe(1.5);
  });

  it('keeps a number it cannot read as typed, so the JSON shows what the checks object to', () => {
    const driven = toRequest(valid({ maxParallel: 'many' })).driven as Record<string, unknown>;
    expect(driven.max_parallel).toBe('many');
  });

  it('shows an empty goal rather than hiding it, unless the task has another source for it', () => {
    const empty = toRequest(valid({ tasks: [task('a', { goal: '' })] }));
    expect((empty.driven as { tasks: unknown[] }).tasks).toEqual([
      { id: 'a', goal: '', size: 'M' },
    ]);
    const withRef = toRequest(
      valid({ tasks: [task('a', { goal: '', extra: { ref: 'ISSUE-4' } })] }),
    );
    expect((withRef.driven as { tasks: unknown[] }).tasks).toEqual([
      { id: 'a', size: 'M', ref: 'ISSUE-4' },
    ]);
  });
});

describe('JSON and the form round-trip', () => {
  it('turns a draft into JSON and back into the same JSON', () => {
    const draft = valid({
      plan: 'on',
      maxParallel: '2',
      maxTokens: '1500000',
      tasks: [
        task('a', { size: 'S', budget: '0.75' }),
        task('b', { size: '', dependsOn: ['a'] }),
        task('c', { size: 'L', dependsOn: ['a', 'b'] }),
      ],
    });
    const text = toJson(draft);
    const parsed = parseDraftJson(text);
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    expect(parsed.notes).toEqual([]);
    expect(toJson(parsed.draft)).toBe(text);
    expect(parsed.draft.tasks.map((t) => [t.id, t.size, t.budget, t.dependsOn])).toEqual([
      ['a', 'S', '0.75', []],
      ['b', '', '', ['a']],
      ['c', 'L', '', ['a', 'b']],
    ]);
  });

  it('keeps the fields the form has no control for, in the request, the context, the batch and each task', () => {
    const request = {
      contract_version: 'request/v1',
      text: 'Review round',
      inputs: [{ id: 'i1', kind: 'note', text: 'x' }],
      context: {
        interactive: false,
        preset: 'driven',
        approved: true,
        verify: ['sh', '-c', 'npm test'],
        repository: { path: REPO, commit: COMMIT },
      },
      driven: {
        max_usd_total: 5,
        draft_pr: true,
        tasks: [
          { id: 'a', ref: 'ISSUE-1', max_turns: 30, max_minutes: 20 },
          { id: 'b', continue_from: { run_id: 'run-1', task_id: 'x' }, answers: ['yes'] },
        ],
      },
    };
    const { draft, notes } = fromRequest(request);
    expect(notes).toEqual([]);
    expect(toRequest(draft)).toMatchObject({
      inputs: request.inputs,
      context: { approved: true, verify: ['sh', '-c', 'npm test'] },
      driven: {
        tasks: [
          { id: 'a', ref: 'ISSUE-1', max_turns: 30, max_minutes: 20 },
          { id: 'b', continue_from: { run_id: 'run-1', task_id: 'x' }, answers: ['yes'] },
        ],
      },
    });
    // A task with a ref or a continue_from needs no goal.
    expect(paths(check(draft, {}))).not.toContain('tasks.0.goal');
    expect(paths(check(draft, {}))).not.toContain('tasks.1.goal');
  });

  it('is forgiving about a partial request and keeps the sizes it does not know', () => {
    const { draft } = fromRequest({
      text: 'x',
      driven: {
        tasks: [
          { id: 'a', goal: 'g', size: 'XL' },
          { id: 'b', goal: 'h' },
        ],
      },
    });
    expect(draft.preset).toBe('driven');
    expect(draft.tasks.map((t) => t.size)).toEqual(['XL', '']);
    expect(messages(check(draft, {}), 'tasks.0.size')).toEqual(['Size must be S, M or L.']);
  });

  it('reports what it could not carry over, as warnings, since the request it sends is whole', () => {
    expect(fromRequest('nope').notes[0]?.message).toBe(
      'The request was not a JSON object, so the form starts empty.',
    );
    const { notes } = fromRequest({
      contract_version: 'request/v2',
      context: { interactive: true, repository: { path: REPO, commit: COMMIT, branch: 'main' } },
      driven: { draft_pr: false, tasks: ['a string', { id: 'a', goal: 'g' }] },
    });
    expect(notes.map((n) => n.message)).toEqual([
      'contract_version is sent as "request/v1".',
      'context.interactive is sent as false: a batch never waits for a question.',
      'context.repository has no field named branch; it was left out.',
      'draft_pr can only be true: pull requests always open as drafts, so it is sent as true.',
      'A task that is not an object was left out.',
    ]);
    expect(notes.every((n) => n.level === 'warning')).toBe(true);
    // Whatever the JSON said, what is sent is a draft pull request batch that does not wait.
    const sent = toRequest(fromRequest({ driven: { draft_pr: false, tasks: [] } }).draft);
    expect((sent.driven as { draft_pr: boolean }).draft_pr).toBe(true);
    expect((sent.context as { interactive: boolean }).interactive).toBe(false);
  });

  it('returns the parser’s complaint for text that is not JSON, or not an object', () => {
    const bad = parseDraftJson('{ "text": ');
    expect(bad.ok).toBe(false);
    expect(parseDraftJson('[1]')).toEqual({
      ok: false,
      error: 'The request must be a JSON object.',
    });
  });
});

describe('the checks', () => {
  it('pass a complete request with nothing to say', () => {
    expect(
      check(valid(), { me: me({ driven: true }), presets: ['driven'], repositories: [] }),
    ).toEqual([]);
  });

  it('hold back "not filled in yet" until a start is tried, and mark it so', () => {
    const issues = check(emptyDraft());
    const late = issues.filter((i) => i.late).map((i) => i.path);
    expect(late).toEqual(
      expect.arrayContaining(['text', 'repository', 'commit', 'tasks.0.goal', 'caps']),
    );
    expect(issuesAt(issues, 'text', false)).toEqual([]);
    expect(issuesAt(issues, 'text', true)).toHaveLength(1);
    // A task’s id has a default; a wrong id is never late.
    expect(
      issuesAt(check(valid({ tasks: [task('Bad Id')] })), 'tasks.0', false).map((i) => i.path),
    ).toEqual(['tasks.0.id']);
  });

  it('check the label, preset, repository and commit', () => {
    const issues = check(valid({ text: ' ', preset: 'Driven!', repository: '', commit: 'xyz' }), {
      presets: ['driven'],
    });
    expect(paths(errorsOf(issues))).toEqual(['text', 'preset', 'repository', 'commit']);
    expect(messages(issues, 'commit')).toEqual([
      'A commit is 7 to 40 lowercase hex digits, such as the start of a SHA.',
    ]);
    expect(
      messages(check(valid({ preset: 'cheap' }), { presets: ['driven', 'draft'] }), 'preset'),
    ).toEqual(['The server has no batch preset named cheap. It offers driven, draft.']);
  });

  it('warn about a repository the server did not list, without blocking', () => {
    const issues = check(valid(), { repositories: [{ path: '/elsewhere' } as never] });
    expect(issues).toEqual([expect.objectContaining({ path: 'repository', level: 'warning' })]);
    expect(errorsOf(issues)).toEqual([]);
  });

  it('check the number of tasks, their ids and goals, sizes and budgets', () => {
    const many = Array.from({ length: MAX_TASKS + 1 }, (_, i) => task(`t${i}`));
    expect(messages(check(valid({ tasks: many })), 'tasks')).toEqual([
      'A batch has at most 20 tasks.',
    ]);
    expect(messages(check(valid({ tasks: [] })), 'tasks')).toEqual(['Add at least one task.']);

    const issues = check(
      valid({
        tasks: [
          task('same'),
          task('same'),
          task('-bad'),
          task(''),
          task('ok', { goal: '  ', size: 'XL', budget: '0' }),
          task('also-ok', { budget: 'cheap' }),
          task('a'.repeat(65)),
        ],
      }),
    );
    expect(messages(issues, 'tasks.0.id')).toEqual(['Another task uses the id same.']);
    expect(messages(issues, 'tasks.1.id')).toEqual(['Another task uses the id same.']);
    expect(messages(issues, 'tasks.2.id')[0]).toMatch(/^An id uses lowercase letters/);
    expect(messages(issues, 'tasks.3.id')).toEqual(['Give the task an id.']);
    expect(messages(issues, 'tasks.4.goal')).toEqual(['Describe what the change should do.']);
    expect(messages(issues, 'tasks.4.size')).toEqual(['Size must be S, M or L.']);
    expect(messages(issues, 'tasks.4.budget')).toEqual([
      'Budget is a dollar amount above 0, such as 1.50.',
    ]);
    expect(messages(issues, 'tasks.5.budget')).toHaveLength(1);
    expect(messages(issues, 'tasks.6.id')).toHaveLength(1);
  });

  it('check depends_on: itself, an unknown id, a repeat and continue_from', () => {
    const issues = check(
      valid({
        tasks: [
          task('a', { dependsOn: ['a', 'ghost', 'b', 'b'] }),
          task('b', { dependsOn: ['a'] }),
          task('c', { dependsOn: ['b'], extra: { continue_from: { run_id: 'r', task_id: 't' } } }),
        ],
      }),
    );
    expect(messages(issues, 'tasks.0.depends_on')).toEqual([
      'A task cannot depend on itself.',
      'ghost is not a task of this batch.',
      'b is listed more than once.',
    ]);
    expect(messages(issues, 'tasks.1.depends_on')).toEqual([]);
    expect(messages(issues, 'tasks.2.depends_on')).toEqual([
      'depends_on and continue_from start from different work; use one.',
    ]);
  });

  it('check dependencies for a cycle, and tell every task in it, not the others', () => {
    const issues = check(
      valid({
        tasks: [
          task('a', { dependsOn: ['c'] }),
          task('b', { dependsOn: ['a'] }),
          task('c', { dependsOn: ['b'] }),
          task('d', { dependsOn: ['a'] }),
        ],
      }),
    );
    const cycle = ['Tasks depend on each other in a cycle: a → c → b → a.'];
    expect(messages(issues, 'tasks.0.depends_on')).toEqual(cycle);
    expect(messages(issues, 'tasks.1.depends_on')).toEqual(cycle);
    expect(messages(issues, 'tasks.2.depends_on')).toEqual(cycle);
    expect(messages(issues, 'tasks.3.depends_on')).toEqual([]);
    expect(errorsOf(issues)).toHaveLength(3);
  });

  it('check the caps: parallelism, tokens and dollars each at their field, and one of the two caps at least', () => {
    const issues = check(valid({ maxParallel: '17', maxTokens: '12.5', maxUsd: '0' }));
    expect(messages(issues, 'max_parallel')).toEqual(['Use a whole number from 1 to 16.']);
    expect(messages(issues, 'max_tokens_total')).toHaveLength(1);
    expect(messages(issues, 'max_usd_total')).toHaveLength(1);
    expect(paths(check(valid({ maxTokens: '', maxUsd: '' })))).toEqual(['caps']);
    expect(paths(check(valid({ maxTokens: '1m', maxUsd: '' })))).toEqual([]);
    expect(paths(check(valid({ maxParallel: '16' })))).toEqual([]);
    expect(paths(check(valid({ maxParallel: '0' })))).toEqual(['max_parallel']);
  });

  it('say when the account may not start a batch', () => {
    const issues = check(valid(), { me: me({ name: 'ci-bot', driven: false }) });
    expect(messages(issues, 'account')).toEqual([
      'Account ci-bot may not start a batch. Ask the owner to allow driven runs.',
    ]);
    expect(errorsOf(issues)).toHaveLength(1);
  });

  it('warn, without blocking, about caps above what the account has left and a full concurrency limit', () => {
    const account = me({
      limits: { max_usd_per_day: 20, max_tokens_per_day: 1_000_000, max_concurrent_runs: 2 },
      unfinished_runs: 2,
    });
    // $26.14 spent today against a $20 cap leaves nothing; widen the cap to leave $8.
    account.limits.max_usd_per_day = 34.14;
    account.spend.day.tokens = 400_000;
    const issues = check(valid({ maxUsd: '12', maxTokens: '2m' }), { me: account });
    expect(errorsOf(issues)).toEqual([]);
    expect(messages(issues, 'max_usd_total')).toEqual([
      'The account has $8.00 left. The server cuts the cap to that.',
    ]);
    expect(messages(issues, 'max_tokens_total')).toEqual([
      'The account has 600,000 tokens left. The server cuts the cap to that.',
    ]);
    expect(messages(issues, 'account')).toEqual([
      'The account has 2 unfinished runs, its limit. The server answers 429 until one ends.',
    ]);
    expect(remaining(account)).toEqual({ usd: 8, tokens: 600_000 });
    expect(remaining(null)).toEqual({ usd: null, tokens: null });
  });

  it('warn that a used-up cap refuses new batches', () => {
    const account = me({ limits: { max_usd_per_day: 20 } });
    expect(messages(check(valid(), { me: account }), 'account')).toEqual([
      'The account has used its dollar cap for now. The server refuses new batches until it resets.',
    ]);
  });
});

describe('cycles', () => {
  const t = (id: string, ...dependsOn: string[]) => ({ id, dependsOn });

  it('finds none in a chain or a diamond, and one in a loop, naming it', () => {
    expect(findCycle([t('a'), t('b', 'a'), t('c', 'b')])).toEqual([]);
    expect(findCycle([t('a'), t('b', 'a'), t('c', 'a'), t('d', 'b', 'c')])).toEqual([]);
    expect(findCycle([t('a', 'c'), t('b', 'a'), t('c', 'b')])).toEqual(['a', 'c', 'b', 'a']);
    expect(findCycle([t('a', 'a')])).toEqual(['a', 'a']);
  });

  it('ignores ids that are not tasks', () => {
    expect(findCycle([t('a', 'ghost')])).toEqual([]);
  });

  it('says whether a dependency would close a loop', () => {
    const tasks = [t('a'), t('b', 'a'), t('c', 'b')];
    expect(dependsOnAnyDepth(tasks, 'c', 'a')).toBe(true);
    expect(dependsOnAnyDepth(tasks, 'a', 'c')).toBe(false);
  });
});

describe('the dependency graph', () => {
  const g = (id: string, ...dependsOn: string[]) => ({ key: `k-${id}`, id, size: 'M', dependsOn });

  it('puts a task one layer right of the last task it waits for, and counts the chain and the widest layer', () => {
    const layout = layoutGraph([g('a'), g('b'), g('c', 'a'), g('d', 'a', 'c'), g('e', 'b')]);
    expect(Object.fromEntries(layout.nodes.map((n) => [n.id, n.layer]))).toEqual({
      a: 0,
      b: 0,
      c: 1,
      d: 2,
      e: 1,
    });
    expect(layout.chain).toBe(3);
    expect(layout.widest).toBe(2);
    expect(layout.edges.map((e) => `${e.from}>${e.to}`)).toEqual(['a>c', 'a>d', 'c>d', 'b>e']);
    expect(layout.cycle).toEqual([]);
    expect(layout.width).toBeGreaterThan(layout.nodes[0]?.x ?? 0);
    expect(layout.height).toBeGreaterThan(0);
  });

  it('draws independent tasks in one layer, and nothing for no tasks', () => {
    const layout = layoutGraph([g('a'), g('b'), g('c')]);
    expect(layout.chain).toBe(1);
    expect(layout.widest).toBe(3);
    expect(layout.edges).toEqual([]);
    expect(layoutGraph([])).toMatchObject({ nodes: [], chain: 0, widest: 0 });
  });

  it('still draws a loop, and reports it', () => {
    const layout = layoutGraph([g('a', 'b'), g('b', 'a'), g('c', 'b')]);
    expect(layout.cycle).toEqual(['a', 'b', 'a']);
    expect(layout.nodes).toHaveLength(3);
  });

  it('draws a task that waits for an unknown id or a blank id without an edge', () => {
    const layout = layoutGraph([g('a', 'ghost'), g('')]);
    expect(layout.edges).toEqual([]);
    expect(layout.nodes).toHaveLength(2);
  });
});

describe('the budget estimate', () => {
  const rules = DEFAULT_RULES;
  const here = ['/srv/work/payments-api', 'example/payments-api'];

  it('uses the task’s own budget first', () => {
    expect(estimateTask({ size: 'S', budget: '2.5' }, learn(), rules, here)).toMatchObject({
      usd: 2.5,
      source: 'task',
    });
  });

  it('uses the repository’s p80 once it has enough past tasks, and says how many', () => {
    const estimate = estimateTask({ size: 'S', budget: '' }, learn(), rules, here);
    expect(estimate).toMatchObject({ usd: 0.35, source: 'repository', n: 24, size: 'S' });
    expect(estimate.label).toBe('p80 of 24 past S tasks in this repository');
  });

  it('pools every repository when this one has too few, weighted by their task counts', () => {
    const estimate = estimateTask({ size: 'M', budget: '' }, learn(), rules, here);
    expect(estimate.source).toBe('all');
    expect(estimate.n).toBe(26);
    expect(estimate.repositories).toBe(2);
    expect(estimate.usd).toBeCloseTo((1.0 * 14 + 0.9 * 12) / 26, 6);
    expect(estimate.label).toBe('about p80 of 26 past M tasks in 2 repositories');
  });

  it('falls back to the flat budget when history is too thin, and says why', () => {
    const estimate = estimateTask({ size: 'L', budget: '' }, learn(), rules, here);
    expect(estimate).toMatchObject({ usd: 10, source: 'flat' });
    expect(estimate.label).toBe('the flat L budget; only 6 past L tasks, the server needs 20');
    const none = estimateTask(
      { size: 'S', budget: '' },
      { since: null, tasks: 0, groups: [] },
      rules,
      here,
    );
    expect(none.label).toBe('the flat S budget; no past S tasks, the server needs 20');
  });

  it('assumes M for a task with no size, and says so', () => {
    expect(estimateTask({ size: '', budget: '' }, learn(), rules, here)).toMatchObject({
      size: 'M',
      sizeAssumed: true,
    });
  });

  it('reads the median when the profile’s percentile is 50, and the profile’s minimum', () => {
    const estimate = estimateTask(
      { size: 'S', budget: '' },
      learn(),
      { ...rules, percentile: 50 },
      here,
    );
    expect(estimate.usd).toBeCloseTo(0.175, 6);
    const strict = estimateTask(
      { size: 'S', budget: '' },
      learn(),
      { ...rules, minTasks: 30 },
      here,
    );
    expect(strict.source).toBe('flat');
  });

  it('adds up a batch, and counts the chain and the tasks that can run at once', () => {
    const tasks = [
      task('a', { size: 'S' }),
      task('b', { size: 'S', dependsOn: ['a'] }),
      task('c', { size: 'L', budget: '2' }),
    ];
    const batch = estimateBatch({ tasks }, learn(), rules, here);
    expect(batch.total).toBeCloseTo(0.35 + 0.35 + 2, 6);
    expect(batch.chain).toBe(2);
    expect(batch.widest).toBe(2);
    expect(batch.historyMissing).toBe(false);
    expect(estimateBatch({ tasks }, undefined, rules, here)).toMatchObject({
      historyMissing: true,
      total: 1.5 + 1.5 + 2,
    });
  });

  it('falls back on the flat figure for every task when the history could not be read', () => {
    const estimate = estimateTask({ size: 'S', budget: '' }, undefined, rules, here);
    expect(estimate).toMatchObject({ usd: 1.5, source: 'flat' });
  });
});

describe('tasks from pasted text', () => {
  it('makes one task per line, drops bullets, and reads a size marker', () => {
    const { tasks, skipped, error } = parseImport(
      '- [S] Let intake watch a repository by alias\n2. (L) Report peak memory per container\n* L: third\n\nplain line',
    );
    expect(error).toBeUndefined();
    expect(skipped).toBe(0);
    expect(tasks.map((t) => [t.id, t.goal, t.size])).toEqual([
      ['let-intake-watch-a-repository-by-alias', 'Let intake watch a repository by alias', 'S'],
      ['report-peak-memory-per-container', 'Report peak memory per container', 'L'],
      ['third', 'third', 'L'],
      ['plain-line', 'plain line', 'M'],
    ]);
  });

  it('does not mistake a size marker at the start of a line for the start of a JSON list', () => {
    const { tasks, error } = parseImport('[S] first\n[M] second');
    expect(error).toBeUndefined();
    expect(tasks.map((t) => t.size)).toEqual(['S', 'M']);
  });

  it('reads a JSON list of goals, of task objects, or a request', () => {
    expect(parseImport('["fix a", "fix b"]').tasks.map((t) => t.goal)).toEqual(['fix a', 'fix b']);
    const objects = parseImport(
      '[{"id":"first","goal":"do it","size":"s","budget_usd":1.5,"depends_on":["x"],"max_turns":9}]',
    ).tasks[0];
    expect(objects).toMatchObject({
      id: 'first',
      goal: 'do it',
      size: 'S',
      budget: '1.5',
      dependsOn: ['x'],
      extra: { max_turns: 9 },
    });
    expect(parseImport('{"driven":{"tasks":[{"id":"q","goal":"g"}]}}').tasks[0]?.id).toBe('q');
    expect(parseImport('{"tasks":["t"]}').tasks).toHaveLength(1);
  });

  it('makes ids that avoid the ones already in the batch and each other', () => {
    const { tasks } = parseImport('fix login\nfix login', ['fix-login']);
    expect(tasks.map((t) => t.id)).toEqual(['fix-login-2', 'fix-login-3']);
  });

  it('keeps to the room left and counts the rest', () => {
    const text = Array.from({ length: 5 }, (_, i) => `task number ${i}`).join('\n');
    const { tasks, skipped } = parseImport(text, [], 3);
    expect(tasks).toHaveLength(3);
    expect(skipped).toBe(2);
  });

  it('says so when the text cannot be read', () => {
    expect(parseImport('["a", ').error).toMatch(/^This looks like JSON but does not parse/);
    expect(parseImport('{"x": 1}').error).toBe(
      'Expected a list of tasks, or an object with a tasks list.',
    );
    expect(parseImport('[1, null]').error).toBe('Nothing in that text looks like a task.');
    expect(parseImport('   ')).toEqual({ tasks: [], skipped: 0 });
  });
});

describe('editing the task list', () => {
  const list = () => [
    task('a'),
    task('b', { dependsOn: ['a'] }),
    task('c', { dependsOn: ['a', 'b'] }),
  ];

  it('renames a task and everything that waited for it', () => {
    const renamed = renameTask(list(), 0, 'first');
    expect(renamed.map((t) => t.id)).toEqual(['first', 'b', 'c']);
    expect(renamed.map((t) => t.dependsOn)).toEqual([[], ['first'], ['first', 'b']]);
  });

  it('leaves dependencies alone when the old id is shared or was empty', () => {
    const shared = [task('a'), task('a'), task('x', { dependsOn: ['a'] })];
    expect(renameTask(shared, 0, 'z')[2]?.dependsOn).toEqual(['a']);
    expect(renameTask([task(''), task('x', { dependsOn: [''] })], 0, 'q')[1]?.dependsOn).toEqual([
      '',
    ]);
  });

  it('removes a task and its id from what waited for it', () => {
    const rest = removeTask(list(), 0);
    expect(rest.map((t) => [t.id, t.dependsOn])).toEqual([
      ['b', []],
      ['c', ['b']],
    ]);
  });

  it('moves a task, and stays put at the ends', () => {
    expect(moveTask(list(), 2, -1).map((t) => t.id)).toEqual(['a', 'c', 'b']);
    expect(moveTask(list(), 0, 1).map((t) => t.id)).toEqual(['b', 'a', 'c']);
    expect(moveTask(list(), 0, -1).map((t) => t.id)).toEqual(['a', 'b', 'c']);
    expect(moveTask(list(), 2, 1).map((t) => t.id)).toEqual(['a', 'b', 'c']);
  });

  it('suggests an id no task uses, and slugs a goal', () => {
    expect(nextTaskId([task('task-1'), task('task-3')])).toBe('task-2');
    expect(nextTaskId([])).toBe('task-1');
    expect(slugify('Fix: the  Login -- redirect!', new Set())).toBe('fix-the-login-redirect');
    expect(slugify('!!!', new Set())).toBe('task');
    // Long goals are cut between words, at 40 characters at most.
    expect(slugify('Let intake watch a repository by an alias', new Set())).toBe(
      'let-intake-watch-a-repository-by-an',
    );
    expect(slugify('x'.repeat(70), new Set())).toBe('x'.repeat(40));
    expect(slugify('word '.repeat(20), new Set()).length).toBeLessThanOrEqual(40);
    expect(slugify('a', new Set(['a', 'a-2']))).toBe('a-3');
  });
});
