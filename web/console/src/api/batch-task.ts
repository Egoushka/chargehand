import { queryOptions, useQuery } from '@tanstack/react-query';
import type { ArtifactRef, BatchTaskBody, TimelineBody } from './batch';
import { getJson } from './client';
import type { BatchRepository, ErrorBody } from './common';

/** `batch-task/v1`: one task with its whole goal and the artifacts of its result as they are. */
export interface BatchTaskDetailBody {
  contract_version: 'batch-task/v1';
  batch_run_id: string;
  actor?: string | null;
  repository?: BatchRepository | null;
  task: BatchTaskBody;
  summary?: string | null;
  result_error?: ErrorBody | null;
  artifacts: ArtifactRef[];
  /** chargehand's own run of the verify command in a fresh container, and its test-integrity result. */
  verification?: unknown;
  /** What the session said it ran; never merged with `verification`. */
  session_tests?: unknown;
  /** Files with their line counts, and the diff (cut to the result's inline limit). */
  changes?: unknown;
  review?: unknown;
  /** What an unfinished task leaves for a continuation. */
  kept_work?: unknown;
  timeline: TimelineBody;
  not_recorded: string[];
}

export const batchTaskQuery = (batch: string, task: string, events = 500) =>
  queryOptions({
    queryKey: ['batch-task', batch, task, events],
    queryFn: ({ signal }) =>
      getJson<BatchTaskDetailBody>(
        `/v1/batches/${encodeURIComponent(batch)}/tasks/${encodeURIComponent(task)}?events=${events}`,
        signal,
      ),
  });

export const useBatchTask = (batch: string, task: string, events = 500) =>
  useQuery(batchTaskQuery(batch, task, events));
