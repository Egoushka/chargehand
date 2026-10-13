import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useRef } from 'react';
import { type InboxItem, useInbox } from '../api/inbox';
import { runsQuery } from '../api/queries';
import type { RunState, RunSummary } from '../api/types';
import { baseName, formatTokens, formatUsd, shortId } from '../lib/format';
import { safeHttpUrl } from '../lib/url';
import { showBrowserNotice } from './notifications';
import { type NotifyKinds, type Preferences, usePreferences } from './preferences';
import { type ToastSpec, useToast } from './toasts';

const OPEN: ReadonlySet<RunState> = new Set(['queued', 'running']);

interface Notice {
  kind: keyof NotifyKinds;
  /** Stable across polls: the same event is raised once. */
  key: string;
  toast: ToastSpec;
}

function inboxNotice(item: InboxItem, go: (to: string) => void): Notice {
  const task = item.task_id ?? shortId(item.run_id);
  const where = baseName(item.repository);
  if (item.kind === 'question') {
    return {
      kind: 'question',
      key: item.id,
      toast: {
        tone: 'accent',
        icon: 'help',
        title: `${task} asked a question`,
        body: item.questions[0] ?? item.title,
        actions: [{ label: 'Answer', onSelect: () => go('/needs-you') }],
      },
    };
  }
  if (item.kind === 'pull_request') {
    const pr = item.pull_request;
    const url = safeHttpUrl(pr?.url);
    const why =
      item.reason === 'test_integrity'
        ? 'Only tests or build files changed, so it waits for you.'
        : item.reason === 'partial_draft'
          ? 'A [partial] draft holds the kept work.'
          : 'Policy is ask: it waits for you to merge it.';
    return {
      kind: 'pull_request',
      key: item.id,
      toast: {
        tone: 'accent',
        icon: 'pr',
        title: `PR ready on ${where}`,
        body: `${item.title}. ${why}`,
        actions: [
          {
            label: 'Open PR',
            onSelect: () =>
              url ? window.open(url, '_blank', 'noopener,noreferrer') : go('/needs-you'),
          },
        ],
      },
    };
  }
  return {
    kind: 'failure',
    key: item.id,
    toast: {
      tone: 'danger',
      icon: 'alert-circle',
      title: `${task} failed`,
      body: item.error ? `${item.error.code}: ${item.error.message}` : (item.reason ?? item.title),
      actions: [{ label: 'Open', onSelect: () => go('/needs-you') }],
    },
  };
}

function batchNotice(run: RunSummary, go: (to: string) => void): Notice {
  const ok = run.status === 'completed';
  const where = baseName(run.repository);
  return {
    kind: ok ? 'batch' : 'failure',
    key: `batch:${run.run_id}:${run.status}`,
    toast: {
      tone: ok ? 'ok' : 'danger',
      icon: ok ? 'check' : 'alert-circle',
      title: ok ? `Batch finished: ${where}` : `Batch failed: ${where}`,
      body: `${shortId(run.run_id)}: ${formatTokens(run.tokens)} tokens · ${formatUsd(run.usd)}${run.error_code ? ` · ${run.error_code}` : ''}`,
      actions: [{ label: 'Open run', onSelect: () => go(`/runs/${run.run_id}`) }],
    },
  };
}

interface Latest {
  prefs: Preferences;
  toast: ReturnType<typeof useToast>;
}

/** Shows one notice, once, if the person has not switched its kind off. */
function raise({ prefs, toast }: Latest, raised: Set<string>, notice: Notice): void {
  if (raised.has(notice.key) || !prefs.notify[notice.kind]) return;
  raised.add(notice.key);
  toast.push({ ...notice.toast, key: notice.key });
  if (prefs.browserNotifications && document.visibilityState === 'hidden')
    showBrowserNotice({
      title: notice.toast.title,
      body: typeof notice.toast.body === 'string' ? notice.toast.body : undefined,
      tag: notice.key,
      onClick: () => notice.toast.actions?.[0]?.onSelect(),
    });
}

/**
 * Raises a notice when something needs you: a task asks a question, a pull request waits, a task or run fails, a batch finishes
 * or fails. It reads the inbox and the batch runs the shell already polls and tells apart what was there at the start from what
 * arrived since, so opening the console does not announce yesterday. Each event shows an in-page notice and, when the person
 * turned browser notifications on and the page is hidden, a browser notification with the same title.
 */
export function useAttention(): void {
  const inbox = useInbox();
  const batches = useQuery(runsQuery({ kind: 'batch', limit: 50 }));
  const prefs = usePreferences();
  const toast = useToast();
  const navigate = useNavigate();
  const seenInbox = useRef<Set<string> | null>(null);
  const seenBatches = useRef<Map<string, RunState> | null>(null);
  const raised = useRef<Set<string>>(new Set());
  const startedAt = useRef(Date.now());

  const go = (to: string) => void navigate({ to });
  // The latest preferences, toast and navigation, read inside effects that must run only when data arrives.
  const latest = useRef({ prefs, toast, go });
  latest.current = { prefs, toast, go };

  useEffect(() => {
    const items = inbox.data?.items;
    if (!items) return;
    if (seenInbox.current === null) {
      seenInbox.current = new Set(items.map((item) => item.id));
      return;
    }
    for (const item of items) {
      if (seenInbox.current.has(item.id)) continue;
      seenInbox.current.add(item.id);
      raise(latest.current, raised.current, inboxNotice(item, latest.current.go));
    }
  }, [inbox.data]);

  useEffect(() => {
    const runs = batches.data;
    if (!runs) return;
    const before = seenBatches.current;
    seenBatches.current = new Map(runs.map((run) => [run.run_id, run.status]));
    if (before === null) return;
    for (const run of runs) {
      const was = before.get(run.run_id);
      const finishedNow = !OPEN.has(run.status) && run.status !== 'lost';
      const cameFromOpen = was !== undefined && OPEN.has(was);
      const startedAndEndedBetweenPolls =
        was === undefined && !!run.finished_at && Date.parse(run.finished_at) > startedAt.current;
      if (finishedNow && (cameFromOpen || startedAndEndedBetweenPolls))
        raise(latest.current, raised.current, batchNotice(run, latest.current.go));
    }
  }, [batches.data]);
}
