import { Mark } from '../../charts/Mark';
import { formatInterval } from '../../charts/stats';
import type { TipData } from '../../charts/Tip';
import { formatCount, formatPercent } from '../../lib/format';
import { SizeBadge } from '../../ui';
import { learnSource, type PassRow } from './model';

const TICKS = [0, 25, 50, 75, 100];
const ROW = 24;
const pct = (fraction: number) => `${(fraction * 100).toFixed(2)}%`;

export function passTip(row: PassRow): TipData {
  const { p } = row;
  return {
    title: `${row.repository} · size ${row.size}`,
    rows: [
      { label: 'pass rate', value: formatPercent(p.rate * 100) },
      { label: 'completed', value: `${formatCount(p.k)} of ${formatCount(p.n)} tasks` },
      { label: '95% Wilson interval', value: formatInterval(p) },
    ],
    source: learnSource(row.group),
  };
}

/** The scale along the top of the rows: 0 to 100 %. The outer labels lean inward so they are not cut off. */
function Axis() {
  return (
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
  );
}

/** One row per repository and size class: the pass rate as a point, its 95 % Wilson interval as a whisker, the counts beside it. */
export function PassRateChart({ rows }: { rows: PassRow[] }) {
  return (
    <ul className="passrate" aria-label="Pass rate by repository and size, with 95% intervals">
      <li className="passrate__row passrate__row--axis" aria-hidden="true">
        <span />
        <Axis />
        <span />
      </li>
      {rows.map((row) => {
        const tip = passTip(row);
        const { p } = row;
        return (
          <li key={row.key} className="passrate__row">
            <span className="passrate__name">
              <SizeBadge size={row.size as 'S' | 'M' | 'L'} />
              <span className="passrate__repo" title={row.repository}>
                {row.repository}
              </span>
            </span>
            <svg className="plot" height={ROW} role="presentation">
              <Mark tip={tip}>
                <rect className="hit" x="0" y="0" width="100%" height={ROW} />
                {TICKS.slice(1, -1).map((t) => (
                  <line key={t} className="gridline" x1={`${t}%`} x2={`${t}%`} y1="0" y2={ROW} />
                ))}
                <line className="whisker" x1={pct(p.lo)} x2={pct(p.hi)} y1={ROW / 2} y2={ROW / 2} />
                <circle className="dot dot--ink" cx={pct(p.rate)} cy={ROW / 2} r="5" />
              </Mark>
            </svg>
            <span className="passrate__value">
              <b>{formatPercent(p.rate * 100)}</b> {formatCount(p.k)}/{formatCount(p.n)}
              <span className="dim"> · CI {formatInterval(p)}</span>
            </span>
          </li>
        );
      })}
    </ul>
  );
}
