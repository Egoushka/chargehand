import { useEffect, useRef, useState } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { cssVars, cx } from '../ui';
import { type ChartColors, type Slot, useChartColors } from './colors';
import { labelIndices, niceTicks } from './scales';
import { TipBody, type TipData } from './Tip';
import { useWidth } from './useWidth';
import './charts.css';

export interface TimeSeries {
  id: string;
  label: string;
  slot: Slot;
  /** One value per bucket; null is a gap (a line breaks, a bar is absent). */
  values: Array<number | null>;
}

export interface TimeChartProps {
  /** One label per bucket, in time order: the x axis. */
  labels: string[];
  /** Stacked bars draw the first series at the bottom. */
  series: TimeSeries[];
  mode: 'stacked-bars' | 'line';
  /** Axis ticks and values. */
  format: (value: number) => string;
  /** Pins the top of the y axis (a share is 0 to 1); else it follows the data. */
  max?: number;
  /** What a hover shows for the bucket at this index. */
  tip: (index: number) => TipData;
  /** What assistive technology hears for the plot; the data table holds the numbers. */
  label: string;
  height?: number;
  /** Fresh data is on its way: the old plot stays, dimmed. */
  stale?: boolean;
}

interface Hover {
  index: number;
  /** Centre of the bucket, in px from the left of the plot box, and the width of one bucket. */
  x: number;
  step: number;
  top: number;
  height: number;
  left: number;
}

const BAR_MAX = 24;

/** Running sums down the stack: the value of series i is the sum of series 0 to i. */
export function cumulative(series: readonly TimeSeries[], n: number): number[][] {
  const sums: number[][] = [];
  let running = Array.from({ length: n }, () => 0);
  for (const s of series) {
    running = running.map((sum, i) => sum + (s.values[i] ?? 0));
    sums.push(running);
  }
  return sums;
}

/** uPlot's options for a chart: an index axis (one tick per bucket, labelled by us), hairline grid, no legend, no cursor line. */
export function plotOptions(
  props: Pick<TimeChartProps, 'labels' | 'series' | 'mode' | 'format' | 'max'>,
  colors: ChartColors,
  size: { width: number; height: number },
  onCursor: (u: uPlot) => void,
): { options: uPlot.Options; data: uPlot.AlignedData } {
  const { labels, series, mode, format } = props;
  const n = labels.length;
  const stacked = mode === 'stacked-bars';
  const sums = stacked ? cumulative(series, n) : [];
  const peak = stacked
    ? Math.max(0, ...(sums[sums.length - 1] ?? []))
    : Math.max(0, ...series.flatMap((s) => s.values.map((v) => v ?? 0)));
  const { top, ticks } = niceTicks(props.max ?? peak, 4);
  // The tallest bar is drawn first, so the shorter ones sit on top of it with their 2 px gap.
  const order = stacked ? [...series.keys()].reverse() : [...series.keys()];
  const font = `11px ${colors.font}`;
  const xs = Array.from({ length: n }, (_, i) => i);
  const data: uPlot.AlignedData = [
    xs,
    ...order.map((i) => (stacked ? (sums[i] ?? []) : (series[i]?.values ?? []))),
  ];
  const bars = uPlot.paths.bars?.({ size: [0.7, BAR_MAX], radius: [0.2, 0] });
  const options: uPlot.Options = {
    width: size.width,
    height: size.height,
    // The last label is centred on the last bucket, half a bucket from the edge: room on the right keeps it from being cut.
    padding: [8, 20, 0, 0],
    legend: { show: false },
    cursor: { x: false, y: false, points: { show: false }, drag: { x: false, y: false } },
    select: { show: false, left: 0, top: 0, width: 0, height: 0 },
    scales: {
      x: { time: false, range: () => [-0.5, n - 0.5] },
      y: { range: () => [0, top] },
    },
    axes: [
      {
        stroke: colors.textSubtle,
        font,
        size: 26,
        gap: 4,
        grid: { show: false },
        ticks: { show: false },
        border: { show: true, stroke: colors.axis, width: 1 },
        splits: () => labelIndices(n, Math.max(2, Math.min(6, Math.floor((size.width - 60) / 80)))),
        values: (_u, splits) => splits.map((i) => labels[i] ?? ''),
      },
      {
        stroke: colors.textSubtle,
        font,
        size: 52,
        gap: 6,
        grid: { stroke: colors.grid, width: 1 },
        ticks: { show: false },
        border: { show: false },
        splits: () => ticks,
        values: (_u, splits) => splits.map((v) => format(v)),
      },
    ],
    series: [
      {},
      ...order.map((i): uPlot.Series => {
        const s = series[i] as TimeSeries;
        const color = colors.slots[s.slot] ?? '';
        return stacked
          ? {
              label: s.label,
              fill: color,
              stroke: colors.surface,
              width: 2,
              paths: bars,
              points: { show: false },
            }
          : {
              label: s.label,
              stroke: color,
              width: 2,
              spanGaps: false,
              points: {
                // One end-dot with the surface ring, on the last value there is.
                show: true,
                size: 8,
                width: 2,
                stroke: colors.surface,
                fill: color,
                filter: (_u, _si, _show) => {
                  const last = s.values.findLastIndex((v) => v != null);
                  return last < 0 ? null : [last];
                },
              },
            };
      }),
    ],
    hooks: { setCursor: [onCursor] },
  };
  return { options, data };
}

/**
 * A time series drawn by uPlot on a canvas: stacked bars (one per bucket) or a line. The hover shows every series at the bucket
 * and where the number comes from. uPlot sets styles through the style object model, which the page's policy allows, and writes no
 * style element. The numbers are also in the data table the frame offers, so the plot is a picture of them and nothing more.
 */
export function TimeChart(props: TimeChartProps) {
  const { labels, series, mode, format, max, tip, label, height = 220, stale } = props;
  const box = useRef<HTMLDivElement>(null);
  const plot = useRef<HTMLDivElement>(null);
  const width = useWidth(box);
  const colors = useChartColors();
  const [hover, setHover] = useState<Hover | null>(null);

  // biome-ignore lint/correctness/useExhaustiveDependencies: the plot is rebuilt when its data, colours or size change; `format` is a pure function of the same inputs
  useEffect(() => {
    const target = plot.current;
    if (!target || labels.length === 0) return;
    const onCursor = (u: uPlot) => {
      const index = u.cursor.idx;
      if (index == null || (u.cursor.left ?? -1) < 0) return setHover(null);
      const x = u.valToPos(index, 'x');
      const step = u.over.offsetWidth / Math.max(1, labels.length);
      setHover({
        index,
        x,
        step,
        left: u.over.offsetLeft,
        top: u.over.offsetTop,
        height: u.over.offsetHeight,
      });
    };
    const { options, data } = plotOptions(
      { labels, series, mode, format, max },
      colors,
      { width, height },
      onCursor,
    );
    const chart = new uPlot(options, data, target);
    return () => {
      chart.destroy();
      setHover(null);
    };
  }, [labels, series, mode, max, colors, width, height]);

  const tipData = hover ? tip(hover.index) : null;
  const flip = hover ? hover.left + hover.x > width / 2 : false;
  return (
    <div
      ref={box}
      className={cx('timechart', stale && 'is-stale')}
      role="img"
      aria-label={label}
      aria-busy={stale || undefined}
    >
      <div ref={plot} className="timechart__plot" />
      {hover && tipData ? (
        <>
          <div
            className="timechart__band"
            style={cssVars({
              '--x': `${hover.left + hover.x - hover.step / 2}px`,
              '--w': `${hover.step}px`,
              '--y': `${hover.top}px`,
              '--h': `${hover.height}px`,
            })}
          />
          <div
            className="chart-tip"
            data-side={flip ? 'left' : 'right'}
            style={cssVars({ '--x': `${hover.left + hover.x}px`, '--y': `${hover.top}px` })}
          >
            <TipBody {...tipData} />
          </div>
        </>
      ) : null}
    </div>
  );
}
