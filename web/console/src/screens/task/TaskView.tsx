import { Link } from '@tanstack/react-router';
import { lazy, Suspense, useMemo, useState } from 'react';
import { useAnswer } from '../../api/answer';
import { useBatchTask } from '../../api/batch-task';
import { describeBudgetSource } from '../../lib/budgets';
import {
  baseName,
  DASH,
  formatDuration,
  formatTime,
  formatTokens,
  formatUsd,
  shortId,
} from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import { useToast } from '../../shell/toasts';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Banner,
  Button,
  buttonClass,
  CapMeter,
  DialogClose,
  EmptyState,
  ErrorState,
  Field,
  Icon,
  IconButton,
  KeyValue,
  LogLine,
  Panel,
  SizeBadge,
  Skeleton,
  Sparkline,
  StatusPill,
  Textarea,
} from '../../ui';
import { asRecord, changeTotals, testIntegrityPaths, verificationFacts } from '../batch/artifacts';
import {
  eventDetail,
  eventKind,
  eventTone,
  notesAbout,
  taskShare,
  taskUsd,
  taskWord as wordOf,
} from '../batch/model';
import { NotRecorded } from '../batch/NotRecorded';
import { CodeBlock } from '../code/CodeBlock';
import { answerPreview, answersReady, sessionRows, sessionText, turnsSeries } from './model';
import './task.css';

const DiffView = lazy(() => import('../code/DiffView').then((m) => ({ default: m.DiffView })));

export interface TaskViewProps {
  batchId: string;
  taskId: string;
  /** The drawer has a close button and a link to the page; the page has neither. */
  mode: 'drawer' | 'page';
  onOpenPage?: () => void;
}

/** One task of a batch: what it was asked, what it spent against its budget, how it ended, and the question it is waiting on. */
export function TaskView({ batchId, taskId, mode, onOpenPage }: TaskViewProps) {
  const phone = useIsPhone();
  const query = useBatchTask(batchId, taskId, 500);
  const detail = query.data;
  const task = detail?.task;
  const [showDiff, setShowDiff] = useState(mode === 'page');
  const word = task ? wordOf(task, [task]) : null;
  const events = detail?.timeline.available ? detail.timeline.events : [];
  const turns = useMemo(() => turnsSeries(events), [events]);

  const header = (
    <header className="task__head">
      <div className="row">
        {word ? <StatusPill status={word} size="lg" /> : <Skeleton width="80px" height={20} />}
        {task?.size ? <SizeBadge size={task.size} /> : null}
        <span className="spacer" />
        {mode === 'drawer' ? (
          <>
            {onOpenPage ? (
              <IconButton icon="external" label="Open as a page" onClick={onOpenPage} />
            ) : null}
            <DialogClose />
          </>
        ) : null}
      </div>
      {mode === 'page' ? (
        <h1 className="task__id mono-b">{taskId}</h1>
      ) : (
        <h2 className="task__id mono-b">{taskId}</h2>
      )}
      <div className="row row--wrap dim small">
        {detail?.repository ? (
          <Badge size="s" label={baseName(detail.repository.path)} title={detail.repository.path} />
        ) : null}
        <Link
          to="/runs/$runId"
          params={{ runId: batchId }}
          search={{ tab: 'tasks' }}
          className="link"
        >
          batch {shortId(batchId)}
        </Link>
        {task?.started_at ? (
          <>
            <span>·</span>
            <span>
              {task.finished_at
                ? `${formatDuration((Date.parse(task.finished_at) - Date.parse(task.started_at)) / 1000)} elapsed`
                : `started ${formatTime(task.started_at)}`}
            </span>
          </>
        ) : task ? (
          <>
            <span>·</span>
            <span>not started</span>
          </>
        ) : null}
      </div>
    </header>
  );

  const body = (() => {
    if (query.isPending)
      return (
        <div className="stack" aria-busy="true">
          <Skeleton width="60%" />
          <Skeleton width="95%" />
          <Skeleton width="80%" />
          <Skeleton width="90%" height={64} />
          <Skeleton width="70%" />
        </div>
      );
    if (query.isError && !detail)
      return (
        <ErrorState
          title="Cannot read this task"
          call={`GET /v1/batches/${batchId}/tasks/${taskId}`}
          detail={query.error.message}
          onRetry={() => void query.refetch()}
        />
      );
    if (!detail || !task) return null;
    return (
      <>
        {query.isError ? (
          <ErrorState
            title="Cannot refresh this task"
            call={`GET /v1/batches/${batchId}/tasks/${taskId}`}
            detail={query.error.message}
            onRetry={() => void query.refetch()}
          />
        ) : null}
        <Overview detail={detail} turns={turns} />
        <Question detail={detail} />
        <Changes detail={detail} show={showDiff} onToggle={() => setShowDiff((s) => !s)} />
        <EventsOf detail={detail} wrap={phone} />
        <NotRecorded notes={notesAbout(detail.not_recorded, 'transcript')} />
      </>
    );
  })();

  const prUrl = safeHttpUrl(task?.pr_url ?? task?.pull_request?.url);
  return (
    <div className="task" data-mode={mode}>
      {header}
      <div className="task__body">{body}</div>
      {task ? (
        <footer className="task__foot">
          {prUrl ? (
            <a
              href={prUrl}
              target="_blank"
              rel="noreferrer noopener"
              className={buttonClass({ variant: 'primary', size: phone ? 'touch' : undefined })}
            >
              <Icon name="external" />
              Open PR{task.pull_request?.number ? ` ${task.pull_request.number}` : ''}
            </a>
          ) : null}
          <Link
            to="/runs/$runId"
            params={{ runId: batchId }}
            search={{ tab: 'tasks' }}
            className={buttonClass({ size: phone ? 'touch' : undefined })}
          >
            Open the batch
          </Link>
        </footer>
      ) : null}
    </div>
  );
}

function Overview({
  detail,
  turns,
}: {
  detail: NonNullable<ReturnType<typeof useBatchTask>['data']>;
  turns: number[];
}) {
  const task = detail.task;
  const usd = taskUsd(task);
  const budget = task.budget?.usd ?? null;
  const share = taskShare(task);
  const spend = task.spend;
  const sessions = sessionRows(task, detail.timeline.available ? detail.timeline.events : []);
  const verification = verificationFacts(detail.verification);
  const session = asRecord(detail.session_tests);
  const integrity = Boolean(task.test_integrity_flagged || verification?.flagged);
  const error = detail.result_error ?? task.error;
  const prUrl = safeHttpUrl(task.pr_url ?? task.pull_request?.url);
  const toast = useToast();
  return (
    <>
      {error ? (
        <Banner
          tone={error.code === 'rate_limited' ? 'warn' : 'danger'}
          icon="alert-circle"
          title={error.code}
          role="status"
        >
          {error.message}
          {error.action ? ` ${error.action}` : ''}
        </Banner>
      ) : null}
      <section className="stack stack--tight">
        <div className="label-caps">Goal</div>
        <p className="quote task__goal">{task.goal ?? DASH}</p>
        {detail.summary ? (
          <>
            <div className="label-caps">What the session reported</div>
            <p className="task__summary">{detail.summary}</p>
          </>
        ) : null}
      </section>
      <section className="stack stack--tight">
        <div className="label-caps">Budget and spend</div>
        {budget != null ? (
          <CapMeter
            label={`Spend of the budget of ${task.id}`}
            value={usd ?? 0}
            cap={budget}
            ticks={[85]}
            head={`budget ${formatUsd(budget)}: ${task.budget ? describeBudgetSource(task.budget.source) : 'no budget'}`}
            text={`${formatUsd(usd, true)} of ${formatUsd(budget)} · ${Math.round(share ?? 0)}%`}
          />
        ) : (
          <p className="dim">
            No budget was recorded for this task
            {usd != null ? `; it has spent ${formatUsd(usd, true)}` : ''}.
          </p>
        )}
        <div className="row task__facts">
          {turns.length >= 2 ? (
            <div className="fact">
              <span className="dim small">Turns over time</span>
              <Sparkline
                values={turns}
                width={120}
                height={28}
                label={`turns over time, now ${turns.at(-1)}`}
              />
            </div>
          ) : null}
          <div className="fact">
            <span className="dim small">Tokens</span>
            <span className="b">
              {spend
                ? `${formatTokens(spend.input_tokens)} in · ${formatTokens(spend.output_tokens)} out`
                : task.live?.tokens != null
                  ? formatTokens(task.live.tokens)
                  : DASH}
            </span>
          </div>
          <div className="fact">
            <span className="dim small">Turns</span>
            <span className="b">{spend?.turns ?? task.live?.turns ?? DASH}</span>
          </div>
          {task.live ? (
            <div className="fact">
              <span className="dim small">Now</span>
              <span className="b">{task.live.stage ? `stage ${task.live.stage}` : 'working'}</span>
            </div>
          ) : null}
        </div>
      </section>
      <section className="stack stack--tight">
        <div className="label-caps">Details</div>
        <KeyValue
          ruled
          items={[
            [
              'Depends on',
              task.depends_on.length === 0 ? (
                <span key="d" className="dim">
                  none
                </span>
              ) : (
                <span key="d" className="row row--wrap">
                  {task.depends_on.map((id) => (
                    <Link
                      key={id}
                      to="/runs/$runId/tasks/$taskId"
                      params={{ runId: detail.batch_run_id, taskId: id }}
                      className="link mono"
                    >
                      {id}
                    </Link>
                  ))}
                </span>
              ),
            ],
            [
              'Sessions',
              sessions.length === 0 ? (
                <span key="s" className="dim">
                  none yet
                </span>
              ) : (
                <span key="s" className="stack stack--tight">
                  {sessions.map((s) => (
                    <span key={s.runId} className="row row--wrap">
                      <span className="mono-b">S{s.n}</span>
                      <span className="dim">{sessionText(s)}</span>
                    </span>
                  ))}
                  {spend && spend.continuations > 0 ? (
                    <span className="dim small">
                      {spend.continuations}{' '}
                      {spend.continuations === 1 ? 'continuation' : 'continuations'}; only the last
                      session has a run record.
                    </span>
                  ) : null}
                </span>
              ),
            ],
            [
              'Branch',
              task.branch ? (
                <span key="b" className="row">
                  <span className="mono ch-trunc" title={task.branch}>
                    {task.branch}
                  </span>
                  <IconButton
                    icon="copy"
                    size="sm"
                    label="Copy the branch name"
                    onClick={() =>
                      navigator.clipboard.writeText(task.branch ?? '').then(
                        () =>
                          toast.push({ tone: 'ok', icon: 'check', title: 'Branch name copied' }),
                        () =>
                          toast.push({
                            tone: 'warn',
                            icon: 'alert-triangle',
                            title: 'Cannot copy',
                          }),
                      )
                    }
                  />
                  {task.state === 'failed' || task.state === 'needs_input' ? (
                    <TakeOver batchId={detail.batch_run_id} taskId={task.id} />
                  ) : null}
                </span>
              ) : (
                <span key="b" className="dim">
                  none yet
                </span>
              ),
            ],
            [
              'Pull request',
              task.pull_request ? (
                <span key="p" className="stack stack--tight">
                  <span className="row row--wrap">
                    {prUrl ? (
                      <a href={prUrl} target="_blank" rel="noreferrer noopener">
                        PR {task.pull_request.number ?? ''}
                      </a>
                    ) : (
                      <span>PR {task.pull_request.number ?? ''}</span>
                    )}
                    <Badge
                      size="s"
                      mode={task.pull_request.integrator_mode}
                      label={task.pull_request.integrator_mode}
                    />
                    <span className="dim">{task.pull_request.state.replace('_', ' ')}</span>
                    {task.pull_request.partial ? (
                      <Badge size="s" tone="warn" label="[partial]" />
                    ) : null}
                  </span>
                  {task.pull_request.integrator_last ? (
                    <span className="dim small">{task.pull_request.integrator_last}</span>
                  ) : null}
                </span>
              ) : (
                <span key="p" className="dim">
                  none yet
                </span>
              ),
            ],
          ]}
        />
      </section>
      <section className="stack stack--tight">
        <div className="label-caps">Tests</div>
        <KeyValue
          ruled
          items={[
            [
              'chargehand’s run',
              verification ? (
                <StatusPill
                  key="v"
                  status={verification.passed ? 'done' : 'failed'}
                  label={
                    verification.passed
                      ? 'Passed'
                      : verification.timedOut
                        ? 'Timed out'
                        : `Failed (exit ${verification.exitCode ?? '?'})`
                  }
                  bare
                />
              ) : (
                <span key="v" className="dim">
                  {task.state === 'running'
                    ? 'runs when the session ends'
                    : task.state === 'needs_input'
                      ? 'not run: the session asked a question'
                      : task.state === 'pending' || task.state === 'not_started'
                        ? 'not started'
                        : 'no verification was recorded'}
                </span>
              ),
            ],
            [
              'Session’s claim',
              session ? (
                <span key="c" className="dim">
                  {typeof session.command === 'string' ? session.command : 'tests'}: exit{' '}
                  {String(session.exit_code ?? '?')} (kept apart from chargehand’s run)
                </span>
              ) : (
                <span key="c" className="dim">
                  not reported
                </span>
              ),
            ],
            [
              'Test integrity',
              integrity ? (
                <span key="t">
                  <Badge size="s" tone="warn" label="flagged" />{' '}
                  <span className="dim">
                    {verification?.reason ?? 'The change touches only test or build files.'}
                  </span>
                </span>
              ) : (
                <StatusPill key="t" status="done" label="Not flagged" bare />
              ),
            ],
          ]}
        />
      </section>
    </>
  );
}

/** The CLI command that checks a stalled task's branch out into a local worktree with its handoff and prints how to open a Claude Code session there. */
function TakeOver({ batchId, taskId }: { batchId: string; taskId: string }) {
  const [open, setOpen] = useState(false);
  const toast = useToast();
  const command = `chargehand takeover ${batchId}/${taskId}`;
  return (
    <>
      <Button size="sm" label="Take over" onClick={() => setOpen((v) => !v)} aria-expanded={open} />
      {open ? (
        <span className="row">
          <code className="mono">{command}</code>
          <IconButton
            icon="copy"
            size="sm"
            label="Copy the takeover command"
            onClick={() =>
              navigator.clipboard.writeText(command).then(
                () => toast.push({ tone: 'ok', icon: 'check', title: 'Command copied' }),
                () => toast.push({ tone: 'warn', icon: 'alert-triangle', title: 'Cannot copy' }),
              )
            }
          />
        </span>
      ) : null}
    </>
  );
}

function Question({ detail }: { detail: NonNullable<ReturnType<typeof useBatchTask>['data']> }) {
  const task = detail.task;
  const toast = useToast();
  const answer = useAnswer();
  const [answers, setAnswers] = useState<string[]>([]);
  const [sentRun, setSentRun] = useState<string | null | undefined>(undefined);
  if (task.questions.length === 0) return null;
  const sent = sentRun !== undefined;
  const ready = answersReady(answers, task.questions);
  const preview = answerPreview(detail, answers);
  const send = () => {
    if (!task.answer || !ready) return;
    answer.mutate(
      { hint: task.answer, answer: task.questions.map((_, i) => answers[i] ?? '') },
      {
        onSuccess: ({ runId }) => {
          setSentRun(runId);
          toast.push({
            tone: 'ok',
            icon: 'send',
            title: `Answer sent to ${task.id}`,
            body: 'A new batch continues the task from its kept work.',
          });
        },
      },
    );
  };
  return (
    <section className="stack">
      <div className="label-caps">The session asked</div>
      {task.questions.map((q, i) => (
        <div key={q} className="stack stack--tight">
          <p className="quote task__goal">{q}</p>
          {task.answer && !sent && !task.answered ? (
            <Field label={`Your answer ${i + 1}`}>
              {(control) => (
                <Textarea
                  {...control}
                  rows={2}
                  placeholder="Write the answer so it stands alone"
                  value={answers[i] ?? ''}
                  onChange={(e) =>
                    setAnswers((all) => {
                      const next = [...all];
                      next[i] = e.target.value;
                      return next;
                    })
                  }
                />
              )}
            </Field>
          ) : null}
        </div>
      ))}
      {task.answered ? (
        <div className="card__sent" role="status">
          <Icon name="check" />
          <span>This question was answered in run</span>
          {task.answered_by.map((link) => (
            <Link
              key={link.run_id}
              to="/runs/$runId"
              params={{ runId: link.run_id }}
              className="mono"
              title={link.run_id}
            >
              {shortId(link.run_id)}
            </Link>
          ))}
        </div>
      ) : sent ? (
        <div className="card__sent" role="status">
          <Icon name="check" />
          <span>Answer sent. A new batch continues the task</span>
          {sentRun ? (
            <Link to="/runs/$runId" params={{ runId: sentRun }} className="mono">
              {shortId(sentRun)}
            </Link>
          ) : null}
        </div>
      ) : task.answer ? (
        <>
          <p className="dim small">
            The answers go in as sent, with no pairing to the questions: write each so it stands
            alone. This starts a new batch that continues this task from its kept work (
            <span className="mono">continue_from</span>).
          </p>
          {answer.error ? (
            <div className="ch-error" role="alert">
              {answer.error.message || 'The server refused the answer.'}
            </div>
          ) : null}
          <div className="row">
            <Button
              variant="primary"
              label="Answer and continue"
              disabled={!ready}
              loading={answer.isPending}
              onClick={send}
            />
          </div>
          {preview ? (
            <details>
              <summary className="link">Show the request</summary>
              <CodeBlock code={preview} lang="json" label="The request an answer sends" />
            </details>
          ) : null}
        </>
      ) : (
        <p className="dim small">This question has no request to answer it; open the batch.</p>
      )}
    </section>
  );
}

function Changes({
  detail,
  show,
  onToggle,
}: {
  detail: NonNullable<ReturnType<typeof useBatchTask>['data']>;
  show: boolean;
  onToggle: () => void;
}) {
  const changes = asRecord(detail.changes);
  const totals = changeTotals(detail.changes);
  if (!changes || !totals) return null;
  const diff = typeof changes.diff === 'string' ? changes.diff : null;
  const files = Array.isArray(changes.files)
    ? (changes.files as Array<Record<string, unknown>>)
    : [];
  return (
    <section className="stack stack--tight">
      <div className="row row--wrap">
        <div className="label-caps">Changes</div>
        <span className="dim small">
          {totals.files} {totals.files === 1 ? 'file' : 'files'} · +{totals.added} −{totals.removed}
        </span>
        <span className="spacer" />
        {diff ? (
          <Button
            size="sm"
            label={show ? 'Hide diff' : 'Show diff'}
            aria-expanded={show}
            onClick={onToggle}
          />
        ) : null}
      </div>
      <ul className="task__files list-reset">
        {files.map((f) => (
          <li key={String(f.path)} className="row">
            <span className="mono ch-trunc" title={String(f.path)}>
              {String(f.path)}
            </span>
            <span className="spacer" />
            <span className="small dim">
              {f.binary === true ? 'binary' : `+${String(f.added)} −${String(f.removed)}`}
            </span>
          </li>
        ))}
      </ul>
      {changes.diff_truncated === true ? (
        <p className="dim small">
          The result keeps the first 32 KiB of the diff; the pull request shows the rest.
        </p>
      ) : null}
      {diff && show ? (
        <Suspense fallback={<Skeleton width="100%" height={160} />}>
          <DiffView
            diff={diff}
            label="Changes"
            testPaths={testIntegrityPaths(detail.verification)}
          />
        </Suspense>
      ) : !diff ? (
        <EmptyState
          icon="file"
          title="The diff is not in the result"
          text="The result drops the diff when the file list is long; the pull request has it."
        />
      ) : null}
    </section>
  );
}

function EventsOf({
  detail,
  wrap,
}: {
  detail: NonNullable<ReturnType<typeof useBatchTask>['data']>;
  wrap: boolean;
}) {
  const events = detail.timeline.available ? detail.timeline.events : [];
  return (
    <Panel title="Events of this task" count={events.length} flush>
      {events.length === 0 ? (
        <EmptyState
          icon="activity"
          title={detail.timeline.available ? 'No event for this task' : 'The events are not held'}
          text={
            detail.timeline.available
              ? 'Events appear here as the task’s session works.'
              : 'The server keeps events in memory, for the last 25 finished runs; this task’s are gone.'
          }
        />
      ) : (
        <div
          className="ch-stream task__events"
          // biome-ignore lint/a11y/noNoninteractiveTabindex: a log that scrolls must be reachable by keyboard
          tabIndex={0}
          role="log"
          aria-live="off"
          aria-label="Events of this task"
        >
          {events.map((e) => (
            <LogLine
              key={`${e.at}|${eventKind(e)}|${e.task_id}|${e.detail}`}
              time={formatTime(e.at, { seconds: true })}
              kind={eventKind(e)}
              detail={eventDetail(e)}
              tone={eventTone(e)}
              wrap={wrap}
            />
          ))}
        </div>
      )}
    </Panel>
  );
}
