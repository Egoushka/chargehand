// What Start a batch reads besides the account: the checkouts (`repositories/v1`) and the learning report (`/v1/learn`). Invented
// data in the shapes of docs/guide/server.md; the paths and names are made up.
import type { RepositoriesBody, RepositoryBody } from '../api/repositories';
import type { LearnGroup, LearnReport } from '../api/types';
import { REPO } from './fixtures';

/** The head of the first checkout; its first 12 digits are the commit the other fixtures use. */
export const HEAD = 'a91c0ffe21b7d4c3e8f05a1b2c3d4e5f60718293';
export const OTHER_REPO = '/srv/work/billing-worker';
export const OTHER_HEAD = '0c5e9214b83f7a0d6e1c2b3a495867788796a5b4';

const repository = (over: Partial<RepositoryBody>): RepositoryBody => ({
  name: 'payments-api',
  path: REPO,
  root: '/srv/work',
  remote: 'https://github.com/example/payments-api.git',
  repository: 'example/payments-api',
  default_branch: 'main',
  head: HEAD,
  ecosystems: [{ name: 'dotnet', markers: ['Payments.slnx'] }],
  verify: { command: ['dotnet', 'test', '--nologo'], source: 'detected' },
  verify_candidates: [],
  integrator_mode: 'ask',
  watched_by_intake: false,
  open_pull_requests: [],
  ...over,
});

export function repositories(now = Date.now()): RepositoriesBody {
  return {
    contract_version: 'repositories/v1',
    generated_at: new Date(now).toISOString(),
    not_recorded: [],
    roots: [{ path: '/srv/work', exists: true }],
    repositories: [
      repository({}),
      repository({
        name: 'billing-worker',
        path: OTHER_REPO,
        repository: 'example/billing-worker',
        head: OTHER_HEAD,
        ecosystems: [
          { name: 'node', markers: ['package.json'] },
          { name: 'python', markers: ['pyproject.toml'] },
        ],
      }),
    ],
  };
}

const group = (
  repo: string,
  size: string,
  tasks: number,
  p80: number,
  median = p80 / 2,
): LearnGroup => ({
  repository: repo,
  size_class: size,
  tasks,
  spend_known: tasks,
  spend_median_usd: median,
  spend_p80_usd: p80,
  completed: Math.round(tasks * 0.8),
  partial: Math.round(tasks * 0.1),
  completion_rate: 0.8,
  partial_rate: 0.1,
  checkpointed: Math.round(tasks * 0.1),
  checkpoint_rate: 0.1,
  checkpointed_completed: 0,
  checkpointed_completed_rate: null,
  prs: {
    total: tasks,
    merged_unchanged: Math.round(tasks * 0.5),
    merged_edited: 0,
    merged: 0,
    closed: 0,
    open: tasks - Math.round(tasks * 0.5),
  },
  merged_unchanged_rate: 0.5,
});

/**
 * Enough S history in the first repository to be trusted on its own (24 tasks), M history spread over two repositories (14 and 12,
 * so only pooled they pass the 20-task minimum), and L history too thin anywhere (6 tasks), so L falls back to the flat budget.
 */
export function learn(): LearnReport {
  return {
    since: null,
    tasks: 24 + 14 + 12 + 6,
    groups: [
      group('example/payments-api', 'S', 24, 0.35),
      group('example/payments-api', 'M', 14, 1.0),
      group('example/billing-worker', 'M', 12, 0.9),
      group('example/payments-api', 'L', 6, 3.1),
    ],
  };
}

/** `serveApi({ replies })` for the routes Start a batch reads that the fake server has no default for. */
export function startReplies(): Record<string, [number, unknown]> {
  return { '/v1/repositories': [200, repositories()], '/v1/learn': [200, learn()] };
}
