import { Mark } from '../../charts/Mark';
import type { Bin } from '../../charts/scales';
import { niceCeil } from '../../charts/scales';
import type { TipData } from '../../charts/Tip';
import { cssVars } from '../../ui';
import { type Durations, durationBinLabel } from './model';

const HEIGHT = 128;
const TOP = 18;
const BAR = 24;

export function durationTip(bin: Bin, total: number): TipData {
  return {
    title: `Finished batches that took ${durationBinLabel(bin)}`,
    rows: [
      { label: 'batches', value: String(bin.count) },
      { label: 'share', value: total > 0 ? `${Math.round((bin.count / total) * 100)}%` : '0%' },
    ],
    source: 'GET /v1/runs?kind=batch · started_at to finished_at',
  };
}

/** A column per duration bin, one hue (the bins are ordered, not categories): the count sits on the cap. */
export function DurationChart({ durations }: { durations: Durations }) {
  const { bins, n } = durations;
  const peak = niceCeil(Math.max(...bins.map((b) => b.count)));
  const slot = 100 / bins.length;
  return (
    <div className="hist">
      <svg className="plot" height={HEIGHT} role="presentation">
        <line className="baseline" x1="0" x2="100%" y1={HEIGHT} y2={HEIGHT} />
        {bins.map((bin, i) => {
          const h = (bin.count / peak) * (HEIGHT - TOP);
          const centre = `${((i + 0.5) * slot).toFixed(3)}%`;
          const tip = durationTip(bin, n);
          return (
            <Mark key={bin.from} tip={tip} slot={1}>
              <rect
                className="hit"
                x={`${(i * slot).toFixed(3)}%`}
                y="0"
                width={`${slot}%`}
                height={HEIGHT}
              />
              {bin.count > 0 ? (
                <>
                  <rect
                    className="bar"
                    x={centre}
                    y={HEIGHT - h}
                    width={BAR}
                    height={h + 4}
                    rx="4"
                    transform={`translate(${-BAR / 2} 0)`}
                  />
                  <text x={centre} y={HEIGHT - h - 5} textAnchor="middle">
                    {bin.count}
                  </text>
                </>
              ) : null}
            </Mark>
          );
        })}
      </svg>
      <div className="hist__labels" style={cssVars({ '--n': bins.length })} aria-hidden="true">
        {bins.map((bin) => (
          <span key={bin.from}>{durationBinLabel(bin)}</span>
        ))}
      </div>
    </div>
  );
}
