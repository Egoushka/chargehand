// Server answers for the tests and for a local look at the console: the shapes of docs/guide/server.md#console-routes with
// invented data (repositories, ids and people here are made up). Times are relative to `now`, so "in the last 24 hours" holds
// whenever a test runs.
import type { BatchBody, BatchTaskBody } from '../api/batch';
import type { AccountBody, PullRequestBody } from '../api/common';
import type { InboxBody, InboxItem } from '../api/inbox';
import type { OverviewBody } from '../api/overview';
import type { PullsBody } from '../api/pulls';
import type { SystemBody } from '../api/system';
import type { Me, RunSummary } from '../api/types';

export const LOGIN = 'chk1.fixture.secret';

export const REPO = '/srv/work/payments-api';
export const COMMIT = 'a91c0ffe21b7';
export const BATCH_A = 'run-20261013-124239-1fdf01';
export const BATCH_B = 'run-20261013-124402-77bd0a';

const iso = (now: number, minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();

const window = (now: number, usd: number, tokens: number) => ({
  usd,
  tokens,
  resets_at: new Date(Math.ceil(now / 86_400_000) * 86_400_000).toISOString(),
});

export function me(over: Partial<Me> = {}, now = Date.now()): Me {
  return {
    name: 'owner',
    owner: true,
    driven: true,
    repositories: null,
    limits: {},
    spend: { day: window(now, 26.14, 5_100_000), month: window(now, 311.2, 62_000_000) },
    unfinished_runs: 2,
    halted: false,
    presets: ['default', 'driven', 'draft'],
    ...over,
  };
}

const accountBody = (
  name: string,
  usdCap: number | null,
  usd: number,
  tokens: number,
  now: number,
): AccountBody => {
  const { halted: _h, presets: _p, ...standing } = me({ name, owner: name === 'owner' }, now);
  return {
    ...standing,
    limits: usdCap == null ? {} : { max_usd_per_day: usdCap },
    spend: { day: window(now, usd, tokens), month: window(now, usd * 11, tokens * 11) },
    credential: { mode: 'server', present: true },
    keys: [],
  };
};

export function overview(over: Partial<OverviewBody> = {}, now = Date.now()): OverviewBody {
  return {
    contract_version: 'overview/v1',
    generated_at: iso(now, 0),
    scope: 'owner',
    account: 'owner',
    server: {
      version: '0.9.1',
      halted: false,
      runs_held: 2,
      runs_queued: 0,
      runs_running: 2,
      max_unfinished: 10,
    },
    driven: {
      enabled: true,
      runner: { configured: true, reachable: true, elapsed_ms: 14 },
      host: {
        available_mb: 2253,
        total_mb: 32154,
        load1: 7.4,
        cpus: 16,
        free_after_reserve_mb: 1229,
        reserve_mb: 1024,
        max_load_per_cpu: 1.5,
      },
      sessions: { running: 5, max_parallel_total: 6, batches: 2 },
      rate_limit: { paused: false, pauses: [] },
    },
    standing: me({}, now),
    accounts: [
      accountBody('owner', null, 12.4, 2_300_000, now),
      accountBody('ci-bot', 10, 9.4, 1_900_000, now),
      accountBody('reviewer', 25, 4.34, 900_000, now),
    ],
    inbox: { questions: 1, pull_requests: 1, failures: 1, total: 3 },
    not_recorded: [],
    ...over,
  };
}

/** What an account (not the owner) gets: its own spend and nothing about the host or other accounts. */
export function accountOverview(now = Date.now()): OverviewBody {
  const base = overview({}, now);
  return {
    ...base,
    scope: 'account',
    account: 'ci-bot',
    driven: { ...base.driven, runner: null, host: null, sessions: { running: 1, batches: 1 } },
    standing: me({ name: 'ci-bot', owner: false, limits: { max_usd_per_day: 10 } }, now),
    accounts: null,
    inbox: { questions: 0, pull_requests: 0, failures: 0, total: 0 },
  };
}

const pr = (over: Partial<PullRequestBody>, now: number): PullRequestBody => ({
  url: 'https://github.com/example/payments-api/pull/412',
  number: 412,
  repository: 'example/payments-api',
  batch_run_id: BATCH_A,
  task_id: 't2-retry-policy',
  run_id: `${BATCH_A}-t2`,
  state: 'open',
  partial: false,
  test_integrity_flagged: false,
  integrator_mode: 'ask',
  awaiting_person: true,
  awaiting_reason: 'policy_ask',
  opened_at: iso(now, 35),
  ...over,
});

export function inboxItems(now = Date.now()): InboxItem[] {
  return [
    {
      kind: 'question',
      id: `question:${BATCH_A}:t3-memory-ttl`,
      at: iso(now, 20),
      actor: 'owner',
      batch_run_id: BATCH_A,
      task_id: 't3-memory-ttl',
      run_id: `${BATCH_A}-t3`,
      repository: REPO,
      title: 'Add a TTL to the memory cache',
      questions: [
        'Should the TTL come from the environment or from the container template?',
        'If the template, which size class should the default follow?',
      ],
      answer: {
        method: 'POST',
        path: '/v1/runs',
        continue_from: { run_id: BATCH_A, task_id: 't3-memory-ttl' },
        repository: { path: REPO, commit: COMMIT },
        preset: 'driven',
      },
    },
    {
      kind: 'pull_request',
      id: 'pull_request:https://github.com/example/payments-api/pull/412',
      at: iso(now, 35),
      actor: 'owner',
      batch_run_id: BATCH_A,
      task_id: 't2-retry-policy',
      run_id: `${BATCH_A}-t2`,
      repository: REPO,
      title: 'example/payments-api #412',
      questions: [],
      pull_request: pr({}, now),
      reason: 'policy_ask',
    },
    {
      kind: 'failure',
      id: `failure:${BATCH_B}:t1-pii-scrub`,
      at: iso(now, 55),
      actor: 'owner',
      batch_run_id: BATCH_B,
      task_id: 't1-pii-scrub',
      run_id: `${BATCH_B}-t1`,
      repository: REPO,
      title: 'Scrub personal data from the export job',
      questions: [],
      error: {
        code: 'verification_failed',
        message: '3 tests fail in a fresh container.',
        retryable: true,
        action: 'Open the verification artifact, then retry the task.',
      },
      reason: 'verification_failed',
      subject: 'task',
    },
  ];
}

/** A question with text that does not fit on a line, to see how long content lays out. */
export function longQuestion(now = Date.now()): InboxItem {
  const [first] = inboxItems(now);
  return {
    ...(first as InboxItem),
    id: 'question:long',
    task_id: 'ch-118-122-stability-guards-and-the-scheduler-memory-admission-test',
    repository: '/srv/work/a-repository-with-a-very-long-name-that-does-not-fit-in-a-badge',
    title:
      'The memory admission test passes on a 32 GiB host but the CI runner has 7 GiB: should the test read the limit from the environment, from the container template, or be skipped in CI with a visible reason?',
    questions: [
      'The memory admission test passes on a 32 GiB host but the CI runner has 7 GiB: should the test read the limit from the environment, from the container template, or be skipped in CI with a visible reason that names the host limit so nobody mistakes it for a pass?',
      'If the answer is the template, which of the three size classes should the default memory limit follow?',
    ],
  };
}

export function inbox(items: InboxItem[] = inboxItems(), now = Date.now()): InboxBody {
  const count = (kind: InboxItem['kind']) => items.filter((i) => i.kind === kind).length;
  return {
    contract_version: 'inbox/v1',
    generated_at: iso(now, 0),
    since: iso(now, 14 * 24 * 60),
    counts: {
      questions: count('question'),
      pull_requests: count('pull_request'),
      failures: count('failure'),
      total: items.length,
    },
    items,
    not_recorded: [],
    resolved: { superseded: 0, infrastructure: 0, continued: 0, dismissed: 0, duplicates: 0 },
  };
}

export const INFRA_REPO = '/srv/repos/example/infra-config';

/** The inbox as it looked with repeats (CHARGEHAND-237): three reviews of one repository failed with one code, folded into a group. */
export function groupedInbox(now = Date.now()): InboxBody {
  const reviews: InboxItem[] = [3, 2, 1].map((n) => ({
    kind: 'failure',
    id: `failure:run-review-${n}`,
    at: iso(now, 60 * (4 - n)),
    actor: 'owner',
    run_id: `run-review-${n}`,
    repository: INFRA_REPO,
    title: `Review pull request example/infra-config#${40 + n}.`,
    questions: [],
    error: {
      code: 'checkout_has_secrets',
      message:
        'the repository tracks files the preset denies reading, which grep would still reach',
      retryable: false,
      action: 'Use a preset that allows them, or stop tracking them.',
    },
    reason: 'checkout_has_secrets',
    subject: 'run',
    count: n === 3 ? 4 : 1,
  }));
  const body = inbox([...inboxItems(now), ...reviews], now);
  body.groups = [
    {
      id: 'group:checkout_has_secrets:failure:run-review-3',
      kind: 'failure',
      repository: INFRA_REPO,
      reason: 'checkout_has_secrets',
      count: 6,
      at: iso(now, 60),
      items: reviews.map((r) => r.id),
    },
  ];
  return body;
}

export function run(id: string, over: Partial<RunSummary> = {}, now = Date.now()): RunSummary {
  return {
    contract_version: 'run-summary/v1',
    run_id: id,
    preset: 'driven',
    status: 'running',
    started_at: iso(now, 42),
    usd: null,
    tokens: 1_480_000,
    repository: REPO,
    commit: COMMIT,
    kind: 'batch',
    actor: 'owner',
    ...over,
  };
}

const task = (
  id: string,
  state: BatchTaskBody['state'],
  over: Partial<BatchTaskBody> = {},
): BatchTaskBody => ({
  id,
  goal_truncated: false,
  size: 'M',
  depends_on: [],
  state,
  test_integrity_flagged: false,
  sessions: [],
  questions: [],
  answered_by: [],
  spend: {
    usd_estimate: 0.9,
    usd_estimate_partial: false,
    input_tokens: 120_000,
    output_tokens: 40_000,
    cache_read_tokens: 0,
    cache_write_tokens: 0,
    continuations: 0,
  },
  ...over,
});

export function batch(id = BATCH_A, now = Date.now()): BatchBody {
  const tasks = [
    task('t1-schema', 'completed'),
    task('t2-retry-policy', 'completed'),
    task('t3-memory-ttl', 'running'),
    task('t4-metrics', 'running'),
    task('t5-docs', 'pending'),
    task('t6-cleanup', 'pending', { depends_on: ['t3-memory-ttl'] }),
  ];
  return {
    contract_version: 'batch/v1',
    run: run(id, {}, now),
    actor: 'owner',
    repository: { path: REPO, commit: COMMIT },
    limits: { max_parallel: 4, max_usd_total: 15 },
    tasks,
    timeline: { available: false, total: 0, events: [] },
    pull_requests: [],
    artifacts: [],
    rate_limits: [],
    not_recorded: [],
  };
}

export function pulls(now = Date.now()): PullsBody {
  return {
    contract_version: 'pulls/v1',
    generated_at: iso(now, 0),
    pulls: [
      pr(
        {
          number: 409,
          url: 'https://github.com/example/payments-api/pull/409',
          state: 'merged_unchanged',
          integrator_mode: 'merge',
          awaiting_person: false,
          awaiting_reason: null,
          task_id: 't0-lint',
          state_at: iso(now, 80),
        },
        now,
      ),
      pr(
        {
          number: 407,
          url: 'https://github.com/example/web-shop/pull/407',
          repository: 'example/web-shop',
          state: 'merged_edited',
          integrator_mode: 'merge',
          awaiting_person: false,
          awaiting_reason: null,
          task_id: 'shop-12-banner',
          state_at: iso(now, 200),
        },
        now,
      ),
      pr({}, now),
    ],
    not_recorded: [],
  };
}

/** The owner's system view, cut to what the console reads today: uptime and the runner. */
export function system(now = Date.now()): SystemBody {
  return {
    contract_version: 'system/v1',
    generated_at: iso(now, 0),
    version: '0.9.1',
    started_at: iso(now, 3 * 24 * 60 + 120),
    halted: false,
    unfinished: 2,
    presets: [],
    runner: {
      configured: true,
      reachable: true,
      container_count: 11,
      containers: [],
      networks: [],
      volumes: [],
    },
    images: [],
    not_recorded: [],
  } as unknown as SystemBody;
}
