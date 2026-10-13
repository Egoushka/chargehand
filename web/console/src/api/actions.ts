import { postJson } from './client';

/** What the four action routes answer (`202`, or `200` with `changed: false` when a repeat changed nothing). A refusal is a `409` problem body. */
export interface ActionResult {
  batch_run_id?: string;
  task_id?: string;
  action: 'cancel' | 'retry' | 'session_stop' | 'checks_rerun';
  changed: boolean;
  state?: string;
  /** `retry`: the new batch. */
  run_id?: string;
  continue_from?: boolean;
  /** `checks_rerun`. */
  checks?: string[];
  rerun_runs?: number;
}

const task = (batch: string, id: string) =>
  `/v1/batches/${encodeURIComponent(batch)}/tasks/${encodeURIComponent(id)}`;

export const cancelTask = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/cancel`);
export const retryTask = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/retry`);
export const stopSession = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/session/stop`);
export const rerunChecks = (owner: string, repo: string, pull: number) =>
  postJson<ActionResult>(
    `/v1/pulls/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/${pull}/checks/rerun`,
  );
