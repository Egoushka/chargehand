import type { GovernorCount } from '../../api/analytics';
import { Mark } from '../../charts/Mark';
import { formatInterval, type Proportion } from '../../charts/stats';
import type { TipData } from '../../charts/Tip';
import { formatCount, formatPercent } from '../../lib/format';
import { Badge, CapMeter } from '../../ui';
import { ENFORCE_THRESHOLD, type ModeRow } from './model';

const ROW = 40;
const LINE = 28;
const pct = (fraction: number) => `${(fraction * 100).toFixed(2)}%`;

const MODE_NOTE: Record<string, string> = {
  observe: 'a stop the task did not need',
  enforce: 'a task that finished after its continuation',
  off: 'the governor was off',
  none: 'the record has no governor',
};

export function modeTip(row: ModeRow): TipData {
  return {
    title: `Governor mode ${row.mode}`,
    rows: [
      { label: 'false-stop rate', value: formatPercent(row.p.rate * 100) },
      {
        label: 'checkpointed tasks that completed',
        value: `${formatCount(row.completed)} of ${formatCount(row.checkpointed)}`,
      },
      { label: '95% Wilson interval', value: formatInterval(row.p) },
      { label: 'tasks in this mode', value: formatCount(row.tasks) },
    ],
    source: `analytics/v1 tasks.governor.modes[${row.mode}] · checkpointed_completed over checkpointed`,
  };
}

const TICKS = [0, 25, 50, 75, 100];

/** The share of checkpointed tasks that went on to complete, per mode, with its interval and the 5 % line ADR 0045 asks for. */
export function FalseStopChart({ rows }: { rows: ModeRow[] }) {
  return (
    <div className="stack stack--tight">
      {rows.map((row) => {
        const tip = modeTip(row);
        const p: Proportion = row.p;
        return (
          <div key={row.mode} className="falsestop">
            <div className="row row--wrap">
              <Badge
                label={row.mode}
                size="s"
                tone={
                  row.mode === 'enforce' ? 'danger' : row.mode === 'observe' ? 'info' : 'neutral'
                }
              />
              <span className="b">{formatPercent(p.rate * 100)}</span>
              <span className="dim">
                {formatCount(row.completed)} of {formatCount(row.checkpointed)} checkpointed tasks
                completed · CI {formatInterval(p)}
              </span>
            </div>
            <svg className="plot" height={ROW} role="presentation">
              <Mark tip={tip}>
                <rect className="hit" x="0" y="0" width="100%" height={ROW} />
                <line className="baseline" x1="0" x2="100%" y1={LINE} y2={LINE} />
                <line
                  className="rule"
                  x1={pct(ENFORCE_THRESHOLD)}
                  x2={pct(ENFORCE_THRESHOLD)}
                  y1="12"
                  y2={ROW}
                />
                <text x={pct(ENFORCE_THRESHOLD)} y="8">
                  5% to enforce
                </text>
                <line className="whisker" x1={pct(p.lo)} x2={pct(p.hi)} y1={LINE} y2={LINE} />
                <circle className="dot dot--ink" cx={pct(p.rate)} cy={LINE} r="5" />
              </Mark>
            </svg>
            <div className="dim small">{MODE_NOTE[row.mode] ?? ''}</div>
          </div>
        );
      })}
      <svg className="plot" height="16" aria-hidden="true">
        {TICKS.map((t) => (
          <text
            key={t}
            x={`${t}%`}
            y="11"
            textAnchor={t === 0 ? 'start' : t === 100 ? 'end' : 'middle'}
          >
            {t}%
          </text>
        ))}
      </svg>
    </div>
  );
}

/** What the governor decided, most frequent first: a signal, the rung it reached, how often, and how often that ended a session. */
export function DecisionBars({ decisions }: { decisions: GovernorCount[] }) {
  const peak = Math.max(1, ...decisions.map((d) => d.count));
  return (
    <div className="stack stack--tight">
      <div className="label-caps">Decisions</div>
      {decisions.map((d) => (
        <CapMeter
          key={`${d.signal}/${d.action}`}
          label={`${d.signal}, ${d.action}`}
          value={d.count}
          cap={peak}
          level="info"
          text={`${formatCount(d.count)} · ${formatCount(d.acted)} acted`}
          head={
            <span>
              <span className="meter-name">{d.signal}</span> <span className="dim">{d.action}</span>
            </span>
          }
        />
      ))}
    </div>
  );
}
