import { lazy, Suspense, useState } from 'react';
import type { ArtifactRef, BatchBody } from '../../api/batch';
import { useBatchTask } from '../../api/batch-task';
import { DASH, formatDuration } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  EmptyState,
  ErrorState,
  Icon,
  KeyValue,
  Panel,
  Select,
  Skeleton,
  StatusPill,
} from '../../ui';
import { CodeBlock } from '../code/CodeBlock';
import {
  type ArtifactItem,
  asRecord,
  batchArtifacts,
  changeTotals,
  taskArtifacts,
  testIntegrityPaths,
  verificationFacts,
} from './artifacts';
import { notesAbout } from './model';
import { NotRecorded } from './NotRecorded';

// The diff viewer and its syntax colour are loaded when a diff is opened.
const DiffView = lazy(() => import('../code/DiffView').then((m) => ({ default: m.DiffView })));

const BATCH = '__batch';

/** What a batch and each of its tasks produced: the diff, the verification run, the governor log reference, the result. */
export function ArtifactsTab({
  batch,
  initialTask,
}: {
  batch: BatchBody;
  initialTask?: string | null;
}) {
  const phone = useIsPhone();
  const [scope, setScope] = useState(initialTask ?? BATCH);
  const [picked, setPicked] = useState<string | null>(null);
  const taskId = scope === BATCH ? null : scope;
  const detail = useBatchTask(batch.run.run_id, taskId ?? '', 0, taskId != null);
  const items =
    taskId == null ? batchArtifacts(batch) : detail.data ? taskArtifacts(detail.data) : [];
  const current = items.find((i) => i.id === picked) ?? items[0];
  const loading = taskId != null && detail.isPending;
  return (
    <div className="artifacts" data-phone={phone || undefined}>
      <Panel
        title="Artifacts"
        flush
        aside={
          <Select
            aria-label="Artifacts of"
            value={scope}
            touch={phone}
            onChange={(e) => {
              setScope(e.target.value);
              setPicked(null);
            }}
          >
            <option value={BATCH}>The batch</option>
            {batch.tasks.map((t) => (
              <option key={t.id} value={t.id}>
                {t.id}
              </option>
            ))}
          </Select>
        }
      >
        {loading ? (
          <div className="pad stack stack--tight" aria-busy="true">
            <Skeleton width="70%" />
            <Skeleton width="50%" />
            <Skeleton width="60%" />
          </div>
        ) : detail.isError && taskId ? (
          <div className="pad">
            <ErrorState
              title="Cannot read the task"
              call={`GET /v1/batches/${batch.run.run_id}/tasks/${taskId}`}
              detail={detail.error.message}
              onRetry={() => void detail.refetch()}
            />
          </div>
        ) : items.length === 0 ? (
          <EmptyState
            icon="box"
            title="No artifact yet"
            text={
              taskId
                ? 'A task keeps artifacts when its session ends: the branch, the changes, the verification run.'
                : 'The batch result is written when the batch ends.'
            }
          />
        ) : (
          <ul className="list-reset artifact-list">
            {items.map((item) => (
              <li key={item.id}>
                <button
                  type="button"
                  className="li li--click artifact-list__item"
                  aria-pressed={item.id === current?.id}
                  onClick={() => setPicked(item.id)}
                >
                  <Icon name={item.icon} />
                  <span className="li__main">
                    <span>{item.name}</span>
                    <span className="li__meta">{item.meta}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      <div className="stack">
        {current ? <Viewer item={current} detail={detail.data} /> : null}
        <NotRecorded notes={notesAbout(batch.not_recorded, 'transcript', 'governor')} />
      </div>
    </div>
  );
}

function Viewer({
  item,
  detail,
}: {
  item: ArtifactItem;
  detail: ReturnType<typeof useBatchTask>['data'];
}) {
  if (item.view === 'changes')
    return <ChangesView changes={item.value} verification={detail?.verification} />;
  if (item.view === 'verification') return <VerificationView value={item.value} />;
  if (item.view === 'json')
    return (
      <Panel title={item.name} aside={<span className="dim">{item.meta}</span>}>
        <CodeBlock
          code={JSON.stringify(item.value, null, 2)}
          lang="json"
          label={`${item.name} as JSON`}
        />
      </Panel>
    );
  return item.ref ? <RefView item={item} ref_={item.ref} /> : null;
}

function ChangesView({ changes, verification }: { changes: unknown; verification: unknown }) {
  const c = asRecord(changes);
  const totals = changeTotals(changes);
  const diff = typeof c?.diff === 'string' ? c.diff : null;
  const paths = testIntegrityPaths(verification);
  const files = Array.isArray(c?.files) ? (c.files as Array<Record<string, unknown>>) : [];
  return (
    <div className="stack stack--tight">
      <div className="row row--wrap">
        <span className="b">
          {totals?.files ?? 0} {totals?.files === 1 ? 'file' : 'files'} changed
        </span>
        <span className="dim">
          +{totals?.added ?? 0} −{totals?.removed ?? 0}
          {typeof c?.base === 'string' ? ` · base ${c.base.slice(0, 7)}` : ''}
          {typeof c?.commit === 'string' ? ` · commit ${c.commit.slice(0, 7)}` : ''}
        </span>
        <span className="spacer" />
        {paths.length > 0 ? (
          <Badge
            size="s"
            tone="warn"
            label={`${paths.length} test or build ${paths.length === 1 ? 'file' : 'files'}`}
          />
        ) : null}
      </div>
      {c?.diff_truncated === true ? (
        <p className="dim small">
          The result keeps the first 32 KiB of the diff; the pull request shows the rest.
          {files.length > 0
            ? ` Files changed: ${files.map((f) => String(f.path)).join(', ')}.`
            : ''}
        </p>
      ) : null}
      {diff ? (
        <Suspense fallback={<Skeleton width="100%" height={160} />}>
          <DiffView diff={diff} label="Changes" testPaths={paths} />
        </Suspense>
      ) : (
        <EmptyState
          icon="file"
          title="The diff is not in the result"
          text="The result keeps the diff first and drops it when the file list is long; open the pull request to read it."
        />
      )}
    </div>
  );
}

function VerificationView({ value }: { value: unknown }) {
  const v = verificationFacts(value);
  if (!v) return null;
  return (
    <Panel
      title="Verification"
      aside={<span className="dim">chargehand's own run, in a fresh container</span>}
    >
      <div className="stack stack--tight">
        <KeyValue
          ruled
          items={[
            ['Command', v.command || DASH, true],
            [
              'Result',
              <StatusPill
                key="r"
                status={v.passed ? 'done' : 'failed'}
                label={
                  v.passed
                    ? 'Passed'
                    : v.timedOut
                      ? 'Timed out'
                      : `Failed (exit ${v.exitCode ?? '?'})`
                }
                bare
              />,
            ],
            ['Took', v.seconds != null ? formatDuration(v.seconds) : DASH],
            [
              'Where',
              [v.sandbox, v.network ? `network ${v.network}` : null].filter(Boolean).join(', ') ||
                DASH,
            ],
            [
              'Test integrity',
              v.flagged ? (
                <span key="t">
                  <Badge size="s" tone="warn" label="flagged" />{' '}
                  <span className="dim">{v.reason}</span>
                </span>
              ) : (
                <StatusPill key="t" status="done" label="Not flagged" bare />
              ),
            ],
          ]}
        />
        {v.tail ? (
          <CodeBlock code={v.tail} lang="text" label="End of the verification output" />
        ) : null}
      </div>
    </Panel>
  );
}

function RefView({ item, ref_ }: { item: ArtifactItem; ref_: ArtifactRef }) {
  return (
    <Panel title={item.name} aside={<span className="dim">a reference, not the content</span>}>
      <div className="stack stack--tight">
        <KeyValue
          ruled
          items={[
            ['Kind', ref_.kind, true],
            ['Media type', ref_.media_type, true],
            ['sha256', ref_.sha256, true],
            ['Stored', ref_.uri ? `${ref_.uri}` : 'in the result'],
          ]}
        />
        <p className="dim small">
          {ref_.kind === 'governor-log'
            ? 'The governor log is written to the session’s output volume and referenced here with its hash; it is never inline, and the server does not serve it.'
            : ref_.uri
              ? 'The content lives outside the result and the server does not serve it.'
              : 'The server serves this artifact’s content only for the kinds shown with a view.'}
        </p>
      </div>
    </Panel>
  );
}
