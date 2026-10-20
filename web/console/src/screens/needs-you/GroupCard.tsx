import { Link } from '@tanstack/react-router';
import { type Ref, useId, useState } from 'react';
import { type InboxGroup, type InboxItem, useDismiss } from '../../api/inbox';
import { baseName, formatDateTime, formatTime, shortId } from '../../lib/format';
import { inboxStatus } from '../../lib/status';
import { useToast } from '../../shell/toasts';
import { Badge, Button, buttonClass, StatusPill } from '../../ui';
import { targetRun } from './model';

export interface GroupCardProps {
  group: InboxGroup;
  /** The group's items in the inbox body, newest first. */
  items: InboxItem[];
  cursor?: boolean;
  cardRef?: Ref<HTMLElement>;
}

/** Failures with the same repository and code as one card (CHARGEHAND-237): the count, the newest, the rest behind Show. */
export function GroupCard({ group, items, cursor, cardRef }: GroupCardProps) {
  const [open, setOpen] = useState(false);
  const heading = useId();
  const list = useId();
  const newest = items[0] as InboxItem;
  const code = group.reason ?? newest.error?.code ?? 'failure';
  const run = targetRun(newest);
  const repo = baseName(group.repository);
  return (
    <article
      ref={cardRef}
      className="card"
      data-cursor={cursor ? 'true' : undefined}
      aria-labelledby={heading}
      tabIndex={-1}
    >
      <div className="card__head">
        <StatusPill status={inboxStatus(newest)} />
        <Badge size="s" label={repo} title={group.repository ?? undefined} />
        <Badge
          size="s"
          label={`×${group.count}`}
          title={`${group.count} failures with this code on this repository`}
        />
        <span className="spacer" />
        <span className="dim mono" title={formatDateTime(group.at)}>
          {formatTime(group.at)}
        </span>
      </div>
      <div className="card__body">
        <h3 id={heading} className="card__title">
          {group.count} failures: <span className="mono">{code}</span>
          {group.repository ? ` on ${repo}` : ''}
        </h3>
        <div className="card__note">Newest: {newest.title}</div>
        {newest.error?.message || newest.error?.action ? (
          <div className="card__note">
            {[newest.error?.message, newest.error?.action]
              .filter(Boolean)
              .map((part) => (part as string).replace(/\.$/, ''))
              .join('. ')}
            .
          </div>
        ) : null}
        {open ? (
          <ul id={list} className="group-list list-reset" aria-label={`The ${code} failures`}>
            {items.map((item) => (
              <li key={item.id} className="group-list__row">
                <span className="group-list__title" title={item.title}>
                  {item.title}
                </span>
                {(item.count ?? 1) > 1 ? <span className="dim mono">×{item.count}</span> : null}
                <span className="dim mono" title={formatDateTime(item.at)}>
                  {formatTime(item.at)}
                </span>
                {targetRun(item) ? (
                  <Link
                    to="/runs/$runId"
                    params={{ runId: targetRun(item) as string }}
                    className="mono"
                    aria-label={`Open run ${item.task_id ?? shortId(item.run_id)}`}
                  >
                    {item.task_id ?? shortId(item.run_id)}
                  </Link>
                ) : null}
              </li>
            ))}
            {group.items.length > items.length ? (
              <li className="dim small">
                {group.items.length - items.length} more beyond the list's limit.
              </li>
            ) : null}
          </ul>
        ) : null}
      </div>
      <div className="card__foot">
        <Button
          label={open ? 'Hide runs' : `Show ${group.items.length} runs`}
          aria-expanded={open}
          aria-controls={open ? list : undefined}
          onClick={() => setOpen((was) => !was)}
        />
        {run ? (
          <Link
            to="/runs/$runId"
            params={{ runId: run }}
            className={buttonClass()}
            aria-label={`Open newest run ${newest.task_id ?? shortId(run)}`}
          >
            Open newest
          </Link>
        ) : null}
        <DismissGroup group={group} code={code} />
      </div>
    </article>
  );
}

/** Takes every failure of the group out of the inbox in one call; the notice offers Undo. */
function DismissGroup({ group, code }: { group: InboxGroup; code: string }) {
  const toast = useToast();
  const dismiss = useDismiss();
  const n = group.count;
  return (
    <Button
      variant="quiet"
      label={`Dismiss all ${n}`}
      aria-label={`Dismiss all ${n} ${code} failures`}
      loading={dismiss.isPending}
      onClick={() =>
        dismiss.mutate(
          { ids: [group.id] },
          {
            onSuccess: () =>
              toast.push({
                tone: 'ok',
                icon: 'check',
                title: `Dismissed ${n} ${code} failures`,
                actions: [{ label: 'Undo', onSelect: () => void dismiss.restore([group.id]) }],
              }),
            onError: (error) =>
              toast.push({
                tone: 'danger',
                icon: 'alert-triangle',
                title: 'Not dismissed',
                body: error.message,
              }),
          },
        )
      }
    />
  );
}
