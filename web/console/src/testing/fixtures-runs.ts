// Invented data for the Runs, Batch, Task and Research screens: a long run list, a batch whose tasks are in every state, a task with a diff,
// and a research result. Times are relative to `now`. Nothing here is a real repository, person or run.
import type { BatchBody, BatchTaskBody } from '../api/batch';
import type { BatchTaskDetailBody } from '../api/batch-task';
import type { PullRequestBody } from '../api/common';
import type { PullsBody } from '../api/pulls';
import type { RunResult, RunStatus, RunSummary } from '../api/types';
import { BATCH_A, COMMIT, REPO } from './fixtures';

const iso = (now: number, minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();

/** A deterministic mix of runs, newest first: batches and single runs, every state, three repositories, two accounts, three presets. */
export function runList(count: number, now = Date.now()): RunSummary[] {
  const repos = [REPO, '/srv/work/web-shop', '/srv/work/infra-notes'];
  const states: Array<Pick<RunSummary, 'status' | 'error_code'>> = [
    { status: 'running' },
    { status: 'completed' },
    { status: 'completed' },
    { status: 'failed', error_code: 'verification_failed' },
    { status: 'needs_input' },
    { status: 'completed' },
    { status: 'failed', error_code: 'cancelled' },
    { status: 'lost' },
    { status: 'completed' },
    { status: 'denied', error_code: 'repository_not_allowed' },
  ];
  return Array.from({ length: count }, (_, i) => {
    const state = states[i % states.length] ?? { status: 'completed' as const };
    const active = state.status === 'running';
    const started = 5 + i * 17;
    const id = `run-20261013-${String(100000 + i).slice(1)}-${(0xa10000 + i).toString(16)}`;
    return {
      contract_version: 'run-summary/v1',
      run_id: id,
      preset: ['driven', 'default', 'thorough'][i % 3] ?? 'driven',
      started_at: iso(now, started),
      finished_at: active ? null : iso(now, started - 6),
      usd: state.status === 'completed' && i % 2 === 0 ? 0.4 + (i % 7) / 10 : null,
      tokens: 120_000 + i * 3_100,
      branch:
        state.status === 'completed' && i % 3 === 0 ? `chargehand/${id.slice(4, 12)}-t1` : null,
      pr_url:
        state.status === 'completed' && i % 3 === 0
          ? `https://github.com/example/payments-api/pull/${400 + i}`
          : null,
      actor: i % 4 === 3 ? 'ci-bot' : 'owner',
      kind: i % 3 === 2 ? 'single' : 'batch',
      repository: repos[i % repos.length],
      commit: COMMIT,
      ...state,
    } satisfies RunSummary;
  });
}

const status = (
  now: number,
  minutesAgo: number,
  event: NonNullable<RunStatus['event']>,
  taskId: string | undefined,
  detail: string,
  extra: Partial<RunStatus> = {},
): RunStatus => ({
  contract_version: 'run-status/v1',
  run_id: BATCH_A,
  status: 'running',
  event,
  at: iso(now, minutesAgo),
  task_id: taskId,
  detail,
  ...extra,
});

/** The events of the batch, oldest first. */
export function batchEvents(now = Date.now()): RunStatus[] {
  return [
    status(now, 42, 'accepted', undefined, 'run accepted: 8 tasks, preset driven, max_parallel 4'),
    status(now, 41, 'container_started', 't1-schema', 'session container is up'),
    status(now, 41, 'container_started', 't2-retry-policy', 'session container is up'),
    status(now, 40, 'container_started', 't3-memory-ttl', 'session container is up'),
    status(now, 40, 'container_started', 't7-guards', 'session container is up'),
    status(now, 34, 'pushed', 't1-schema', 'branch pushed', { branch: 'chargehand/t1-schema' }),
    status(now, 33, 'pr_opened', 't1-schema', 'draft pull request opened /pull/411'),
    status(now, 33, 'integrated', 't1-schema', 'merged: required checks passed'),
    status(now, 33, 'task_finished', 't1-schema', 'completed'),
    status(now, 33, 'container_started', 't4-metrics', 'session container is up'),
    status(
      now,
      29,
      'governor',
      't2-retry-policy',
      'observe · warn · repeated_call · turn 9 · ≈ $0.31 of $2.05',
      {
        turns: 9,
      },
    ),
    status(
      now,
      26,
      'question',
      't3-memory-ttl',
      'task t3-memory-ttl: the session asked:\n- Should the TTL come from the environment or from the container template?\n- If the template, which size class should the default follow?',
    ),
    status(now, 26, 'task_finished', 't3-memory-ttl', 'needs_input'),
    status(
      now,
      24,
      'governor',
      't7-guards',
      'observe · checkpoint · context 171,882 tokens · turn 77 · ≈ $2.94 of $3.10 (p80 of 21 past L tasks)',
      {
        turns: 77,
      },
    ),
    status(
      now,
      22,
      'container_started',
      't2-retry-policy',
      'session container is up (continuation 1)',
    ),
    status(now, 18, 'pushed', 't2-retry-policy', 'branch pushed', {
      branch: 'chargehand/t2-retry-policy',
    }),
    status(now, 17, 'pr_opened', 't2-retry-policy', 'draft pull request opened /pull/412'),
    status(now, 17, 'task_finished', 't2-retry-policy', 'completed'),
    status(now, 14, 'task_finished', 't7-guards', 'rate_limited: usage limit reached', {
      status: 'failed',
    }),
    status(now, 2, 'session_progress', 't4-metrics', 'turn 22 · stage test', {
      turns: 22,
      tokens: 412_000,
      stage: 'test',
      usd_estimate: 0.96,
    }),
  ];
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
  integrator_last: 'ready: waiting for a person to merge (policy ask)',
  opened_at: iso(now, 17),
  ...over,
});

const task = (
  id: string,
  state: BatchTaskBody['state'],
  over: Partial<BatchTaskBody> = {},
): BatchTaskBody => ({
  id,
  goal: `Goal of ${id}`,
  goal_truncated: false,
  size: 'M',
  depends_on: [],
  state,
  test_integrity_flagged: false,
  sessions: [],
  questions: [],
  answered_by: [],
  ...over,
});

const spend = (usd: number, input: number, output: number, cacheRead = 0, continuations = 0) => ({
  usd_estimate: usd,
  usd_estimate_partial: false,
  input_tokens: input,
  output_tokens: output,
  cache_read_tokens: cacheRead,
  cache_write_tokens: Math.round(cacheRead / 20),
  turns: 12 + continuations * 6,
  continuations,
});

/** A batch whose tasks are in every state, with a dependency graph, a continuation, a rate-limit pause, a governor record and pull requests. */
export function richBatch(id = BATCH_A, now = Date.now()): BatchBody {
  const merged = pr(
    {
      number: 411,
      url: 'https://github.com/example/payments-api/pull/411',
      task_id: 't1-schema',
      state: 'merged_unchanged',
      integrator_mode: 'merge',
      awaiting_person: false,
      awaiting_reason: null,
      integrator_last: 'merged: required checks passed',
    },
    now,
  );
  const open = pr({}, now);
  const tasks: BatchTaskBody[] = [
    task('t1-schema', 'completed', {
      goal: 'Add the retry columns to the payments schema and a migration for them',
      size: 'S',
      size_from: 'plan',
      started_at: iso(now, 41),
      finished_at: iso(now, 33),
      run_id: `${BATCH_A}-t1`,
      branch: 'chargehand/t1-schema',
      pr_url: merged.url,
      budget: { usd: 0.8, source: { kind: 'history', n: 21, percentile: 80, scope: 'repository' } },
      spend: spend(0.42, 90_000, 21_000, 400_000),
      governor: { mode: 'observe', decisions: [] },
      sessions: [{ run_id: `${BATCH_A}-t1`, role: 'first' }],
      pull_request: merged,
    }),
    task('t2-retry-policy', 'completed', {
      goal: 'Retry failed payment calls with capped exponential backoff and a jitter',
      depends_on: ['t1-schema'],
      started_at: iso(now, 41),
      finished_at: iso(now, 17),
      run_id: `${BATCH_A}-t2`,
      branch: 'chargehand/t2-retry-policy',
      pr_url: open.url,
      budget: {
        usd: 2.05,
        source: { kind: 'history', n: 21, percentile: 80, scope: 'repository' },
      },
      spend: spend(1.12, 220_000, 64_000, 1_900_000, 1),
      governor: {
        mode: 'observe',
        decisions: [
          { signal: 'repeated_call', action: 'warn', count: 2, acted: 0 },
          { signal: 'context', action: 'checkpoint', count: 1, acted: 0 },
        ],
      },
      sessions: [
        { run_id: `${BATCH_A}-t2a`, role: 'first' },
        { run_id: `${BATCH_A}-t2`, role: 'continuation' },
      ],
      pull_request: open,
    }),
    task('t3-memory-ttl', 'needs_input', {
      goal: 'Add a TTL to the memory cache so stale entries drop out',
      started_at: iso(now, 40),
      finished_at: iso(now, 26),
      run_id: `${BATCH_A}-t3`,
      budget: { usd: 2.05, source: { kind: 'task' } },
      spend: spend(0.9, 120_000, 40_000),
      sessions: [{ run_id: `${BATCH_A}-t3`, role: 'first' }],
      questions: [
        'Should the TTL come from the environment or from the container template?',
        'If the template, which size class should the default follow?',
      ],
      answered: false,
      answer: {
        method: 'POST',
        path: '/v1/runs',
        continue_from: { run_id: BATCH_A, task_id: 't3-memory-ttl' },
        repository: { path: REPO, commit: COMMIT },
        preset: 'driven',
      },
    }),
    task('t4-metrics', 'running', {
      goal: 'Emit retry counters and a latency histogram from the payment client',
      depends_on: ['t1-schema'],
      phase: 'started',
      started_at: iso(now, 33),
      container: 'session-t4',
      budget: {
        usd: 2.05,
        source: { kind: 'history', n: 21, percentile: 80, scope: 'repository' },
      },
      spend: spend(0.96, 180_000, 52_000, 1_100_000),
      governor: { mode: 'observe', decisions: [] },
      live: {
        at: iso(now, 0),
        stage: 'test',
        turns: 22,
        tokens: 412_000,
        usd_estimate: 0.96,
        usd_estimate_partial: false,
      },
      sessions: [{ run_id: `${BATCH_A}-t4`, role: 'first' }],
    }),
    task('t5-docs', 'pending', {
      goal: 'Document the retry policy and its settings in the payments guide',
      size: 'S',
      depends_on: ['t4-metrics'],
      budget: { usd: 0.8, source: { kind: 'flat' } },
    }),
    task('t6-cleanup', 'pending', {
      goal: 'Remove the old fixed-delay retry helper once nothing calls it',
      size: 'S',
      depends_on: ['t3-memory-ttl'],
      budget: { usd: 0.8, source: { kind: 'flat' } },
    }),
    task('t7-guards', 'failed', {
      goal: 'Stability guards for the runner and the scheduler: admission, orphan sweep, a cap per runner',
      size: 'L',
      depends_on: ['t1-schema'],
      started_at: iso(now, 40),
      finished_at: iso(now, 14),
      run_id: `${BATCH_A}-t7`,
      branch: 'chargehand/t7-guards',
      error: {
        code: 'rate_limited',
        message: 'The provider usage limit was reached',
        retryable: true,
        action: 'Retry after the limit resets.',
      },
      budget: { usd: 3.1, source: { kind: 'history', n: 21, percentile: 80, scope: 'repository' } },
      spend: spend(2.94, 640_000, 180_000, 5_400_000),
      governor: {
        mode: 'observe',
        stop_reason: 'checkpoint_context',
        decisions: [{ signal: 'checkpoint_context', action: 'checkpoint', count: 1, acted: 0 }],
      },
      sessions: [{ run_id: `${BATCH_A}-t7`, role: 'first' }],
    }),
    task('t8-announce', 'not_started', {
      goal: 'Announce the guards in the changelog',
      size: 'S',
      depends_on: ['t7-guards'],
    }),
  ];
  return {
    contract_version: 'batch/v1',
    run: {
      contract_version: 'run-summary/v1',
      run_id: id,
      preset: 'driven',
      status: 'running',
      started_at: iso(now, 42),
      usd: null,
      tokens: 2_900_000,
      repository: REPO,
      commit: COMMIT,
      kind: 'batch',
      actor: 'owner',
    },
    actor: 'owner',
    repository: { path: REPO, commit: COMMIT },
    limits: { max_parallel: 4, max_tokens_total: 4_000_000, max_usd_total: 15, plan: true },
    credential_delivery: 'gateway',
    tasks,
    timeline: { available: true, total: batchEvents(now).length, events: batchEvents(now) },
    pull_requests: [merged, open],
    artifacts: [{ kind: 'driven-batch', media_type: 'application/json', sha256: 'a'.repeat(64) }],
    rate_limits: [
      {
        batch_run_id: id,
        task_id: 't7-guards',
        observed_at: iso(now, 14),
        resets_at: iso(now, -9),
      },
    ],
    not_recorded: [
      'Events (the timeline) are kept in memory: for a finished batch only the last 25 finished runs of this process are held, and a restart drops them.',
      "Only a task's last session has a run record; earlier sessions of a finished task are counted (continuations) but not listed.",
      "Governor decisions are kept as counts per signal and rung; each decision's detail is in the live events and in the session's output volume, which the runner does not serve.",
      "The session transcript is not served: the runner reads only the files of ADR 0039's list from a session's output volume, and the stream is not on it.",
      "A pull request's CI checks and the CI fixer's attempts are seen only while the integrator's step runs; they are not stored.",
    ],
  };
}

/** The same batch once it has finished: a result, and no live events. */
export function finishedBatch(id = BATCH_A, now = Date.now()): BatchBody {
  const batch = richBatch(id, now);
  return {
    ...batch,
    run: {
      ...batch.run,
      status: 'failed',
      error_code: 'tasks_incomplete',
      finished_at: iso(now, 3),
      usd: 6.1,
    },
    tasks: batch.tasks.map((t) =>
      t.state === 'running'
        ? { ...t, state: 'completed' as const, live: null, finished_at: iso(now, 3) }
        : t,
    ),
    timeline: { available: false, total: 0, events: [] },
    result: {
      status: 'failed',
      summary: '3 of 8 tasks did not produce a pull request',
      error: {
        code: 'tasks_incomplete',
        message: '3 of 8 tasks did not produce a pull request',
        retryable: false,
        action: 'Send the tasks that did not finish again.',
      },
      input_tokens: 1_700_000,
      output_tokens: 400_000,
      cache_read_tokens: 9_000_000,
      cache_write_tokens: 400_000,
      usd: null,
    },
    plan: { tasks: [{ id: 't1-schema', size: 'S' }] },
  };
}

export const DIFF = `diff --git a/src/Payments/RetryPolicy.cs b/src/Payments/RetryPolicy.cs
index 3f1c2aa..91b7e04 100644
--- a/src/Payments/RetryPolicy.cs
+++ b/src/Payments/RetryPolicy.cs
@@ -10,7 +10,11 @@ public sealed class RetryPolicy
 {
     private readonly int max;

-    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(attempt);
+    public TimeSpan Delay(int attempt) =>
+        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
+
+    public bool ShouldRetry(int attempt) => attempt < max;
 }
diff --git a/docs/guide/retries.md b/docs/guide/retries.md
index 5a0e111..6b2f9ce 100644
--- a/docs/guide/retries.md
+++ b/docs/guide/retries.md
@@ -3,3 +3,4 @@
 A failed payment call is retried.
+The delay doubles each time, up to 30 seconds.
 See the settings below.
diff --git a/tests/Payments/RetryPolicyTests.cs b/tests/Payments/RetryPolicyTests.cs
new file mode 100644
index 0000000..8d5c2e1
--- /dev/null
+++ b/tests/Payments/RetryPolicyTests.cs
@@ -0,0 +1,5 @@
+public sealed class RetryPolicyTests
+{
+    [Fact]
+    public void Delay_doubles_and_caps_at_thirty_seconds() => Assert.Equal(TimeSpan.FromSeconds(30), new RetryPolicy().Delay(9));
+}
`;

/** One task's own view: the whole goal, the artifacts of its result, a diff, a verification run and this task's events. */
export function taskDetail(batchId: string, taskId: string, now = Date.now()): BatchTaskDetailBody {
  const batch = richBatch(batchId, now);
  const found = batch.tasks.find((t) => t.id === taskId) ?? batch.tasks[0];
  const body = found as BatchTaskBody;
  const finished = body.state === 'completed';
  return {
    contract_version: 'batch-task/v1',
    batch_run_id: batchId,
    actor: 'owner',
    repository: batch.repository,
    task: {
      ...body,
      goal: `${body.goal ?? ''}. The whole goal is shown here, not the first 500 characters of it.`,
    },
    summary: finished ? 'Delay doubles to a 30 second cap; ShouldRetry stops at max.' : null,
    result_error: body.error ?? null,
    artifacts: finished
      ? [
          {
            kind: 'branch',
            media_type: 'application/vnd.chargehand.branch+json',
            sha256: 'b'.repeat(64),
          },
          {
            kind: 'changes',
            media_type: 'application/vnd.chargehand.changes+json',
            sha256: 'c'.repeat(64),
          },
          { kind: 'verification', media_type: 'application/json', sha256: 'd'.repeat(64) },
          { kind: 'session-tests', media_type: 'application/json', sha256: 'e'.repeat(64) },
          {
            kind: 'governor-log',
            media_type: 'application/jsonl',
            sha256: 'f'.repeat(64),
            uri: 'volume://governor.jsonl',
          },
          {
            kind: 'pull-request',
            media_type: 'application/vnd.chargehand.pull-request+json',
            sha256: '1'.repeat(64),
          },
        ]
      : [],
    verification: finished
      ? {
          argv: ['dotnet', 'test'],
          source: 'detected',
          sandbox: 'container',
          network: 'none',
          exit_code: 0,
          passed: true,
          timed_out: false,
          attempts: 1,
          duration_seconds: 41.2,
          output_tail: 'Passed!  - Failed:     0, Passed:   412, Skipped:     0, Total:   412',
          changed_verification_paths: [],
          test_integrity: null,
        }
      : null,
    session_tests: finished
      ? { claimed_by: 'session', command: 'dotnet test', exit_code: 0 }
      : null,
    changes: finished
      ? {
          base: COMMIT,
          commit: '4be1c07a91c0',
          files_total: 3,
          files: [
            { path: 'src/Payments/RetryPolicy.cs', added: 4, removed: 1, binary: false },
            { path: 'docs/guide/retries.md', added: 1, removed: 0, binary: false },
            { path: 'tests/Payments/RetryPolicyTests.cs', added: 5, removed: 0, binary: false },
          ],
          diff: DIFF,
          diff_truncated: false,
        }
      : null,
    review: null,
    kept_work:
      body.state === 'failed'
        ? { files: ['src/Runner/AdmissionGate.cs'], note: 'kept for a continuation' }
        : null,
    timeline: {
      available: true,
      total: 3,
      events: batchEvents(now).filter((e) => e.task_id === taskId),
    },
    not_recorded: batch.not_recorded,
  };
}

/** The pull requests of the batch, as GET /v1/pulls lists them. */
export function batchPulls(now = Date.now()): PullsBody {
  const batch = richBatch(BATCH_A, now);
  return {
    contract_version: 'pulls/v1',
    generated_at: iso(now, 0),
    pulls: batch.pull_requests,
    not_recorded: [],
  };
}

/** A finished research run: claims with evidence at a commit, open questions, a signature. */
export function researchResult(): RunResult {
  return {
    contract_version: 'result/v1',
    task_id: 'research-1',
    node_id: 'n1',
    trace_id: '0123456789abcdef0123456789abcdef',
    status: 'completed',
    summary:
      'Payments retry through RetryPolicy: the delay doubles per attempt and stops at 30 seconds; the number of attempts comes from the settings.',
    claims: [
      {
        text: 'RetryPolicy.Delay doubles the wait on each attempt and caps it at 30 seconds.',
        evidence: ['e1'],
        confidence: 0.94,
        support: 'supported',
      },
      {
        text: 'The maximum number of attempts is read from PaymentsOptions and defaults to 5.',
        evidence: ['e2', 'e3'],
        confidence: 0.81,
        support: 'partial',
      },
      {
        text: 'A retry is logged at information level with the attempt number.',
        evidence: ['e4'],
        confidence: 0.55,
        support: 'unchecked',
      },
    ],
    evidence: [
      { id: 'e1', kind: 'file', locator: 'src/Payments/RetryPolicy.cs:12-18', commit: COMMIT },
      { id: 'e2', kind: 'file', locator: 'src/Payments/PaymentsOptions.cs:31', commit: COMMIT },
      { id: 'e3', kind: 'file', locator: 'docs/guide/retries.md:3', commit: COMMIT },
      { id: 'e4', kind: 'url', locator: 'https://example.com/notes/retry-logging' },
    ],
    open_questions: [
      'Does the jitter in the design note exist in the code? No claim cites it.',
      'Which callers bypass RetryPolicy and retry on their own?',
    ],
    confidence: 0.77,
    usage: { input: 48_000, output: 6_200, cache_read: 210_000, cache_write: 9_000, usd: 0.31 },
    actor: 'owner',
    signature: {
      alg: 'ES256',
      key_id: '0123456789abcdef',
      value: 'MEUCIQDk3q8nTjQ0m1zYc9Zr5f_wX2vH7pLsA6bN4uE0gQIgY1',
    },
  };
}

/** A single run for the Research screen. */
export function singleRun(
  id: string,
  now = Date.now(),
  over: Partial<RunSummary> = {},
): RunSummary {
  return {
    contract_version: 'run-summary/v1',
    run_id: id,
    preset: 'thorough',
    status: 'completed',
    started_at: iso(now, 9),
    finished_at: iso(now, 7),
    usd: 0.31,
    tokens: 54_200,
    kind: 'single',
    actor: 'owner',
    repository: REPO,
    commit: COMMIT,
    ...over,
  };
}
