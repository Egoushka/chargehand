import type { Tone } from '../lib/status';

/** SVG path data for a series: a line, a closed area under it, and the last point. */
export function sparkPath(values: number[], width: number, height: number, pad = 2) {
  const n = values.length;
  if (n === 0) return { line: '', area: '', last: [0, 0] as [number, number] };
  const lo = Math.min(...values);
  const hi = Math.max(...values);
  const span = hi - lo || 1;
  const points = values.map((v, i): [number, number] => [
    n === 1 ? width / 2 : pad + (i * (width - 2 * pad)) / (n - 1),
    height - pad - ((v - lo) / span) * (height - 2 * pad),
  ]);
  const line = points
    .map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`)
    .join('');
  const first = points[0] as [number, number];
  const last = points[n - 1] as [number, number];
  const area = `${line}L${last[0].toFixed(1)} ${height}L${first[0].toFixed(1)} ${height}Z`;
  return { line, area, last };
}

export interface SparklineProps {
  values: number[];
  width?: number;
  height?: number;
  /** `info` for live activity (default), `neutral` for totals, `accent` only for a "needs you" series. */
  tone?: Tone;
  /** Names the series and its latest value. A sparkline alone is not readable data: put the number beside it. */
  label?: string;
}

/** A trend line with no axes, drawn inline so the page's policy needs nothing. */
export function Sparkline({
  values,
  width = 96,
  height = 24,
  tone,
  label = 'trend',
}: SparklineProps) {
  const { line, area, last } = sparkPath(values, width, height);
  return (
    <svg
      className="ch-spark"
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      data-tone={tone}
      role="img"
      aria-label={label}
    >
      <path className="area" d={area} />
      <path className="line" d={line} />
      <circle className="dot" cx={last[0]} cy={last[1]} r={2.5} />
    </svg>
  );
}
