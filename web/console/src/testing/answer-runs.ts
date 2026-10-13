// The API for the Runs, Batch, Task and Research screens as plain data: no `fetch`, no browser. The end-to-end mock answers a page's
// requests with it, and so can a local server that serves the build, so what a test sees is what a person sees.
import type { RunSummary } from '../api/types';
import * as fx from './fixtures';
import * as rx from './fixtures-runs';

export const RESEARCH_ID = 'run-20261013-143000-5e0a11';
/** A second batch that has finished, and one the server has accepted and not started. */
export const FINISHED_ID = fx.BATCH_B;
export const QUEUED_ID = 'run-20261013-150500-0aa0aa';

export interface Reply {
  status: number;
  json?: unknown;
  /** A body that is not JSON: a server-sent-events stream. */
  text?: string;
  contentType?: string;
  headers?: Record<string, string>;
}

const ok = (json: unknown, status = 200): Reply => ({ status, json });

/** The runs the fake server holds, newest first: the two batches, a research run, and a long mixed list behind them. */
export function allRuns(now: number): RunSummary[] {
  return [
    fx.run(fx.BATCH_A, { preset: 'driven' }, now),
    rx.singleRun(RESEARCH_ID, now),
    fx.run(FINISHED_ID, { status: 'failed', error_code: 'tasks_incomplete' }, now),
    ...rx.runList(120, now),
  ];
}

const events = (list: ReadonlyArray<unknown>) =>
  list.map((e) => `event: status\ndata: ${JSON.stringify(e)}\n\n`).join('');

/**
 * Answers one request, or null for a path this does not know (the caller falls through to the foundation's answers). The query is what
 * follows the `?`.
 */
export function answerRuns(
  method: string,
  pathname: string,
  query: URLSearchParams,
  now: number,
): Reply | null {
  if (method === 'GET' && pathname === '/v1/runs') {
    const wanted = query.get('run_id');
    const since = query.get('since');
    const rows = allRuns(now).filter(
      (r) =>
        (!wanted || r.run_id === wanted) &&
        (!query.get('status') || r.status === query.get('status')) &&
        (!query.get('kind') || r.kind === query.get('kind')) &&
        (!query.get('repository') || r.repository === query.get('repository')) &&
        (!query.get('actor') || r.actor === query.get('actor')) &&
        (!since || Date.parse(r.started_at) >= Date.parse(since)),
    );
    return ok({ runs: rows.slice(0, Number(query.get('limit') ?? 100)) });
  }

  const cancel = /^\/v1\/runs\/([^/]+)\/cancel$/.exec(pathname);
  if (method === 'POST' && cancel) return ok({ run_id: cancel[1], status: 'cancelling' }, 202);

  const stream = /^\/v1\/runs\/([^/]+)\/events$/.exec(pathname);
  if (method === 'GET' && stream)
    return {
      status: 200,
      text: events(rx.batchEvents(now)),
      contentType: 'text/event-stream',
    };

  const one = /^\/v1\/runs\/([^/]+)$/.exec(pathname);
  if (method === 'GET' && one) {
    const id = decodeURIComponent(one[1] ?? '');
    return id === RESEARCH_ID
      ? ok(rx.researchResult())
      : ok({ contract_version: 'run-status/v1', run_id: id, status: 'running' }, 202);
  }

  const task = /^\/v1\/batches\/([^/]+)\/tasks\/([^/]+)$/.exec(pathname);
  if (method === 'GET' && task)
    return ok(
      rx.taskDetail(decodeURIComponent(task[1] ?? ''), decodeURIComponent(task[2] ?? ''), now),
    );

  const batch = /^\/v1\/batches\/([^/]+)$/.exec(pathname);
  if (method === 'GET' && batch) {
    const id = decodeURIComponent(batch[1] ?? '');
    if (id === QUEUED_ID) return { status: 404, json: { errors: ['no such batch'] } };
    return ok(id === FINISHED_ID ? rx.finishedBatch(id, now) : rx.richBatch(id, now));
  }

  if (method === 'GET' && pathname === '/v1/pulls') return ok(rx.batchPulls(now));
  return null;
}
