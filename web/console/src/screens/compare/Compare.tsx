import { useQueries } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { lazy, Suspense } from 'react';
import { batchTaskQuery } from '../../api/batch-task';
import { Badge, EmptyState, ErrorState, KeyValue, Skeleton } from '../../ui';
import { testIntegrityPaths } from '../batch/artifacts';
import './compare.css';
import { parseRef, type QualityCard, qualityCard, refText, type TaskRef, verdict } from './model';

const DiffView = lazy(() => import('../code/DiffView').then((m) => ({ default: m.DiffView })));

/** `/compare?a=<batch>/<task>&b=<batch>/<task>`: two tasks on one goal, side by side. Agent text is shown as text. */
export function Compare() {
  const search = useSearch({ from: '/compare' });
  const a = parseRef(search.a);
  const b = parseRef(search.b);
  if (!a || !b)
    return (
      <div className="screen">
        <EmptyState
          icon="file"
          title="Pick two tasks to compare"
          text="Open this screen with both tasks in the address: compare?a=<batch>/<task>&b=<batch>/<task>."
        />
      </div>
    );
  return <Pair a={a} b={b} />;
}

function Pair({ a, b }: { a: TaskRef; b: TaskRef }) {
  const queries = useQueries({
    queries: [batchTaskQuery(a.batch, a.task), batchTaskQuery(b.batch, b.task)],
  });
  const [qa, qb] = queries;
  const cards = qa?.data && qb?.data ? [qualityCard(qa.data), qualityCard(qb.data)] : null;
  const result = cards?.[0] && cards[1] ? verdict(cards[0], cards[1]) : null;
  return (
    <div className="screen">
      <h1 className="compare__title">Compare two tasks</h1>
      {result ? (
        <p className="compare__verdict" role="status">
          {result.reason}
        </p>
      ) : null}
      <div className="compare__grid">
        {[a, b].map((ref, i) => {
          const q = queries[i];
          const side = i === 0 ? 'a' : 'b';
          const card = cards?.[i];
          return (
            <section
              key={side}
              className="card compare__side"
              aria-label={`Task ${side.toUpperCase()}`}
            >
              <header className="row row--wrap">
                <span className="label-caps">Task {side.toUpperCase()}</span>
                <Link
                  className="mono ch-trunc"
                  to="/runs/$runId/tasks/$taskId"
                  params={{ runId: ref.batch, taskId: ref.task }}
                >
                  {refText(ref)}
                </Link>
                <span className="spacer" />
                {result?.winner === side ? <Badge size="s" tone="ok" label="Stronger" /> : null}
              </header>
              {q?.isPending ? (
                <Skeleton width="100%" height={120} />
              ) : q?.isError ? (
                <ErrorState
                  title="Cannot read this task"
                  call={`GET /v1/batches/${ref.batch}/tasks/${ref.task}`}
                  detail={q.error.message}
                  onRetry={() => void q.refetch()}
                />
              ) : q?.data && card ? (
                <Card
                  card={card}
                  goal={q.data.task.goal}
                  testPaths={testIntegrityPaths(q.data.verification)}
                  label={refText(ref)}
                />
              ) : null}
            </section>
          );
        })}
      </div>
    </div>
  );
}

function Card({
  card,
  goal,
  testPaths,
  label,
}: {
  card: QualityCard;
  goal?: string | null;
  testPaths: string[];
  label: string;
}) {
  return (
    <div className="stack">
      {goal ? <p className="compare__goal">{goal}</p> : null}
      <KeyValue
        items={[
          ['State', card.state],
          ['Verification', card.verification === 'none' ? 'not recorded' : card.verification],
          ['Test integrity', card.flagged ? 'flagged' : 'not flagged'],
          ['Change', `${card.files} files · +${card.added} −${card.removed}`],
        ]}
      />
      {card.diff ? (
        <Suspense fallback={<Skeleton width="100%" height={160} />}>
          <DiffView diff={card.diff} label={`Changes of ${label}`} testPaths={testPaths} />
        </Suspense>
      ) : (
        <EmptyState icon="file" title="No diff in the result" text="The pull request has it." />
      )}
    </div>
  );
}
