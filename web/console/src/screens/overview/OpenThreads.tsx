import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { actorName, threadCardQuery, threadsQuery } from '../../api/threads';
import { formatAgo } from '../../lib/format';
import { Dialog, DialogClose, EmptyState, ErrorState, Panel, Skeleton } from '../../ui';

/** The open threads (ADR 0090 addendum): goal, next step and who moved it last; a click opens the card in a sheet. */
export function OpenThreads() {
  const threads = useQuery(threadsQuery());
  const [open, setOpen] = useState<string | null>(null);
  return (
    <Panel title="Open threads" count={threads.data?.length} busy={threads.isPending} flush>
      {threads.isPending ? (
        <div className="pad">
          <Skeleton width="70%" />
        </div>
      ) : threads.isError ? (
        <div className="pad">
          <ErrorState
            title="Cannot read the threads"
            call="GET /v1/threads"
            detail={threads.error.message}
            onRetry={() => void threads.refetch()}
          />
        </div>
      ) : threads.data.length === 0 ? (
        <EmptyState
          icon="inbox"
          title="No open thread"
          text="A thread opens when an agent records a step."
        />
      ) : (
        <ul className="stack stack--tight pad" aria-label="Open threads">
          {threads.data.map((t) => (
            <li key={t.thread}>
              <button type="button" className="link" onClick={() => setOpen(t.thread)}>
                {t.goal ?? t.thread}
              </button>
              <div className="dim small">
                Next: {t.next ?? 'none'} · {actorName(t.last_step.actor)} ·{' '}
                {formatAgo(t.last_activity)}
              </div>
            </li>
          ))}
        </ul>
      )}
      {open ? <CardSheet id={open} onClose={() => setOpen(null)} /> : null}
    </Panel>
  );
}

function CardSheet({ id, onClose }: { id: string; onClose: () => void }) {
  const card = useQuery(threadCardQuery(id));
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()} title="Thread card" hideTitle wide>
      <div className="ch-panel__head">
        <span className="ch-panel__title" aria-hidden="true">
          Thread card
        </span>
        <div className="ch-panel__aside">
          <DialogClose />
        </div>
      </div>
      <div className="pad-lg">
        {card.isPending ? <Skeleton height={80} /> : null}
        {card.isError ? (
          <ErrorState
            title="Cannot read the card"
            call="GET /v1/threads/{id}/card"
            detail={card.error.message}
            onRetry={() => void card.refetch()}
          />
        ) : null}
        {card.data ? <pre className="thread-card">{card.data.text ?? ''}</pre> : null}
      </div>
    </Dialog>
  );
}
