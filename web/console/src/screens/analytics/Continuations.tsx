import type { AnalyticsBody } from '../../api/analytics';
import { formatInterval, type Proportion } from '../../charts/stats';
import { TightEmpty } from '../../charts/TightEmpty';
import { formatCount, formatPercent } from '../../lib/format';
import { CapMeter, Sparkline } from '../../ui';
import { continuationView } from './model';

const rateText = (p: Proportion | null) =>
  p
    ? `${formatPercent(p.rate * 100)} · ${formatCount(p.k)} of ${formatCount(p.n)} · CI ${formatInterval(p)}`
    : 'no task';

/** What came of tasks that went on in a fresh session after one was stopped, and the CI fixer, which the server does not record. */
export function Continuations({
  body,
  values,
  note,
}: {
  body: AnalyticsBody;
  values: number[];
  note: string;
}) {
  const c = continuationView(body);
  return (
    <div className="stack">
      <div className="stack stack--tight">
        <div className="label-caps">Tasks that went on in a fresh session</div>
        {c.withCount === 0 ? (
          <p className="dim">No task needed a continuation in this range.</p>
        ) : (
          <>
            <CapMeter
              label="Completed after a continuation"
              value={(c.withRate?.rate ?? 0) * 100}
              cap={100}
              level="info"
              text={rateText(c.withRate)}
              head="With a continuation"
            />
            <CapMeter
              label="Completed without a continuation"
              value={(c.withoutRate?.rate ?? 0) * 100}
              cap={100}
              level="info"
              text={rateText(c.withoutRate)}
              head="Without one"
            />
            <div className="row row--wrap dim small">
              <span>
                {formatCount(c.sessions)} continuation sessions over {formatCount(c.withCount)}{' '}
                tasks
              </span>
              {values.length > 1 ? (
                <Sparkline
                  values={values}
                  tone="neutral"
                  label="Continuation sessions per bucket"
                />
              ) : null}
            </div>
          </>
        )}
        <div className="dim small">
          analytics/v1 tasks.continuations · completion_rate, completion_rate_without
        </div>
      </div>
      <div className="stack stack--tight">
        <div className="label-caps">CI fixer, by attempt</div>
        <TightEmpty icon="info" title="Not recorded yet" text={note} />
      </div>
    </div>
  );
}
