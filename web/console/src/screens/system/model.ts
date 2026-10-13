import type {
  ContainerBody,
  ImageBody,
  PresetBody,
  RunnerBody,
  SystemBody,
} from '../../api/system';
import { DASH, formatDuration, middleTruncate, shortId } from '../../lib/format';
import type { Status } from '../../lib/status';

/** How long the server has been up, from the start time the route reports. */
export const uptime = (startedAt: string, now: number = Date.now()): string =>
  formatDuration((now - Date.parse(startedAt)) / 1000);

export interface RunnerView {
  status: Status;
  /** The full sentence, with the probe's time or error. */
  text: string;
  /** One word for a table cell. */
  short: string;
  tone?: 'warn' | 'danger' | 'off';
}

/** Whether the runner answered the server's probe: `reachable` is null when driven sessions are off. */
export function runnerView(runner: RunnerBody): RunnerView {
  if (!runner.configured && runner.reachable == null)
    return { status: 'off', text: 'not configured', short: 'not configured', tone: 'off' };
  if (runner.reachable === false)
    return {
      status: 'failed',
      text: `unreachable${runner.error ? `: ${runner.error}` : ''}`,
      short: 'unreachable',
      tone: 'danger',
    };
  if (runner.reachable == null)
    return { status: 'off', text: 'driven sessions are off', short: 'off', tone: 'off' };
  return {
    status: 'done',
    text: `reachable${runner.elapsed_ms != null ? ` in ${runner.elapsed_ms} ms` : ''}`,
    short: 'reachable',
  };
}

export interface ContainerRow {
  name: string;
  kind: ContainerBody['kind'];
  /** The task a session works on; for another kind, the batch it belongs to. */
  owner: string;
  actor: string;
  /** Only a session container has a task to cancel; the server has no route to stop one yet. */
  session: boolean;
}

export function containerRows(runner: RunnerBody): ContainerRow[] {
  return runner.containers.map((c) => ({
    name: c.name,
    kind: c.kind,
    owner: c.task_id
      ? `${c.task_id}${c.batch_run_id ? ` · ${shortId(c.batch_run_id)}` : ''}`
      : c.batch_run_id
        ? `batch ${shortId(c.batch_run_id)}`
        : DASH,
    actor: c.actor ?? DASH,
    session: c.kind === 'session',
  }));
}

/** Counts by kind, for the line under the table. */
export function containerCounts(runner: RunnerBody): string {
  const counts = new Map<string, number>();
  for (const c of runner.containers) counts.set(c.kind, (counts.get(c.kind) ?? 0) + 1);
  return [...counts].map(([kind, n]) => `${n} ${kind}`).join(' · ');
}

export interface PresetRow {
  name: string;
  version: string;
  mode: string;
  actions: string;
  error?: string;
}

export function presetRows(presets: PresetBody[]): PresetRow[] {
  return presets.map((p) => ({
    name: p.name,
    version: p.version ?? DASH,
    mode: p.driven ? 'driven' : p.approval ? 'approval' : 'read-only',
    actions: p.allowed_actions.length ? p.allowed_actions.join(', ') : DASH,
    error: p.error ?? undefined,
  }));
}

export interface ImageRow {
  name: string;
  digest: string;
  full: string;
  pinned: boolean;
}

export const imageRows = (images: ImageBody[]): ImageRow[] =>
  images.map((i) => ({
    name: i.name,
    digest: i.digest ? middleTruncate(i.digest, 28) : 'no digest',
    full: i.reference,
    pinned: i.pinned,
  }));

/** The profile as the route returns it, as text: secret items are already booleans, URLs already schemes and hosts. */
export const profileText = (system: SystemBody): string =>
  system.profile ? JSON.stringify(system.profile, null, 2) : '';
