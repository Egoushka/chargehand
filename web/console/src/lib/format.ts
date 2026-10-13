export const DASH = '—';

/** Dollars; a run with no priced cost (a subscription) has none. */
export function formatUsd(value: number | null | undefined): string {
  if (value == null) return DASH;
  return `$${value.toFixed(value >= 1 ? 2 : 3)}`;
}

export function formatCount(value: number | null | undefined): string {
  return value == null ? DASH : new Intl.NumberFormat('en-US').format(value);
}

export function formatWhen(iso: string | null | undefined): string {
  if (!iso) return DASH;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

export function baseName(path: string | null | undefined): string {
  return path ? (path.split(/[\\/]/).filter(Boolean).pop() ?? path) : DASH;
}
