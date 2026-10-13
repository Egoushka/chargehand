/** A share with its 95 % Wilson score interval, as fractions of 1. */
export interface Proportion {
  k: number;
  n: number;
  rate: number;
  lo: number;
  hi: number;
}

const Z95 = 1.959964;

/**
 * k successes in n tries with the Wilson 95 % interval, the one `chargehand bench` reports (docs/guide/benchmark.md). It stays
 * inside 0..1 and is wide for a small n, which is the point: one task moves a rate by 5 to 20 points. Null for no tries.
 */
export function wilson(k: number, n: number): Proportion | null {
  if (n <= 0 || k < 0 || k > n) return null;
  const p = k / n;
  const z2 = Z95 * Z95;
  const denom = 1 + z2 / n;
  const centre = (p + z2 / (2 * n)) / denom;
  const half = (Z95 * Math.sqrt((p * (1 - p)) / n + z2 / (4 * n * n))) / denom;
  return { k, n, rate: p, lo: Math.max(0, centre - half), hi: Math.min(1, centre + half) };
}

/** `72–80%`: the interval as whole percents. */
export function formatInterval(p: Proportion): string {
  return `${Math.round(p.lo * 100)}–${Math.round(p.hi * 100)}%`;
}

/** Nearest-rank percentile of a list (the server's rule): the smallest value with at least that share at or below it. */
export function percentile(values: readonly number[], percent: number): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = Math.min(sorted.length, Math.max(1, Math.ceil((percent / 100) * sorted.length)));
  return sorted[rank - 1] ?? null;
}
