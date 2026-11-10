import { useQuery } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { conversationsQuery } from '../../api/conversations';
import { formatAgo, formatUsd, shortRepo } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import { buttonClass, EmptyState, ErrorState, Icon, Skeleton, StatusPill } from '../../ui';
import { Detail } from './Detail';
import { statusOf } from './model';
import './conversations.css';

export function Conversations() {
  const { id } = useSearch({ from: '/conversations' });
  const phone = useIsPhone();
  const list = useQuery(conversationsQuery());
  const showList = !(phone && id);

  return (
    <div className="screen cv-screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Conversations</h1>
          <p className="screen__sub">
            Agents you steer while they work: read, message, approve, end.
          </p>
        </div>
      </div>
      <div className="cv-layout">
        {showList ? (
          <nav className="cv-list" aria-label="Conversations">
            {list.isPending ? <Skeleton height={120} /> : null}
            {list.isError ? (
              <ErrorState
                title="Could not read the conversations"
                call="GET /v1/conversations"
                onRetry={() => void list.refetch()}
              />
            ) : null}
            {list.data?.length === 0 ? (
              <EmptyState
                title="No conversations"
                icon="inbox"
                text="Start one with POST /v1/conversations; it appears here."
              />
            ) : null}
            {list.data?.map((c) => (
              <Link
                key={c.id}
                to="/conversations"
                search={{ id: c.id }}
                className="cv-row"
                aria-current={c.id === id ? 'page' : undefined}
              >
                <span className="cv-row__main">
                  <span className="cv-row__title">{shortRepo(c.repository.repository)}</span>
                  <span className="cv-row__meta">
                    {c.runtime} · {formatAgo(c.updated_at)} · {formatUsd(c.usage?.usd ?? null)}
                  </span>
                </span>
                <StatusPill
                  status={statusOf(c)}
                  label={c.pending_approvals.length > 0 ? 'Needs approval' : undefined}
                />
              </Link>
            ))}
          </nav>
        ) : null}
        {id ? (
          <section className="cv-pane" aria-label="Conversation">
            {phone ? (
              <Link to="/conversations" search={{}} className={buttonClass({ size: 'touch' })}>
                <Icon name="arrow-left" /> All conversations
              </Link>
            ) : null}
            <Detail key={id} id={id} />
          </section>
        ) : phone ? null : (
          <section className="cv-pane">
            <EmptyState
              title="Pick a conversation"
              icon="inbox"
              text="Its transcript streams here."
            />
          </section>
        )}
      </div>
    </div>
  );
}
