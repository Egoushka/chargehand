import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import {
  answerApproval,
  type ConversationEvent,
  conversationQuery,
  endConversation,
  isOver,
  sendMessage,
  useConversationEvents,
} from '../../api/conversations';
import { formatTokens, formatUsd, shortRepo } from '../../lib/format';
import {
  Banner,
  Button,
  ConfirmDialog,
  ErrorState,
  Skeleton,
  StatusPill,
  Textarea,
} from '../../ui';
import { type Item, pending, statusOf, transcript } from './model';

const STATE_EVENTS = new Set([
  'state',
  'usage',
  'approval_requested',
  'approval_answered',
  'ended',
]);

interface LineProps {
  item: Item;
  onAnswer: (call: string, allow: boolean) => void;
  busy: boolean;
}

function Line({ item, onAnswer, busy }: LineProps) {
  switch (item.kind) {
    case 'message':
      return (
        <div className={`cv-msg cv-msg--${item.role === 'user' ? 'user' : 'agent'}`}>
          <span className="cv-msg__who">{item.role === 'user' ? 'You' : 'Agent'}</span>
          <p className="cv-msg__text">{item.text}</p>
        </div>
      );
    case 'tool':
      return (
        <div className="cv-tool">
          <span className="cv-tool__title">{item.title}</span>
          <span className="cv-tool__meta">
            {item.toolKind} · {item.status}
            {item.paths.length > 0 ? ` · ${item.paths.join(', ')}` : ''}
          </span>
        </div>
      );
    case 'approval':
      return (
        <fieldset className="cv-approval" aria-label={`Approval: ${item.title}`}>
          <span className="cv-tool__title">{item.title}</span>
          <span className="cv-tool__meta">
            asks to run{item.paths.length > 0 ? ` · ${item.paths.join(', ')}` : ''}
          </span>
          {item.decision === null ? (
            <div className="cv-approval__actions">
              <Button
                label="Approve"
                variant="primary"
                size="touch"
                disabled={busy}
                onClick={() => onAnswer(item.callId, true)}
              />
              <Button
                label="Decline"
                size="touch"
                disabled={busy}
                onClick={() => onAnswer(item.callId, false)}
              />
            </div>
          ) : (
            <span className="cv-tool__meta">
              {item.decision === 'allow_once' ? 'approved' : 'declined'}
            </span>
          )}
        </fieldset>
      );
    case 'error':
      return (
        <Banner tone="danger" icon="alert-circle" title="The agent reported an error">
          {item.text}
        </Banner>
      );
    case 'ended':
      return (
        <p className="cv-ended">Ended{item.reason ? `: ${item.reason.replace(/_/g, ' ')}` : ''}</p>
      );
  }
}

/** One conversation: its transcript live from the event stream, a box to send a message, and the approvals and end button. */
export function Detail({ id }: { id: string }) {
  const client = useQueryClient();
  const view = useQuery({ ...conversationQuery(id), refetchInterval: 5_000 });
  const refresh = () => {
    void client.invalidateQueries({ queryKey: ['conversations'] });
  };
  const stream = useConversationEvents(id, (e: ConversationEvent) => {
    if (STATE_EVENTS.has(e.type)) refresh();
  });
  const items = transcript(stream.events);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [ending, setEnding] = useState(false);
  const bottom = useRef<HTMLDivElement>(null);
  const count = items.length;
  useEffect(() => {
    if (count > 0) bottom.current?.scrollIntoView?.({ block: 'end' });
  }, [count]);

  const act = async (run: () => Promise<unknown>) => {
    setBusy(true);
    setFailure(null);
    try {
      await run();
      refresh();
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  if (view.isPending) return <Skeleton height={160} />;
  if (view.isError || !view.data)
    return (
      <ErrorState
        title="Could not read the conversation"
        call={`GET /v1/conversations/${id}`}
        onRetry={() => void view.refetch()}
      />
    );
  const c = view.data;
  const over = isOver(c);
  const waiting = pending(items).length;

  return (
    <div className="cv-detail">
      <header className="cv-detail__head">
        <div>
          <h2 className="cv-detail__title">{shortRepo(c.repository.repository)}</h2>
          <p className="cv-detail__sub">
            {c.runtime}
            {c.model ? ` · ${c.model}` : ''} ·{' '}
            {formatTokens((c.usage?.input_tokens ?? 0) + (c.usage?.output_tokens ?? 0))} tokens
            {c.usage?.usd == null ? '' : ` · ${formatUsd(c.usage.usd)}`}
          </p>
        </div>
        <StatusPill status={statusOf(c)} label={waiting > 0 ? 'Needs approval' : undefined} />
        {over ? null : (
          <Button label="End conversation" variant="danger-quiet" onClick={() => setEnding(true)} />
        )}
      </header>
      {stream.error ? (
        <Banner tone="warn" icon="alert-triangle" title="The live stream broke">
          {stream.error}; asking again.
        </Banner>
      ) : null}
      {failure ? (
        <Banner tone="danger" icon="alert-circle" title="That did not go through" role="alert">
          {failure}
        </Banner>
      ) : null}
      <div className="cv-transcript" aria-live="polite">
        {count === 0 ? <p className="cv-ended">Nothing said yet.</p> : null}
        {items.map((item) => (
          <Line
            key={item.key}
            item={item}
            busy={busy}
            onAnswer={(call, allow) =>
              void act(() => answerApproval(id, call, allow ? 'allow_once' : 'reject'))
            }
          />
        ))}
        <div ref={bottom} />
      </div>
      <form
        className="cv-compose"
        onSubmit={(e) => {
          e.preventDefault();
          const message = text.trim();
          if (message === '') return;
          void act(async () => {
            await sendMessage(id, message);
            setText('');
          });
        }}
      >
        <Textarea
          aria-label="Message"
          touch
          rows={2}
          value={text}
          disabled={over}
          placeholder={over ? 'This conversation is over.' : 'Message the agent'}
          onChange={(e) => setText(e.target.value)}
        />
        <Button
          label="Send"
          type="submit"
          variant="primary"
          size="touch"
          disabled={over || busy || text.trim() === ''}
        />
      </form>
      <ConfirmDialog
        open={ending}
        onOpenChange={setEnding}
        title="End this conversation?"
        text="The running turn is cancelled and the agent stops. A conversation that has ended cannot be continued."
        keepLabel="Keep going"
        confirmLabel="End conversation"
        onConfirm={() => {
          setEnding(false);
          void act(() => endConversation(id));
        }}
      />
    </div>
  );
}
