// Server answers for the owner-side screens (intake, repositories, accounts, system): the shapes of docs/guide/server.md#console-routes
// with invented data. Times are relative to `now`, so "in the last 24 hours" holds whenever a test runs.
import type { AccountsBody } from '../api/accounts';
import type { AnalyticsBody, SeriesPoint } from '../api/analytics';
import type { AccountBody } from '../api/common';
import type { IntakeBody, IntakeTaskBody } from '../api/intake';
import type { RepositoriesBody, RepositoryBody } from '../api/repositories';
import type { ContainerBody, SystemBody } from '../api/system';
import { BATCH_A, BATCH_B, me, REPO, system } from './fixtures';

const iso = (now: number, minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();
const DAY = 24 * 60;

const task = (over: Partial<IntakeTaskBody>, now: number, minutesAgo: number): IntakeTaskBody => ({
  repository: 'example/payments-api',
  issue: 231,
  batch_run_id: BATCH_A,
  task_id: 'issue-231',
  started: iso(now, minutesAgo),
  size: 'M',
  phase: 'running',
  awaiting_answer: false,
  ...over,
});

export function intake(over: Partial<IntakeBody> = {}, now = Date.now()): IntakeBody {
  return {
    contract_version: 'intake/v1',
    generated_at: iso(now, 0),
    enabled: true,
    state_present: true,
    state_saved_at: iso(now, 2),
    watched: [
      { repository: 'example/payments-api', path: REPO },
      { repository: 'example/web-shop', path: '/srv/work/web-shop' },
      { repository: 'example/infra', path: '/srv/work/infra' },
    ],
    allowed_authors: ['octocat'],
    label: 'chargehand',
    mention: '@chargehand',
    poll_seconds: 120,
    caps: {
      max_concurrent: 2,
      max_starts_per_day: 10,
      max_review_rounds: 3,
      max_tokens_total: 2_000_000,
      max_usd_total: 5,
      budget_class: 'M',
    },
    starts: { last24h: 7, cap: 10, remaining: 3, next_slot_at: null },
    running: 1,
    tasks: [
      task({}, now, 15),
      task(
        {
          issue: 230,
          batch_run_id: BATCH_B,
          task_id: 'issue-230',
          phase: 'asked',
          awaiting_answer: true,
          repository: 'example/web-shop',
        },
        now,
        90,
      ),
      task(
        {
          issue: 229,
          batch_run_id: 'run-20261012-101500-aa11bb',
          task_id: 'issue-229',
          phase: 'done',
          pr_url: 'https://github.com/example/payments-api/pull/412',
        },
        now,
        DAY + 30,
      ),
      task(
        {
          issue: 412,
          batch_run_id: 'run-20261012-111500-cc22dd',
          task_id: 'pr-412',
          phase: 'done',
          round: 2,
          pr_url: 'https://github.com/example/payments-api/pull/412',
        },
        now,
        DAY + 400,
      ),
      task(
        {
          issue: 227,
          batch_run_id: 'run-20261010-091500-ee33ff',
          task_id: 'issue-227',
          phase: 'done',
        },
        now,
        3 * DAY,
      ),
    ],
    pending_reviews: [
      {
        id: 'c4412',
        repository: 'example/payments-api',
        pull: 412,
        author: 'octocat',
        path: 'src/Retry.cs',
        line: 41,
        at: iso(now, 8),
      },
      {
        id: 'c4413',
        repository: 'example/payments-api',
        pull: 412,
        author: 'octocat',
        path: null,
        line: null,
        at: iso(now, 5),
      },
    ],
    handled: { total: 48, labels: 20, comments: 18, reviews: 7, rounds: 3 },
    notices: [
      { reason: 'cap', trigger: 'label:example/web-shop#232:9981' },
      { reason: 'wait', trigger: 'comment:example/payments-api:44102' },
      { reason: 'author', trigger: 'label:example/infra#17:7710' },
      { reason: 'rounds', trigger: 'example/payments-api#409' },
    ],
    comment_cursors: { 'example/payments-api': iso(now, 3) },
    not_recorded: [
      'A trigger from an author who is not allowed is marked handled and ignored without a reason; only the cases intake comments on (see notices) carry one.',
      'Handled keys and notices are not timestamped, so they are not ordered.',
      'The state file is the poller’s last save: a change since then is not visible, and the read does not wait for a poll.',
    ],
    ...over,
  };
}

/** Intake with no `automation.github` in the profile. */
export const intakeOff = (now = Date.now()): IntakeBody =>
  intake(
    {
      enabled: false,
      state_present: false,
      state_saved_at: null,
      watched: [],
      allowed_authors: [],
      label: null,
      mention: null,
      poll_seconds: null,
      caps: null,
      starts: null,
      running: 0,
      tasks: [],
      pending_reviews: [],
      handled: { total: 0, labels: 0, comments: 0, reviews: 0, rounds: 0 },
      notices: [],
      comment_cursors: {},
    },
    now,
  );

const repo = (over: Partial<RepositoryBody>, now: number): RepositoryBody => ({
  name: 'payments-api',
  path: REPO,
  root: '/srv/work',
  remote: 'https://github.com/example/payments-api.git',
  repository: 'example/payments-api',
  default_branch: 'main',
  head: 'a91c0ffe21b7d4c0be4f10aa5e7c3b9d2f80e617',
  error: null,
  ecosystems: [{ name: 'dotnet', markers: ['Payments.sln', 'global.json'] }],
  verify: { command: ['dotnet', 'test'], source: 'Payments.sln' },
  verify_candidates: [{ command: ['dotnet', 'test'], source: 'Payments.sln' }],
  integrator_mode: 'merge',
  watched_by_intake: true,
  open_pull_requests: [
    {
      url: 'https://github.com/example/payments-api/pull/412',
      number: 412,
      repository: 'example/payments-api',
      batch_run_id: BATCH_A,
      task_id: 't2-retry-policy',
      state: 'open',
      partial: false,
      test_integrity_flagged: false,
      integrator_mode: 'ask',
      awaiting_person: true,
      awaiting_reason: 'policy_ask',
      opened_at: iso(now, 35),
    },
    {
      url: 'https://github.com/example/payments-api/pull/413',
      number: 413,
      repository: 'example/payments-api',
      batch_run_id: BATCH_B,
      task_id: 't1-docs',
      state: 'open',
      partial: false,
      test_integrity_flagged: false,
      integrator_mode: 'merge',
      awaiting_person: false,
      opened_at: iso(now, 20),
    },
  ],
  ...over,
});

export function repositories(now = Date.now()): RepositoriesBody {
  return {
    contract_version: 'repositories/v1',
    generated_at: iso(now, 0),
    roots: [{ path: '/srv/work', exists: true }],
    repositories: [
      repo({}, now),
      repo(
        {
          name: 'web-shop',
          path: '/srv/work/web-shop',
          remote: 'https://github.com/example/web-shop.git',
          repository: 'example/web-shop',
          head: '5d9c1e4b83f7a0c5e921d4a6c18f3b27a9e0d514',
          ecosystems: [
            { name: 'node', markers: ['package.json'] },
            { name: 'python', markers: ['pyproject.toml'] },
          ],
          verify: { command: ['npm', 'test'], source: 'package.json' },
          verify_candidates: [
            { command: ['npm', 'test'], source: 'package.json' },
            { command: ['pytest'], source: 'pyproject.toml' },
          ],
          integrator_mode: 'ask',
          open_pull_requests: [],
        },
        now,
      ),
      repo(
        {
          name: 'infra',
          path: '/srv/work/infra',
          remote: null,
          repository: null,
          default_branch: null,
          head: null,
          error: 'git could not read HEAD',
          ecosystems: [],
          verify: null,
          verify_candidates: [],
          integrator_mode: 'off',
          watched_by_intake: false,
          open_pull_requests: [],
        },
        now,
      ),
    ],
    not_recorded: [
      'Open pull requests are the ones chargehand opened and has a record of, with the state last read back from the forge; the server does not list a forge’s pull requests itself.',
      'Detection reads marker files only (RepositoryDetector); the test command is a proposal, not what a run used.',
      'HEAD and the default branch are what the checkout has now; the server fetches before a batch, not for this route.',
    ],
  };
}

const point = (
  group: string,
  weeksAgo: number,
  tasks: number,
  completed: number,
  now: number,
): SeriesPoint => ({
  bucket_start: iso(now, weeksAgo * 7 * DAY),
  group,
  tasks,
  completed,
  partial: 0,
  failed: tasks - completed,
  needs_input: 0,
  pass_rate: tasks ? completed / tasks : null,
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
  prs: { total: 0, merged_unchanged: 0, merged_edited: 0, merged: 0, closed: 0, open: 0 },
});

/** `GET /v1/analytics?group_by=repository&bucket=week`, cut to what the Repositories screen reads. */
export function repositoryHistory(now = Date.now()): AnalyticsBody {
  const points = [
    point(REPO, 4, 3, 2, now),
    point(REPO, 3, 4, 3, now),
    point(REPO, 2, 5, 4, now),
    point(REPO, 1, 4, 4, now),
    point(REPO, 0, 2, 1, now),
    point('example/web-shop', 1, 2, 1, now),
  ];
  return {
    contract_version: 'analytics/v1',
    generated_at: iso(now, 0),
    since: iso(now, 70 * DAY),
    bucket: 'week',
    group_by: 'repository',
    tasks: {
      since: iso(now, 70 * DAY),
      bucket: 'week',
      group_by: 'repository',
      tasks: points.reduce((sum, p) => sum + p.tasks, 0),
      points,
      total: point('', 0, 0, 0, now),
      governor: { decisions: [], modes: [] },
      continuations: { tasks_with_continuation: 0, continuations: 0 },
    },
    runs: [],
    not_recorded: [],
  };
}

const account = (
  name: string,
  over: Partial<AccountBody>,
  usd: [day: number, month: number],
  now: number,
): AccountBody => {
  const { halted: _h, presets: _p, ...standing } = me({ name, owner: false }, now);
  return {
    ...standing,
    spend: {
      day: { ...standing.spend.day, usd: usd[0], tokens: Math.round(usd[0] * 200_000) },
      month: { ...standing.spend.month, usd: usd[1], tokens: Math.round(usd[1] * 200_000) },
    },
    credential: { mode: 'server', present: null },
    keys: [],
    ...over,
  };
};

export function accounts(now = Date.now()): AccountsBody {
  return {
    contract_version: 'accounts/v1',
    generated_at: iso(now, 0),
    accounts: [
      account(
        'owner',
        {
          owner: true,
          unfinished_runs: 1,
          limits: {},
          credential: { mode: 'server', present: null },
        },
        [12.4, 231.5],
        now,
      ),
      account(
        'ci-bot',
        {
          repositories: [REPO, '/srv/work/web-shop'],
          unfinished_runs: 2,
          limits: {
            max_usd_per_day: 10,
            max_usd_per_month: 100,
            max_tokens_per_day: 8_000_000,
            max_concurrent_runs: 3,
          },
          credential: { mode: 'own_key', present: true },
          keys: [
            { id: 'k7f3a1', label: 'ci pipeline', created: iso(now, 40 * DAY) },
            {
              id: 'k2b9c4',
              label: 'old runner',
              created: iso(now, 90 * DAY),
              revoked: iso(now, 10 * DAY),
            },
          ],
        },
        [9.4, 61.2],
        now,
      ),
      account(
        'reviewer',
        {
          driven: false,
          repositories: [REPO],
          unfinished_runs: 0,
          limits: { max_usd_per_day: 4, max_concurrent_runs: 1 },
          credential: { mode: 'server', present: null },
          keys: [{ id: 'k5d1e8', label: null, created: iso(now, 12 * DAY) }],
        },
        [0.6, 14.8],
        now,
      ),
      account(
        'gateway-user',
        {
          unfinished_runs: 0,
          limits: { max_usd_per_month: 50 },
          credential: { mode: 'own_gateway', present: false },
          keys: [],
        },
        [0, 3.1],
        now,
      ),
      account(
        'unfunded',
        { unfinished_runs: 0, limits: {}, credential: { mode: 'none', present: null } },
        [0, 0],
        now,
      ),
    ],
    not_recorded: [
      'A key’s last use is not recorded.',
      '‘present’ says the secret item resolves, not that the credential works; chargehand doctor and a run find that out.',
    ],
  };
}

const container = (
  name: string,
  kind: ContainerBody['kind'],
  extra: Partial<ContainerBody> = {},
): ContainerBody => ({ name, kind, ...extra });

/** The owner's system view: the redacted profile, presets, a runner with live containers and pinned images. */
export function systemFull(now = Date.now()): SystemBody {
  const base = system(now);
  return {
    ...base,
    unfinished: 2,
    presets: [
      {
        name: 'default',
        version: '1.4.0',
        allowed_actions: ['read', 'search'],
        driven: false,
        approval: false,
      },
      {
        name: 'driven',
        version: '2.1.0',
        allowed_actions: ['read', 'write', 'test'],
        driven: true,
        approval: false,
      },
      {
        name: 'review',
        version: '1.0.2',
        allowed_actions: ['read'],
        driven: false,
        approval: true,
      },
      {
        name: 'draft',
        version: null,
        allowed_actions: [],
        driven: false,
        approval: false,
        error: 'preset file is not valid YAML',
      },
    ],
    runner: {
      configured: true,
      host: 'http://runner-a.example.test:4301',
      reachable: true,
      elapsed_ms: 38,
      container_count: 11,
      containers: [
        container('chargehand-run-20261013-124239-1fdf01-t3', 'session', {
          run_id: 'run-20261013-124239-1fdf01-t3',
          batch_run_id: BATCH_A,
          task_id: 't3-memory-ttl',
          actor: 'owner',
        }),
        container('chargehand-run-20261013-124239-1fdf01-t4', 'session', {
          run_id: 'run-20261013-124239-1fdf01-t4',
          batch_run_id: BATCH_A,
          task_id: 't4-metrics',
          actor: 'owner',
        }),
        container('chargehand-run-20261013-124402-77bd0a-t1', 'session', {
          run_id: 'run-20261013-124402-77bd0a-t1',
          batch_run_id: BATCH_B,
          task_id: 't1-docs',
          actor: 'ci-bot',
        }),
        container('chargehand-egress-run-20261013-124239-1fdf01', 'egress', {
          batch_run_id: BATCH_A,
          actor: 'owner',
        }),
        container('chargehand-egress-run-20261013-124402-77bd0a', 'egress', {
          batch_run_id: BATCH_B,
          actor: 'ci-bot',
        }),
        container('chargehand-svc-postgres-run-20261013-124239-1fdf01', 'service', {
          batch_run_id: BATCH_A,
          actor: 'owner',
        }),
        container('chargehand-prep-run-20261013-124402-77bd0a-t1', 'workspace', {
          run_id: 'run-20261013-124402-77bd0a-t1',
          batch_run_id: BATCH_B,
        }),
        container('chargehand-stray', 'other'),
      ],
      networks: ['chargehand-net-1fdf01'],
      volumes: ['chargehand-vol-1fdf01'],
      host_snapshot: {
        available_mb: 14336,
        total_mb: 32768,
        load1: 2.4,
        cpus: 8,
        free_after_reserve_mb: 10240,
        reserve_mb: 4096,
        max_load_per_cpu: 0.9,
      },
    },
    images: [
      {
        reference:
          'registry.example.test/chargehand/session:1.9@sha256:6f3a1c9d0b7e4a52c8d91e0f3b5a7c2d4e6f8091a2b3c4d5e6f708192a3b4c5d',
        name: 'registry.example.test/chargehand/session:1.9',
        digest: 'sha256:6f3a1c9d0b7e4a52c8d91e0f3b5a7c2d4e6f8091a2b3c4d5e6f708192a3b4c5d',
        pinned: true,
      },
      {
        reference: 'docker.io/library/postgres:16',
        name: 'docker.io/library/postgres:16',
        digest: null,
        pinned: false,
      },
    ],
    profile: {
      schema: 'profile/v1',
      runtime: 'claude_code',
      default_preset: 'default',
      worker_root: '/srv/chargehand/work',
      repository_roots: ['/srv/work'],
      run_cap_usd: 5,
      max_parallel_nodes: 4,
      support_check: false,
      signing: false,
      telemetry: false,
      memory: [],
      mcp_servers: [],
      models: { default: 'provider/model-large', review: 'provider/model-small' },
      http: {
        port: 4300,
        listen: '127.0.0.1',
        allowed_hosts: ['127.0.0.1'],
        max_concurrent_runs: 1,
        ui: true,
        api_key_set: true,
        accounts: ['ci-bot', 'reviewer'],
      },
      claude_code: { version: '2.1', binary: 'claude', credential: 'api_key', gateway: false },
      driven: {
        enabled: true,
        max_parallel: 3,
        max_parallel_total: 6,
        max_session_calls: 2,
        images: ['registry.example.test/chargehand/session:1.9'],
        network_allow: ['api.github.com'],
        runners: [
          {
            name: 'local',
            host: 'http://runner-a.example.test:4301',
            max_sessions: 4,
            platform: 'linux/amd64',
            weight: 1,
          },
          {
            name: 'second',
            host: 'http://runner-b.example.test:4301',
            max_sessions: 2,
            platform: 'linux/arm64',
            weight: 0.5,
          },
        ],
        push_secret_set: true,
        governor: { mode: 'observe' },
        integrator: {
          app_configured: true,
          repositories: { 'example/payments-api': 'merge', 'example/web-shop': 'ask' },
          always_ask: ['example/infra'],
          ci_fix: { attempts: 3, turns: 20, minutes: 15, wait: 20 },
        },
        service_repositories: [],
        task_source_set: false,
      },
      automation: {
        repositories: ['example/payments-api', 'example/web-shop', 'example/infra'],
        allowed_authors: 1,
        label: 'chargehand',
        mention: '@chargehand',
        poll_seconds: 120,
        max_concurrent: 2,
        max_starts_per_day: 10,
        max_review_rounds: 3,
      },
    },
    not_recorded: [
      'The runner lists container, network and volume names only: their start time, state and memory use are not served.',
      'A container is tied to a batch and task by the saved state of unfinished batches; one the server no longer holds state for shows without them.',
      'Presets list what the file says; whether a change to one passed Prompt CI is not recorded here.',
    ],
  };
}
