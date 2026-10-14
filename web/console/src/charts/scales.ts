// Scales for the charts: where a value lands, which ticks to print, how a list falls into bins. Pure, so the arithmetic is tested
// without a canvas.

/** The smallest "nice" number (1, 2, 5 or 10 times a power of ten) at or above `max`; 1 for a max of zero or less. */
export function niceCeil(max: number): number {
  if (!(max > 0) || !Number.isFinite(max)) return 1;
  const exp = 10 ** Math.floor(Math.log10(max));
  const f = max / exp;
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * exp;
}

/**
 * Ticks from 0 up to a top at or above `max`, spaced by a nice step so that about `count` of them fit: a max of 570 over four
 * ticks steps by 200 to 600, not by 250 to 1000. Always at least one step.
 */
export function niceTicks(max: number, count = 4): { top: number; ticks: number[] } {
  const step = niceCeil(Math.max(max, 0) / count);
  const steps = Math.max(1, Math.ceil(max / step - 1e-9));
  return { top: step * steps, ticks: Array.from({ length: steps + 1 }, (_, i) => step * i) };
}

/** A linear map from a domain to a range. A flat domain maps to the start of the range. */
export function linear(
  domain: [number, number],
  range: [number, number],
): (value: number) => number {
  const span = domain[1] - domain[0];
  return (value) =>
    span === 0 ? range[0] : range[0] + ((value - domain[0]) / span) * (range[1] - range[0]);
}

/** The indices of the ticks to label on an axis of `n` buckets: all of them while few, else about `target`, always the last. */
export function labelIndices(n: number, target = 6): number[] {
  if (n <= 0) return [];
  if (n <= target + 1) return Array.from({ length: n }, (_, i) => i);
  const step = Math.ceil((n - 1) / target);
  const indices: number[] = [];
  for (let i = 0; i < n; i += step) indices.push(i);
  const last = indices[indices.length - 1] ?? 0;
  // The last bucket is labelled too, unless it would crowd the one before it.
  if (last !== n - 1) {
    if (n - 1 - last < step / 2) indices[indices.length - 1] = n - 1;
    else indices.push(n - 1);
  }
  return indices;
}

export interface Bin {
  /** Inclusive start. */
  from: number;
  /** Exclusive end; `Infinity` for the last, open bin. */
  to: number;
  count: number;
}

/** Counts `values` into bins whose edges are `edges` (the last bin is open-ended). A value under the first edge is dropped. */
export function histogram(values: readonly number[], edges: readonly number[]): Bin[] {
  const bins: Bin[] = edges.map((from, i) => ({
    from,
    to: edges[i + 1] ?? Number.POSITIVE_INFINITY,
    count: 0,
  }));
  for (const value of values) {
    const bin = bins.find((b) => value >= b.from && value < b.to);
    if (bin) bin.count += 1;
  }
  return bins;
}
