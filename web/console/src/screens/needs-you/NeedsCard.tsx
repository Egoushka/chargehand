import { Link } from '@tanstack/react-router';
import { type Ref, useId, useState } from 'react';
import { useAnswer } from '../../api/answer';
import { type InboxItem, useDismiss } from '../../api/inbox';
import { baseName, formatDateTime, formatTime, shortId } from '../../lib/format';
import { inboxStatus } from '../../lib/status';
import { safeHttpUrl } from '../../lib/url';
import { useToast } from '../../shell/toasts';
import {
  Badge,
  Banner,
  Button,
  buttonClass,
  cx,
  Field,
  Icon,
  StatusPill,
  Textarea,
} from '../../ui';
import { targetRun, waitingFor } from './model';
import { Options } from './Options';

export interface NeedsCardProps {
  item: InboxItem;
  /** The keyboard cursor is on this card. */
  cursor?: boolean;
  /** Where `a` puts the focus. */
  answerRef?: Ref<HTMLTextAreaElement>;
  cardRef?: Ref<HTMLElement>;
}

/** One thing that needs you, with the action that deals with it: answer a question, review a pull request, look at a failure. */
export function NeedsCard({ item, cursor, answerRef, cardRef }: NeedsCardProps) {
  const status = inboxStatus(item);
  const run = targetRun(item);
  const openLabel = item.kind === 'question' && item.task_id ? 'Open task' : 'Open run';
  const heading = useId();
  // A server since ADR 0085 says what, why and the options; the card shows those four parts.
  const decision = Boolean(item.what && item.options);
  const label = item.task_id ?? (item.run_id ? shortId(item.run_id) : item.title);
  return (
    <article
      ref={cardRef}
      className={cx('card', item.kind === 'question' && 'card--accent')}
      data-cursor={cursor ? 'true' : undefined}
      aria-labelledby={heading}
      tabIndex={-1}
    >
      <div className="card__head">
        <StatusPill status={status} />
        <Badge size="s" label={baseName(item.repository)} title={item.repository ?? undefined} />
        {item.pull_request ? (
          <Badge
            size="s"
            mode={item.pull_request.integrator_mode}
            label={item.pull_request.integrator_mode}
          />
        ) : null}
        {item.pull_request?.test_integrity_flagged ? (
          <Badge size="s" tone="warn" label="test-integrity flagged" />
        ) : null}
        {(item.count ?? 1) > 1 ? (
          <Badge
            size="s"
            label={`×${item.count}`}
            title={`${item.count} identical failures; this is the newest`}
          />
        ) : null}
        <span className="spacer" />
        <span className="dim mono" title={formatDateTime(item.at)}>
          {formatTime(item.at)}
        </span>
      </div>
      <div className="card__body">
        {decision ? (
          <Decision item={item} headingId={heading} answerRef={answerRef} />
        ) : item.kind === 'question' ? (
          <Question item={item} headingId={heading} answerRef={answerRef} />
        ) : item.kind === 'pull_request' ? (
          <PullRequest item={item} headingId={heading} />
        ) : (
          <Failure item={item} headingId={heading} />
        )}
        <div className="mono dim small" title={item.task_id ?? item.run_id ?? undefined}>
          {item.task_id ?? shortId(item.run_id)}
          {item.pull_request?.number ? ` · PR ${item.pull_request.number}` : ''}
        </div>
        {decision ? <Options item={item} label={label} /> : null}
      </div>
      <div className="card__foot">
        {!decision && item.kind === 'pull_request' ? <PullRequestActions item={item} /> : null}
        {run &&
        !(decision && item.options?.some((o) => o.id === 'open' && !o.url?.startsWith('http'))) ? (
          <Link
            to="/runs/$runId"
            params={{ runId: run }}
            className={buttonClass()}
            aria-label={`${openLabel} ${item.task_id ?? shortId(run)}`}
          >
            {openLabel}
          </Link>
        ) : null}
        {decision ? null : <DismissButton item={item} />}
      </div>
    </article>
  );
}

/**
 * The card in four parts (ADR 0085): what happened, why, the options (below the body), and whose it is. The task's goal
 * moves under the card, collapsed; a question keeps its text and its answer box.
 */
function Decision({
  item,
  headingId,
  answerRef,
}: {
  item: InboxItem;
  headingId: string;
  answerRef?: Ref<HTMLTextAreaElement>;
}) {
  return (
    <>
      <Title id={headingId}>{item.what ?? item.title}</Title>
      {item.why ? <div className="card__why">{item.why}</div> : null}
      {item.kind === 'question' ? <Question item={item} answerRef={answerRef} asked /> : null}
      {item.repository ? (
        <div className="card__note">
          Owner: <span title={item.repository}>{baseName(item.repository)}</span>
        </div>
      ) : null}
      {item.kind !== 'pull_request' ? (
        <details className="card__goal">
          <summary>Goal</summary>
          <div className="card__note">{item.title}</div>
          {item.error?.message ? (
            <div className="card__note">
              <span className="mono">{item.error.code}</span>: {item.error.message}
            </div>
          ) : null}
        </details>
      ) : null}
    </>
  );
}

/** Takes the item out of the inbox for everyone who sees it; the notice offers Undo. */
function DismissButton({ item }: { item: InboxItem }) {
  const toast = useToast();
  const dismiss = useDismiss();
  const label = item.task_id ?? (item.run_id ? shortId(item.run_id) : item.title);
  return (
    <Button
      variant="quiet"
      label="Dismiss"
      aria-label={`Dismiss ${label}`}
      loading={dismiss.isPending}
      onClick={() =>
        dismiss.mutate(
          { ids: [item.id] },
          {
            onSuccess: () =>
              toast.push({
                tone: 'ok',
                icon: 'check',
                title: `Dismissed ${label}`,
                actions: [{ label: 'Undo', onSelect: () => void dismiss.restore([item.id]) }],
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

function Title({ id, children }: { id: string; children: string }) {
  return (
    <h3 id={id} className="card__title">
      {children}
    </h3>
  );
}

function Question({
  item,
  headingId,
  answerRef,
  asked = false,
}: {
  item: InboxItem;
  headingId?: string;
  answerRef?: Ref<HTMLTextAreaElement>;
  /** Under a card that says what happened: every question is a list item, and there is no title or goal line. */
  asked?: boolean;
}) {
  const toast = useToast();
  const answer = useAnswer();
  const [text, setText] = useState('');
  const [sentRun, setSentRun] = useState<string | null | undefined>(undefined);
  const [first, ...rest] = item.questions;
  const sent = sentRun !== undefined;

  function send() {
    if (!item.answer || !text.trim()) return;
    answer.mutate(
      { hint: item.answer, answer: text },
      {
        onSuccess: ({ runId }) => {
          setSentRun(runId);
          toast.push({
            tone: 'ok',
            icon: 'send',
            title: `Answer sent to ${item.task_id}`,
            body: 'A new batch continues the task from its kept work.',
          });
        },
      },
    );
  }

  const listed = asked ? item.questions : rest;
  return (
    <>
      {asked ? null : <Title id={headingId ?? ''}>{first ?? item.title}</Title>}
      {listed.length > 0 ? (
        <ul className="questions">
          {listed.map((q) => (
            <li key={q}>{q}</li>
          ))}
        </ul>
      ) : null}
      {first && !asked ? <div className="card__note">Task: {item.title}</div> : null}
      {item.answer ? (
        sent ? (
          <div className="card__sent" role="status">
            <Icon name="check" />
            <span>Answer sent. A new batch continues the task</span>
            {sentRun ? (
              <Link to="/runs/$runId" params={{ runId: sentRun }} className="mono">
                {shortId(sentRun)}
              </Link>
            ) : null}
          </div>
        ) : (
          <Field
            label="Your answer"
            hint="Starts a new batch that continues this task on its branch (continue_from). The answer goes in as sent."
            error={
              answer.error
                ? `${answer.error.message || 'The server refused the answer.'}`
                : undefined
            }
          >
            {(control) => (
              <>
                <Textarea
                  {...control}
                  ref={answerRef}
                  rows={3}
                  placeholder="Write the answer so it stands alone"
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  onKeyDown={(e) => {
                    if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') send();
                  }}
                />
                <div className="row">
                  <Button
                    variant="primary"
                    label="Answer and continue"
                    disabled={text.trim().length === 0}
                    loading={answer.isPending}
                    onClick={send}
                  />
                </div>
              </>
            )}
          </Field>
        )
      ) : (
        <div className="card__note">This question has no request to answer it; open the task.</div>
      )}
    </>
  );
}

function PullRequest({ item, headingId }: { item: InboxItem; headingId: string }) {
  const pr = item.pull_request;
  return (
    <>
      <Title id={headingId}>{item.title}</Title>
      <div className="card__note">{waitingFor(item.reason)}</div>
      {pr?.test_integrity_flagged ? (
        <Banner tone="warn" icon="alert-triangle" title="test-integrity flagged">
          The change touches only tests or build files. Check that no test was weakened before you
          merge.
        </Banner>
      ) : null}
    </>
  );
}

function PullRequestActions({ item }: { item: InboxItem }) {
  const url = safeHttpUrl(item.pull_request?.url);
  if (!url) return null;
  return (
    <a
      href={url}
      target="_blank"
      rel="noreferrer noopener"
      className={buttonClass({ variant: 'primary' })}
    >
      <Icon name="pr" />
      Open PR {item.pull_request?.number ?? ''}
    </a>
  );
}

function Failure({ item, headingId }: { item: InboxItem; headingId: string }) {
  const code = item.error?.code ?? item.reason;
  return (
    <>
      <Title id={headingId}>{item.title}</Title>
      <div className="card__note">
        {code ? <span className="mono">{code}</span> : null}
        {item.error?.message ? `: ${item.error.message}` : ''}
        {item.error?.action ? ` ${item.error.action}` : ''}
        {!code && !item.error ? 'Ended without a result.' : ''}
      </div>
    </>
  );
}
