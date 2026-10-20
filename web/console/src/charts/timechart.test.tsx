import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { type ChartColors, readChartColors, slotVar } from './colors';
import { cumulative, plotOptions, TimeChart, type TimeSeries, tipLeft } from './TimeChart';

const colors: ChartColors = {
  slots: ['#grey', '#one', '#two', '#three'],
  text: '#text',
  textMuted: '#muted',
  textSubtle: '#subtle',
  grid: '#grid',
  axis: '#axis',
  surface: '#surface',
  font: 'sans-serif',
};

const series: TimeSeries[] = [
  { id: 'a', label: 'payments', slot: 1, values: [100, 0, 200] },
  { id: 'b', label: 'ledger', slot: 2, values: [50, 70, null] },
];
const labels = ['11 Oct', '12 Oct', '13 Oct'];
const size = { width: 640, height: 220 };

describe('cumulative', () => {
  it('runs down the stack: the series at i is the sum of the series up to i, a gap counted as nothing', () => {
    expect(cumulative(series, 3)).toEqual([
      [100, 0, 200],
      [150, 70, 200],
    ]);
    expect(cumulative([], 2)).toEqual([]);
  });
});

describe('tipLeft', () => {
  it('sits right of the bucket, flips left near the right edge, and is pushed in when neither side has room', () => {
    expect(tipLeft(100, 200, 640)).toBe(112);
    // The right-most bucket: there is no room on the right, so the tip moves left whole instead of narrowing.
    expect(tipLeft(630, 200, 640)).toBe(418);
    // A phone: neither side holds it, so it keeps its width against the nearer edge, inside the chart.
    expect(tipLeft(150, 260, 340)).toBe(80);
    expect(tipLeft(5, 260, 340)).toBe(17);
    expect(tipLeft(330, 400, 340)).toBe(0);
  });
});

describe('plotOptions', () => {
  const stacked = () =>
    plotOptions(
      { labels, series, mode: 'stacked-bars', format: (v) => `$${v}` },
      colors,
      size,
      () => {},
    );

  it('draws a stack from its running sums, the tallest first so the shorter ones sit on it', () => {
    const { data, options } = stacked();
    expect(data).toEqual([
      [0, 1, 2],
      [150, 70, 200], // ledger on top: payments plus ledger
      [100, 0, 200], // payments
    ]);
    // Series 0 is the x axis; the next two are painted tallest first, each in its own hue with a surface-coloured gap.
    expect(options.series.slice(1).map((s) => [s.label, s.fill, s.stroke, s.width])).toEqual([
      ['ledger', '#two', '#surface', 2],
      ['payments', '#one', '#surface', 2],
    ]);
    expect(options.legend?.show).toBe(false);
  });

  it('puts a nice top on the value axis and one tick per bucket on the index axis', () => {
    const { options } = stacked();
    const y = options.scales?.y?.range as () => [number, number];
    expect(y()).toEqual([0, 200]);
    const x = options.scales?.x?.range as () => [number, number];
    expect(x()).toEqual([-0.5, 2.5]);
    type Axis = { splits: () => number[]; values: (u: unknown, s: number[]) => string[] };
    const [xAxis, yAxis] = (options.axes ?? []) as unknown as [Axis, Axis];
    expect(xAxis.splits()).toEqual([0, 1, 2]);
    expect(xAxis.values(null, [0, 2])).toEqual(['11 Oct', '13 Oct']);
    expect(yAxis.values(null, yAxis.splits())).toEqual(['$0', '$50', '$100', '$150', '$200']);
  });

  it('colours the grid, the axes and the text from the tokens it was given', () => {
    const { options } = stacked();
    const [xAxis, yAxis] = options.axes ?? [];
    expect(xAxis?.stroke).toBe('#subtle');
    expect(xAxis?.border?.stroke).toBe('#axis');
    expect(yAxis?.grid?.stroke).toBe('#grid');
  });

  it('draws a line as its values, with a gap for a missing one and one end dot on the last value there is', () => {
    const { data, options } = plotOptions(
      { labels, series, mode: 'line', format: String, max: 1 },
      colors,
      size,
      () => {},
    );
    expect(data).toEqual([
      [0, 1, 2],
      [100, 0, 200],
      [50, 70, null],
    ]);
    const range = options.scales?.y?.range as unknown as () => [number, number];
    expect(range()[1]).toBeGreaterThanOrEqual(1);
    const ledger = options.series[2] as {
      points?: { filter?: (...a: unknown[]) => number[] | null };
    };
    expect(ledger.points?.filter?.(null, 2, true)).toEqual([1]);
    const payments = options.series[1] as {
      points?: { filter?: (...a: unknown[]) => number[] | null };
    };
    expect(payments.points?.filter?.(null, 1, true)).toEqual([2]);
    expect(options.series[1]?.stroke).toBe('#one');
  });
});

describe('colours from tokens', () => {
  it('reads the chart hues, the surface and the axis from the stylesheet, and maps a slot to its custom property', () => {
    const root = document.documentElement;
    root.style.setProperty('--chart-1', '#112233');
    root.style.setProperty('--neutral-9', '#999999');
    root.style.setProperty('--surface', '#fafafa');
    const read = readChartColors(root);
    expect(read.slots[0]).toBe('#999999');
    expect(read.slots[1]).toBe('#112233');
    expect(read.surface).toBe('#fafafa');
    expect(slotVar(0)).toBe('--neutral-9');
    expect(slotVar(3)).toBe('--chart-3');
    for (const name of ['--chart-1', '--neutral-9', '--surface']) root.style.removeProperty(name);
  });
});

describe('TimeChart', () => {
  const tip = (index: number) => ({
    title: `bucket ${index}`,
    rows: [{ label: 'payments', value: '$1', slot: 1 }],
    source: 'analytics/v1 tasks.points · usd_estimate',
  });

  it('is one named picture of the data, with its numbers in the table next to it, and no style element', () => {
    render(
      <TimeChart
        labels={labels}
        series={series}
        mode="stacked-bars"
        format={String}
        tip={tip}
        label="Spend per day"
      />,
    );
    const chart = screen.getByRole('img', { name: 'Spend per day' });
    expect(chart.querySelector('canvas')).toBeTruthy();
    expect(document.querySelectorAll('style')).toHaveLength(0);
  });

  it('is marked busy and dimmed while fresh data loads, and draws nothing for no bucket', () => {
    const { rerender } = render(
      <TimeChart
        labels={labels}
        series={series}
        mode="line"
        format={String}
        tip={tip}
        label="Cache"
        stale
      />,
    );
    const chart = screen.getByRole('img', { name: 'Cache' });
    expect(chart.getAttribute('aria-busy')).toBe('true');
    expect(chart.className).toContain('is-stale');
    rerender(
      <TimeChart labels={[]} series={[]} mode="line" format={String} tip={tip} label="Cache" />,
    );
    expect(screen.getByRole('img', { name: 'Cache' }).querySelector('canvas')).toBeNull();
  });

  it('shows the hover of the bucket under the pointer, with the source of the number, and hides it when the pointer leaves', async () => {
    render(
      <TimeChart
        labels={labels}
        series={series}
        mode="stacked-bars"
        format={String}
        tip={tip}
        label="Spend per day"
      />,
    );
    const over = document.querySelector('.u-over') as HTMLElement;
    fireEvent.mouseMove(over, { clientX: 300, clientY: 40 });
    const shown = await screen.findByText(/^bucket \d$/);
    expect(shown).toBeTruthy();
    expect(screen.getByText('analytics/v1 tasks.points · usd_estimate')).toBeTruthy();
    fireEvent.mouseLeave(over);
    expect(screen.queryByText(/^bucket \d$/)).toBeNull();
  });
});
