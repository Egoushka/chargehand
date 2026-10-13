import type { SeriesPoint } from '../../api/analytics';
import type { PullRequestBody } from '../../api/common';
import type { RepositoryBody, VerifyBody } from '../../api/repositories';
import { middleTruncate } from '../../lib/format';
import type { Status } from '../../lib/status';

export type CheckLevel = 'ok' | 'warn' | 'fail';

export interface Check {
  level: CheckLevel;
  text: string;
}

export const CHECK_STATUS: Record<CheckLevel, Status> = {
  ok: 'done',
  warn: 'partial',
  fail: 'failed',
};

export const CHECK_LABEL: Record<CheckLevel, string> = {
  ok: 'OK',
  warn: 'Warning',
  fail: 'Failure',
};

/** A proposed test command as one line, the way a person would type it. */
export const commandLine = (verify: VerifyBody): string => verify.command.join(' ');

export const shortHead = (head: string | null | undefined): string | null =>
  head ? head.slice(0, 7) : null;

/**
 * What this read of the checkout found. It is not `chargehand doctor`: the server does not run or keep a doctor result, so the
 * card says so beside these.
 */
export function checkoutChecks(repo: RepositoryBody): Check[] {
  const checks: Check[] = [];
  checks.push(
    repo.error || !repo.head
      ? { level: 'fail', text: repo.error ?? 'git could not read HEAD' }
      : { level: 'ok', text: `HEAD ${shortHead(repo.head)} is readable` },
  );
  checks.push(
    repo.default_branch
      ? { level: 'ok', text: `Default branch ${repo.default_branch}` }
      : {
          level: 'warn',
          text: 'No default branch: origin/HEAD is not set and no branch is checked out',
        },
  );
  checks.push(
    !repo.remote
      ? { level: 'warn', text: 'No origin remote: chargehand cannot push or open a pull request' }
      : repo.repository
        ? { level: 'ok', text: `origin is ${repo.repository}` }
        : {
            level: 'warn',
            text: 'origin does not parse as a forge repository, so its pull requests cannot be linked',
          },
  );
  checks.push(
    repo.ecosystems.length
      ? { level: 'ok', text: `Detected ${repo.ecosystems.map((e) => e.name).join(', ')}` }
      : { level: 'warn', text: 'No ecosystem detected from marker files' },
  );
  checks.push(
    repo.verify
      ? { level: 'ok', text: `Test command proposed from ${repo.verify.source}` }
      : { level: 'warn', text: 'No test command proposed: name one in context.verify' },
  );
  return checks;
}

export interface History {
  /** Pass rate per week, in percent, for the weeks that had tasks. */
  weeks: number[];
  tasks: number;
  completed: number;
  /** Percent of the tasks that completed; null when there were none. */
  rate: number | null;
}

/** The weekly points of one repository: the group is the repository's path as the request named it, or its forge name. */
export function repoHistory(points: SeriesPoint[], repo: RepositoryBody): History {
  const mine = points
    .filter((p) => p.group != null && (p.group === repo.path || p.group === repo.repository))
    .sort((a, b) => a.bucket_start.localeCompare(b.bucket_start));
  const tasks = mine.reduce((sum, p) => sum + p.tasks, 0);
  const completed = mine.reduce((sum, p) => sum + p.completed, 0);
  return {
    weeks: mine.filter((p) => p.tasks > 0).map((p) => Math.round((p.completed / p.tasks) * 100)),
    tasks,
    completed,
    rate: tasks > 0 ? Math.round((completed / tasks) * 100) : null,
  };
}

export interface PrLabel {
  status: Status;
  text: string;
}

/** Why a pull request is on the list: what it waits for, or that it only waits for its checks. */
export function prLabel(pr: PullRequestBody): PrLabel {
  if (pr.awaiting_person)
    return pr.awaiting_reason === 'partial_draft'
      ? { status: 'partial', text: 'Partial draft' }
      : pr.awaiting_reason === 'test_integrity'
        ? { status: 'partial', text: 'Test-integrity flagged' }
        : { status: 'asked', text: 'Waiting for you' };
  return { status: 'waiting', text: 'Open' };
}

/** The test command and the others the detector found, each as one line, the proposal first. */
export function testCommands(repo: RepositoryBody): { best: string | null; others: string[] } {
  const best = repo.verify ? commandLine(repo.verify) : null;
  const others = repo.verify_candidates.map(commandLine).filter((line) => line !== best);
  return { best, others };
}

export const rootLabel = (path: string) => middleTruncate(path, 48);
