import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta } from './common';

/** Which session a transcript is of. */
export interface TranscriptSessionBody {
  run_id: string;
  role: string;
  runner?: string | null;
  started?: string | null;
  ended?: string | null;
  status?: string | null;
}

/**
 * `transcript/v1`: the end of one session's transcript (ADR 0059). `lines` are `stream-json` events as text, oldest first, with secrets redacted;
 * a line over 32 KiB is a `{"type":"chargehand_cut","bytes":N}` stub. They are model and tool output: show them as text, never as markup.
 * `available` is false when there is none, and `reason` is `session_gone`, `runner_unreachable`, `runner_too_old`, `runner_error`, `runner_unknown` or `driven_off`.
 */
export interface TranscriptBody extends Omit<ContractMeta, 'generated_at'> {
  contract_version: 'transcript/v1';
  batch_run_id: string;
  task_id: string;
  session: TranscriptSessionBody;
  available: boolean;
  reason?: string | null;
  /** What the runner returned, and the window asked for. */
  bytes: number;
  max_bytes: number;
  /** Earlier lines exist. */
  truncated: boolean;
  /** Lines replaced by a stub for their length. */
  cut: number;
  /** The kinds of secret that were replaced; never a value. */
  redacted: string[];
  lines: string[];
}

/** `bytes` is the window, 1024 to 262144 (default 65536). A live session's transcript changes, so poll it slowly. */
export const transcriptQuery = (batch: string, task: string, session: string, bytes = 65_536) =>
  queryOptions({
    queryKey: ['transcript', batch, task, session, bytes],
    queryFn: ({ signal }) =>
      getJson<TranscriptBody>(
        `/v1/batches/${encodeURIComponent(batch)}/tasks/${encodeURIComponent(task)}/sessions/${encodeURIComponent(session)}/transcript?bytes=${bytes}`,
        signal,
      ),
    staleTime: 0,
    gcTime: 0,
  });

export const useTranscript = (batch: string, task: string, session: string, bytes = 65_536) =>
  useQuery(transcriptQuery(batch, task, session, bytes));
