import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { SERIES_SLOTS, SlotMemory } from './colors';
import { histogram, labelIndices, linear, niceCeil, niceTicks } from './scales';
import { formatInterval, percentile, wilson } from './stats';

describe('wilson', () => {
  it('gives the textbook interval: 1 of 6 is 3 to 56 percent', () => {
    const p = wilson(1, 6);
    expect(p?.rate).toBeCloseTo(1 / 6, 6);
    expect(Math.round((p?.lo ?? 0) * 100)).toBe(3);
    expect(Math.round((p?.hi ?? 0) * 100)).toBe(56);
    expect(p ? formatInterval(p) : '').toBe('3–56%');
  });

  it('stays inside 0 and 1 at the ends and narrows as the count grows', () => {
    const none = wilson(0, 10);
    const all = wilson(10, 10);
    expect(none?.lo).toBeCloseTo(0, 10);
    expect(none?.hi).toBeLessThan(0.4);
    expect(all?.hi).toBeCloseTo(1, 10);
    expect(all?.lo).toBeGreaterThan(0.6);
    const small = wilson(7, 10);
    const large = wilson(700, 1000);
    expect((large?.hi ?? 0) - (large?.lo ?? 0)).toBeLessThan((small?.hi ?? 0) - (small?.lo ?? 0));
  });

  it('is null for no tries and for a count that cannot be', () => {
    expect(wilson(0, 0)).toBeNull();
    expect(wilson(3, 2)).toBeNull();
    expect(wilson(-1, 2)).toBeNull();
  });

  it('matches the server: nearest-rank percentiles', () => {
    expect(percentile([], 80)).toBeNull();
    expect(percentile([5], 80)).toBe(5);
    expect(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50)).toBe(5);
    expect(percentile([10, 1, 9, 2, 8, 3, 7, 4, 6, 5], 80)).toBe(8);
    expect(percentile([1, 2, 3, 4, 5], 80)).toBe(4);
  });
});

describe('scales', () => {
  it('rounds up to 1, 2, 5 or 10 times a power of ten', () => {
    expect(niceCeil(0)).toBe(1);
    expect(niceCeil(-4)).toBe(1);
    expect(niceCeil(0.7)).toBe(1);
    expect(niceCeil(1.1)).toBe(2);
    expect(niceCeil(3)).toBe(5);
    expect(niceCeil(570_000)).toBe(1_000_000);
    expect(niceCeil(11.1)).toBe(20);
  });

  it('puts ticks on a nice step from zero to a top at or above the maximum', () => {
    expect(niceTicks(570_000, 4)).toEqual({ top: 600_000, ticks: [0, 200_000, 400_000, 600_000] });
    expect(niceTicks(11.1, 4)).toEqual({ top: 15, ticks: [0, 5, 10, 15] });
    expect(niceTicks(1, 4)).toEqual({ top: 1, ticks: [0, 0.5, 1] });
    expect(niceTicks(0, 4)).toEqual({ top: 1, ticks: [0, 1] });
    for (const max of [0.3, 7, 99, 1234, 56_789]) {
      const { top, ticks } = niceTicks(max, 4);
      expect(top).toBeGreaterThanOrEqual(max);
      expect(ticks[0]).toBe(0);
      expect(ticks[ticks.length - 1]).toBe(top);
    }
  });

  it('maps a domain onto a range, and a flat domain onto the start', () => {
    const f = linear([0, 10], [100, 200]);
    expect(f(0)).toBe(100);
    expect(f(5)).toBe(150);
    expect(linear([3, 3], [7, 9])(3)).toBe(7);
  });

  it('labels every bucket while few, about six when many, and always the last', () => {
    expect(labelIndices(0)).toEqual([]);
    expect(labelIndices(5)).toEqual([0, 1, 2, 3, 4]);
    const thirty = labelIndices(30, 6);
    expect(thirty[0]).toBe(0);
    expect(thirty[thirty.length - 1]).toBe(29);
    expect(thirty.length).toBeLessThanOrEqual(8);
    expect(labelIndices(30, 2).length).toBeLessThanOrEqual(4);
    // No two labels crowd each other: the last gap is at least half a step.
    const gaps = thirty.slice(1).map((v, i) => v - (thirty[i] ?? 0));
    expect(Math.min(...gaps)).toBeGreaterThanOrEqual(Math.floor((gaps[0] ?? 1) / 2));
  });

  it('counts values into bins with an open last bin and drops what is under the first edge', () => {
    const bins = histogram([1, 4.9, 5, 14, 15, 500, -1], [0, 5, 15]);
    expect(bins.map((b) => b.count)).toEqual([2, 2, 2]);
    expect(bins[2]?.to).toBe(Number.POSITIVE_INFINITY);
  });
});

describe('SlotMemory', () => {
  it('gives the first group hue 1 and keeps it when neighbours come and go', () => {
    const memory = new SlotMemory();
    expect([...memory.assign(['a', 'b', 'c']).values()]).toEqual([1, 2, 3]);
    // b is filtered out: a and c keep their hues.
    expect([...memory.assign(['a', 'c']).values()]).toEqual([1, 3]);
    // b returns and takes its own hue again.
    expect(memory.assign(['a', 'b', 'c']).get('b')).toBe(2);
  });

  it('takes a slot from a group that is not shown only when none is unclaimed', () => {
    const memory = new SlotMemory();
    const first = Array.from({ length: SERIES_SLOTS }, (_, i) => `g${i}`);
    memory.assign(first);
    // Eight hues are claimed; a ninth name shown alone takes the hue of a group that is not shown.
    const slot = memory.assign(['new']).get('new');
    expect(slot).toBeGreaterThanOrEqual(1);
    expect(slot).toBeLessThanOrEqual(SERIES_SLOTS);
  });

  it('never gives two shown groups the same hue', () => {
    const memory = new SlotMemory();
    memory.assign(['a', 'b', 'c', 'd']);
    memory.assign(['e', 'f', 'g', 'h', 'i', 'j']);
    const shown = memory.assign(['a', 'e', 'j', 'h']);
    const slots = [...shown.values()].filter((s) => s > 0);
    expect(new Set(slots).size).toBe(slots.length);
  });
});

describe('the chart and screen code', () => {
  // Hues come from the design system's tokens (CSS var, or the token's computed value for a canvas) and from nothing else.
  const roots = ['src/charts', 'src/screens/analytics', 'src/screens/learning'];
  const files = (dir: string): string[] =>
    readdirSync(dir).flatMap((name) => {
      const path = join(dir, name);
      return statSync(path).isDirectory() ? files(path) : [path];
    });
  const sources = roots
    .flatMap(files)
    .filter((f) => /\.(tsx?|css)$/.test(f) && !/\.test\./.test(f));

  it('holds no literal colour, and no inline style attribute the page policy would refuse', () => {
    expect(sources.length).toBeGreaterThan(20);
    for (const file of sources) {
      const text = readFileSync(file, 'utf8');
      expect(text, file).not.toMatch(/#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(|\boklch\(/);
      expect(text, file).not.toMatch(
        /\.setAttribute\(\s*['"]style['"]|<style\b|dangerouslySetInnerHTML/,
      );
    }
  });
});
