import { Link } from '@tanstack/react-router';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { type InboxItem, useDismiss, useInbox } from '../../api/inbox';
import { formatDateTime, formatTime } from '../../lib/format';
import { keysBelongElsewhere } from '../../shell/hotkeys';
import { browserNotificationsAvailable, browserPermission } from '../../shell/notifications';
import { updatePreferences, usePreferences } from '../../shell/preferences';
import { useShell } from '../../shell/ShellContext';
import { useToast } from '../../shell/toasts';
import {
  Button,
  buttonClass,
  EmptyState,
  ErrorState,
  Panel,
  Segmented,
  Skeleton,
  Switch,
} from '../../ui';
import { GroupCard } from './GroupCard';
import {
  FILTERS,
  type Filter,
  handledBy,
  laneOf,
  ordered,
  resolvedNote,
  rowIds,
  rowItems,
  rows,
  summary,
  targetRun,
} from './model';
import { NeedsCard } from './NeedsCard';
import './needs-you.css';
import { useNavigate } from '@tanstack/react-router';

/** Everything that waits for a person: questions to answer, pull requests to merge, failures to look at. */
export function NeedsYou() {
  const inbox = useInbox();
  const dismiss = useDismiss();
  const toast = useToast();
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>('all');
  const [cursor, setCursor] = useState(0);
  const cards = useRef(new Map<string, HTMLElement>());
  const answers = useRef(new Map<string, HTMLTextAreaElement>());
  const data = inbox.data;
  // Decide first, then Fix (ADR 0085); the cursor walks both as one list.
  const lanes = useMemo(() => {
    const shown = ordered(data?.items ?? [], filter);
    return {
      decide: rows(
        shown.filter((i) => laneOf(i) === 'decide'),
        data?.groups,
      ),
      fix: rows(
        shown.filter((i) => laneOf(i) === 'fix'),
        data?.groups,
      ),
    };
  }, [data, filter]);
  const items = useMemo(() => [...lanes.decide, ...lanes.fix], [lanes]);
  const at = Math.min(cursor, Math.max(0, items.length - 1));
  const resolved = resolvedNote(data?.resolved);

  function dismissShown() {
    const ids = rowIds(items);
    const shown = rowItems(items);
    if (ids.length === 0) return;
    dismiss.mutate(
      { ids },
      {
        onSuccess: () =>
          toast.push({
            tone: 'ok',
            icon: 'check',
            title: `Dismissed ${shown} item${shown === 1 ? '' : 's'}`,
            actions: [{ label: 'Undo', onSelect: () => void dismiss.restore(ids) }],
          }),
        onError: (error) =>
          toast.push({
            tone: 'danger',
            icon: 'alert-triangle',
            title: 'Not dismissed',
            body: error.message,
          }),
      },
    );
  }

  const move = useCallback(
    (by: number) => {
      setCursor((current) => {
        const next = Math.max(
          0,
          Math.min(items.length - 1, Math.min(current, items.length - 1) + by),
        );
        const id = items[next]?.id;
        if (id) cards.current.get(id)?.scrollIntoView({ block: 'nearest' });
        return next;
      });
    },
    [items],
  );

  // j and k move the cursor, Enter opens the task or run, a goes to the answer box.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey || keysBelongElsewhere(event.target))
        return;
      const row = items[at];
      const item = row?.type === 'group' ? row.items[0] : row?.item;
      if (event.key === 'j') move(1);
      else if (event.key === 'k') move(-1);
      else if (event.key === 'Enter' && item) {
        const run = targetRun(item);
        if (run) void navigate({ to: '/runs/$runId', params: { runId: run } });
      } else if (event.key === 'a' && item?.kind === 'question') {
        const box = answers.current.get(item.id);
        if (box) {
          event.preventDefault();
          box.focus();
        }
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [items, at, move, navigate]);

  const renderRow = (row: (typeof items)[number], index: number) => {
    const cardRef = (el: HTMLElement | null) => {
      if (el) cards.current.set(row.id, el);
      else cards.current.delete(row.id);
    };
    return (
      <li key={row.id}>
        {row.type === 'group' ? (
          <GroupCard group={row.group} items={row.items} cursor={index === at} cardRef={cardRef} />
        ) : (
          <NeedsCard
            item={row.item}
            cursor={index === at}
            cardRef={cardRef}
            answerRef={(el) => {
              if (el) answers.current.set(row.id, el);
              else answers.current.delete(row.id);
            }}
          />
        )}
      </li>
    );
  };

  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Needs you</h1>
          <p className="screen__sub">
            {data ? summary(data.counts) : inbox.isError ? 'Not loaded' : 'Loading'}
            {resolved ? <span className="dim"> · {resolved}</span> : null}
          </p>
        </div>
        <div className="screen__actions">
          {items.length > 0 ? (
            <Button
              variant="quiet"
              label="Dismiss all shown"
              aria-label={`Dismiss all ${rowItems(items)} shown`}
              loading={dismiss.isPending}
              onClick={dismissShown}
            />
          ) : null}
          <Segmented
            label="Filter"
            value={filter}
            onValueChange={(next) => {
              setFilter(next);
              setCursor(0);
            }}
            items={FILTERS.map((f) => ({
              id: f.id,
              label: (
                <>
                  {f.label}{' '}
                  <span className="dim">
                    {data
                      ? f.id === 'all'
                        ? data.counts.total
                        : f.id === 'question'
                          ? data.counts.questions
                          : f.id === 'pull_request'
                            ? data.counts.pull_requests
                            : data.counts.failures
                      : ''}
                  </span>
                </>
              ),
            }))}
          />
        </div>
      </div>

      {inbox.isError ? (
        <ErrorState
          title="Cannot refresh the inbox"
          call="GET /v1/inbox"
          detail={inbox.error.message}
          age={
            data
              ? `The list below is the data from ${formatTime(inbox.dataUpdatedAt, { seconds: true })}. Retrying every 10 s.`
              : undefined
          }
          onRetry={() => void inbox.refetch()}
        />
      ) : null}

      {inbox.isPending ? (
        <div className="stack" aria-busy="true">
          {[1, 2, 3].map((n) => (
            <div key={n} className="card pad-lg stack stack--tight">
              <Skeleton width="40%" />
              <Skeleton width="90%" height={40} />
              <Skeleton width="30%" height={28} />
            </div>
          ))}
        </div>
      ) : null}

      {data && data.items.length === 0 ? (
        <div className="card">
          <EmptyState
            title="Nothing needs you"
            text="No task is waiting for an answer, no PR is waiting for a merge and no run failed in the last 24 hours. You get a notice when that changes."
            action={
              <Link to="/" className={buttonClass()}>
                Back to Overview
              </Link>
            }
          />
        </div>
      ) : null}
      {data && data.items.length === 0 ? <Handled items={data.handled ?? []} /> : null}

      {data && data.items.length > 0 ? (
        <div className="g2">
          <div className="stack">
            {items.length === 0 ? (
              <div className="card">
                <EmptyState
                  icon="filter"
                  title="Nothing in this filter"
                  text="Other kinds of items are waiting. Choose All to see them."
                />
              </div>
            ) : null}
            {(
              [
                ['decide', 'Decide', lanes.decide, 0],
                ['fix', 'Fix', lanes.fix, lanes.decide.length],
              ] as const
            ).map(([lane, title, list, offset]) =>
              list.length > 0 ? (
                <section key={lane} aria-labelledby={`lane-${lane}`}>
                  <h2 id={`lane-${lane}`} className="lane__title">
                    {title} <span className="dim">{list.length}</span>
                  </h2>
                  <ul
                    className="stack list-reset"
                    aria-label={lane === 'decide' ? 'Things that need you' : 'Failures to fix'}
                  >
                    {list.map((row, n) => renderRow(row, offset + n))}
                  </ul>
                </section>
              ) : null,
            )}
            <Handled items={data.handled ?? []} />
          </div>
          <div className="stack">
            <NotifyPanel />
          </div>
        </div>
      ) : null}
    </div>
  );
}

/** What a rule resolved or a person dismissed in the window (ADR 0085), collapsed and not counted, each with Undo. */
function Handled({ items }: { items: InboxItem[] }) {
  const dismiss = useDismiss();
  const toast = useToast();
  if (items.length === 0) return null;
  return (
    <details className="card pad">
      <summary className="lane__title">
        Handled <span className="dim">{items.length}</span>
      </summary>
      <ul className="list-reset" aria-label="Handled">
        {items.map((item) => (
          <li key={item.id} className="handled-row">
            <span className="handled-row__what">{item.what ?? item.title}</span>
            <span className="dim">{handledBy(item.resolved_by)}</span>
            <span className="dim mono" title={formatDateTime(item.at)}>
              {formatTime(item.at)}
            </span>
            <Button
              size="sm"
              label="Undo"
              aria-label={`Undo: put ${item.what ?? item.title} back`}
              onClick={() =>
                void dismiss.restore([item.id]).then(
                  () => toast.push({ tone: 'ok', icon: 'check', title: 'Back in the inbox' }),
                  (error: Error) =>
                    toast.push({
                      tone: 'danger',
                      icon: 'alert-triangle',
                      title: 'Not restored',
                      body: error.message,
                    }),
                )
              }
            />
          </li>
        ))}
      </ul>
    </details>
  );
}

/** Which events raise a notice, and the one-time browser permission for notifications outside this tab. */
function NotifyPanel() {
  const prefs = usePreferences();
  const shell = useShell();
  const available = browserNotificationsAvailable();
  const permission = browserPermission();
  const kinds = [
    ['question', 'Task asks a question'],
    ['pull_request', 'PR is ready to merge'],
    ['batch', 'Batch finishes'],
    ['failure', 'Batch or task fails'],
  ] as const;
  return (
    <Panel title="Notify me">
      <div className="stack stack--tight">
        {kinds.map(([kind, label]) => (
          <div key={kind} className="notify-row">
            <label htmlFor={`notify-${kind}`}>{label}</label>
            <Switch
              id={`notify-${kind}`}
              checked={prefs.notify[kind]}
              onChange={(e) =>
                updatePreferences({ notify: { ...prefs.notify, [kind]: e.target.checked } })
              }
            />
          </div>
        ))}
        {!available ? (
          <p className="dim small">
            Browser notifications need a secure page (https, or localhost). Notices still appear
            here while the console is open.
          </p>
        ) : permission === 'denied' ? (
          <p className="dim small">
            Notifications are blocked for this site in the browser. Allow them in its site settings.
          </p>
        ) : prefs.browserNotifications && permission === 'granted' ? (
          <div className="row row--between">
            <span className="dim small">
              Browser notifications are on when this tab is in the background.
            </span>
            <Button
              label="Turn off"
              size="sm"
              onClick={() => updatePreferences({ browserNotifications: false })}
            />
          </div>
        ) : (
          <div className="row row--between">
            <span className="dim small">
              Needs your permission once per browser. On a phone, install the page to the home
              screen first.
            </span>
            <Button label="Turn on" size="sm" onClick={shell.enableNotifications} />
          </div>
        )}
      </div>
    </Panel>
  );
}
