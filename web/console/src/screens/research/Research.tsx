import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { runQuery, useCancelRuns } from '../../api/queries';
import type { RunResult, RunSummary } from '../../api/types';
import { useRunEvents } from '../../api/useRunEvents';
import {
  baseName,
  formatDateTime,
  formatDuration,
  formatTime,
  formatTokens,
  formatUsd,
} from '../../lib/format';
import { runStatus } from '../../lib/status';
import { useToast } from '../../shell/toasts';
import {
  Badge,
  Button,
  ConfirmDialog,
  cssVars,
  EmptyState,
  ErrorState,
  Field,
  Icon,
  Input,
  KeyValue,
  Kpi,
  LogLine,
  Panel,
  Skeleton,
  StatusPill,
} from '../../ui';
import { eventDetail, eventKind } from '../batch/model';
import { Artifacts, runArtifacts } from './Artifacts';
import {
  type ClaimRow,
  claimRows,
  cleanWeb,
  codeHref,
  confidenceText,
  readWeb,
  supportedCount,
  supportOf,
  webFromPullRequest,
  writeWeb,
} from './model';
import './research.css';

/** A research run's result: the claims it makes with the lines of code they cite at the commit, how sure it is, what it left open, and who signed it. */
export function Research({ run }: { run: RunSummary }) {
  const reply = useQuery(runQuery(run.run_id));
  const toast = useToast();
  const cancel = useCancelRuns();
  const [confirming, setConfirming] = useState(false);
  const data = reply.data;
  const result = data?.state === 'finished' ? data.result : null;
  const word = result ? runStatus(result.status) : data?.state === 'lost' ? 'lost' : 'running';
  const rows = result ? claimRows(result) : [];
  const took =
    run.finished_at && run.started_at
      ? formatDuration((Date.parse(run.finished_at) - Date.parse(run.started_at)) / 1000)
      : null;

  return (
    <div className="screen">
      <div className="screen__head">
        <div className="stack stack--tight head-main">
          <div className="row row--wrap">
            <StatusPill
              status={result ? (run.error_code === 'cancelled' ? 'cancelled' : word) : word}
              size="lg"
            />
            <h1 className="screen__title mono-b batch-id">{run.run_id}</h1>
          </div>
          <div className="row row--wrap dim">
            {run.repository ? (
              <Badge label={baseName(run.repository)} title={run.repository} />
            ) : null}
            {run.commit ? <span className="mono">@{run.commit.slice(0, 7)}</span> : null}
            <span>·</span>
            <span>preset {run.preset}</span>
            {run.actor ? (
              <>
                <span>·</span>
                <span>account {run.actor}</span>
              </>
            ) : null}
            <span>·</span>
            <span title={formatDateTime(run.started_at)}>
              {formatTime(run.started_at)}
              {took ? `, ${took}` : ''}
            </span>
          </div>
        </div>
        <div className="screen__actions">
          {data?.state === 'running' ? (
            <Button
              variant="danger-quiet"
              label="Cancel run"
              loading={cancel.isPending}
              onClick={() => setConfirming(true)}
            />
          ) : null}
          {result ? (
            <Button
              size="sm"
              icon="copy"
              label="Copy result JSON"
              onClick={() =>
                navigator.clipboard.writeText(JSON.stringify(result, null, 2)).then(
                  () => toast.push({ tone: 'ok', icon: 'check', title: 'Result copied' }),
                  () =>
                    toast.push({
                      tone: 'warn',
                      icon: 'alert-triangle',
                      title: 'Cannot copy',
                      body: 'The browser refused clipboard access.',
                    }),
                )
              }
            />
          ) : null}
        </div>
      </div>

      {reply.isError ? (
        <ErrorState
          title="Cannot read the run"
          call={`GET /v1/runs/${run.run_id}`}
          detail={reply.error.message}
          onRetry={() => void reply.refetch()}
        />
      ) : null}
      {reply.isPending ? <Loading /> : null}
      {data?.state === 'lost' ? (
        <Panel title="Lost">
          <EmptyState
            icon="alert-triangle"
            title="The process that ran it ended first"
            text="The run did not finish, so there is no result to show. Run it again from Start a batch."
          />
        </Panel>
      ) : null}
      {data?.state === 'running' ? <Running runId={run.run_id} /> : null}
      {result ? <Result result={result} rows={rows} run={run} /> : null}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Cancel ${run.run_id.slice(-6)}?`}
        text="The run stops and ends failed with the code cancelled."
        keepLabel="Keep running"
        confirmLabel="Cancel run"
        onConfirm={() => {
          setConfirming(false);
          cancel.mutate([run.run_id], {
            onSuccess: ([outcome]) =>
              toast.push(
                outcome?.result === 'cancelling'
                  ? { tone: 'ok', icon: 'check', title: 'Cancelling the run' }
                  : {
                      tone: 'warn',
                      icon: 'alert-triangle',
                      title: 'The run was not cancelled',
                      body:
                        outcome?.result === 'finished'
                          ? 'It had finished, or another process runs it.'
                          : (outcome?.message ?? 'The server could not cancel it.'),
                    },
              ),
            onError: (error) =>
              toast.push({
                tone: 'danger',
                icon: 'alert-triangle',
                title: 'Cannot cancel',
                body: error.message,
              }),
          });
        }}
      />
    </div>
  );
}

function Loading() {
  return (
    <div className="g-split" aria-busy="true">
      <div className="stack">
        <Panel title="Summary">
          <div className="stack stack--tight">
            <Skeleton width="30%" />
            <Skeleton width="95%" />
            <Skeleton width="80%" />
          </div>
        </Panel>
        <Panel title="Claims">
          <div className="stack stack--tight">
            <Skeleton width="60%" />
            <Skeleton width="100%" height={6} />
            <Skeleton width="90%" />
          </div>
        </Panel>
      </div>
      <Panel title="Confidence">
        <div className="stack stack--tight">
          <Skeleton width="50%" />
          <Skeleton width="70%" />
        </div>
      </Panel>
    </div>
  );
}

function Running({ runId }: { runId: string }) {
  const live = useRunEvents(runId);
  return (
    <div className="stack">
      <Panel title="No result yet">
        <EmptyState
          icon="clock"
          title="The workers are still reading"
          text="The result appears here when the run finishes, with every claim cited against the commit."
        />
      </Panel>
      {live.events.length > 0 ? (
        <Panel title="Events" count={live.events.length} flush>
          <div
            className="ch-stream"
            role="log"
            aria-live="off"
            aria-label="Events of this run"
            // biome-ignore lint/a11y/noNoninteractiveTabindex: a log that scrolls must be reachable by keyboard
            tabIndex={0}
          >
            {live.events.map(({ id, data }) => (
              <LogLine
                key={id}
                time={formatTime(data.at, { seconds: true })}
                kind={eventKind(data)}
                task={data.task_id}
                detail={eventDetail(data)}
              />
            ))}
          </div>
        </Panel>
      ) : null}
    </div>
  );
}

function Result({ result, rows, run }: { result: RunResult; rows: ClaimRow[]; run: RunSummary }) {
  const [web, setWeb] = useState<string | null>(
    () => readWeb(run.repository) ?? webFromPullRequest(run.pr_url),
  );
  const [draft, setDraft] = useState(web ?? '');
  const bad = draft.trim() !== '' && cleanWeb(draft) === null;
  const hasSupport = rows.some((r) => r.claim.support);
  const failed = result.status === 'failed' || result.status === 'denied';
  return (
    <>
      {result.error ? (
        <div className="errbox" role="alert">
          <Icon name="alert-triangle" />
          <div className="stack stack--tight">
            <div className="b">The run {result.status === 'denied' ? 'was denied' : 'failed'}</div>
            <div className="dim">
              <span className="mono">{result.error.code}</span>: {result.error.message}
              {result.error.retryable ? ' Retryable.' : ''}
              {result.error.action ? (
                <>
                  {' '}
                  <b className="action">Action:</b> {result.error.action}
                </>
              ) : null}
            </div>
          </div>
        </div>
      ) : null}
      <div className="g-split">
        <div className="stack">
          <Panel title="Summary">
            <p className="research__summary">{result.summary || '—'}</p>
          </Panel>
          <Panel
            title="Claims"
            count={rows.length}
            flush
            aside={
              <span className="dim">
                {hasSupport ? 'support check by a second model' : 'no support check ran'}
              </span>
            }
          >
            {rows.length === 0 ? (
              <EmptyState
                icon="list"
                title={failed ? 'No claims: the run did not finish' : 'The result makes no claim'}
                text={
                  failed
                    ? 'A failed run returns no claims. The error above says why.'
                    : 'Open questions below say what the run could not support with evidence.'
                }
              />
            ) : (
              <ul className="list-reset">
                {rows.map((row) => (
                  <ClaimItem key={row.claim.text} row={row} web={web} commit={run.commit} />
                ))}
              </ul>
            )}
          </Panel>
        </div>
        <div className="stack">
          <Panel title="Confidence">
            <div className="stack stack--tight">
              <Kpi
                label="Overall"
                value={confidenceText(result.confidence)}
                sub="as the run reports it"
              />
              <KeyValue
                stacked
                items={[
                  [
                    'Usage',
                    `${formatTokens(result.usage.input + result.usage.output)} tokens · ${formatUsd(result.usage.usd)}`,
                  ],
                  [
                    'Supported claims',
                    hasSupport ? supportedCount(rows.map((r) => r.claim)) : 'no check ran',
                  ],
                ]}
              />
            </div>
          </Panel>
          <Panel title="Open questions" count={result.open_questions?.length ?? 0} flush>
            {(result.open_questions ?? []).length === 0 ? (
              <p className="dim pad">Nothing is left open.</p>
            ) : (
              <ul className="list-reset">
                {(result.open_questions ?? []).map((q) => (
                  <li key={q} className="li open-q">
                    <Icon name="help" />
                    <span>{q}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
          <Signature result={result} />
          <Panel title="Links to code">
            <form
              className="stack stack--tight"
              onSubmit={(e) => {
                e.preventDefault();
                const next = cleanWeb(draft);
                if (run.repository && (next || draft.trim() === '')) writeWeb(run.repository, next);
                if (next || draft.trim() === '') setWeb(next);
              }}
            >
              <Field
                label="Repository web address"
                hint="Typed once and kept in this browser, by repository. Not a secret. Used to link each citation to its file and lines at the commit."
                error={bad ? 'Use a web address that starts with https:// or http://.' : undefined}
              >
                {(control) => (
                  <Input
                    {...control}
                    mono
                    placeholder="https://github.com/owner/name"
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                  />
                )}
              </Field>
              <div className="row">
                <Button label="Use this address" type="submit" size="sm" disabled={bad} />
              </div>
            </form>
          </Panel>
        </div>
      </div>
      {runArtifacts(result).length > 0 ? <Artifacts artifacts={runArtifacts(result)} /> : null}
    </>
  );
}

function Signature({ result }: { result: RunResult }) {
  const sig = result.signature;
  return (
    <Panel
      title="Signature"
      aside={sig ? <StatusPill status="queued" label="Signed, not checked here" bare /> : undefined}
    >
      {sig ? (
        <div className="stack stack--tight">
          <KeyValue
            stacked
            items={[
              ['Algorithm', sig.alg, true],
              ['Key id', sig.key_id, true],
              [
                'Value',
                <span key="v" className="mono ch-trunc research__sig" title={sig.value}>
                  {sig.value}
                </span>,
              ],
            ]}
          />
          <p className="dim small">
            The signature says who produced this JSON, not that it is true. The console does not
            check it: it has no public key to check it with.
          </p>
        </div>
      ) : (
        <p className="dim">This result carries no signature.</p>
      )}
    </Panel>
  );
}

function ClaimItem({
  row,
  web,
  commit,
}: {
  row: ClaimRow;
  web: string | null;
  commit: string | null | undefined;
}) {
  const support = supportOf(row.claim);
  const pct = Math.round(row.claim.confidence * 100);
  return (
    <li className="li claim">
      <div className="row row--wrap">
        <StatusPill status={support.status} label={support.label} bare />
        <span className="spacer" />
        <div className="ch-meter ch-meter--sm claim__conf">
          <div className="ch-meter__head">
            <span>confidence</span>
            <span className="ch-meter__val">{confidenceText(row.claim.confidence)}</span>
          </div>
          <div className="ch-meter__track" aria-hidden="true">
            <div className="ch-meter__fill" style={cssVars({ '--p': pct })} />
          </div>
        </div>
      </div>
      <div className="claim__text">{row.claim.text}</div>
      <div className="row row--wrap">
        {row.evidence.map((e) => {
          const href = codeHref(web, commit, e);
          const title = href
            ? `Opens ${e.locator}${e.commit || commit ? ` at ${(e.commit ?? commit ?? '').slice(0, 7)}` : ''}`
            : e.kind === 'file'
              ? 'Add the repository web address to link this citation'
              : e.kind;
          return href ? (
            <a
              key={e.id}
              href={href}
              target="_blank"
              rel="noreferrer noopener"
              className="ch-badge ch-badge--mono cite"
              title={title}
            >
              <Icon name="external" />
              {e.locator}
            </a>
          ) : (
            <span key={e.id} className="ch-badge ch-badge--mono cite" title={title}>
              {e.locator}
            </span>
          );
        })}
        {row.missing.map((id) => (
          <span
            key={id}
            className="ch-badge ch-badge--mono"
            data-tone="danger"
            title="The result lists no such evidence"
          >
            {id}: missing
          </span>
        ))}
      </div>
    </li>
  );
}
