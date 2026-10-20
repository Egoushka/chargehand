// Number and time formats of the design system (README, "Writing numbers"). Unknown is an em dash, never 0.
export const DASH = '—';

/** The one zone the console shows time in. The top bar says so once; cells carry no zone suffix. */
export const DISPLAY_ZONE = 'Europe/Kyiv';
export const DISPLAY_ZONE_LABEL = 'Kyiv';

/** Dollars: `$` and two decimals, `<$0.01` under a cent, no decimals from $1,000. `≈` marks the dollar equivalent of tokens. */
export function formatUsd(value: number | null | undefined, estimate = false): string {
  if (value == null) return DASH;
  const text =
    value > 0 && value < 0.01
      ? '<$0.01'
      : `$${value >= 1000 ? Math.round(value).toLocaleString('en-US') : value.toFixed(2)}`;
  return estimate ? `≈ ${text}` : text;
}

const trim = (text: string) => text.replace(/\.0+$/, '');

/** Tokens: as is under 1,000, then `k`, `M` and `B` with three significant digits. */
export function formatTokens(value: number | null | undefined): string {
  if (value == null) return DASH;
  const a = Math.abs(value);
  if (a < 1000) return String(Math.round(value));
  if (a < 1e6)
    return `${trim(a < 1e5 ? (value / 1e3).toFixed(1) : String(Math.round(value / 1e3)))}k`;
  if (a < 1e9)
    return `${trim(a < 1e8 ? (value / 1e6).toFixed(a < 1e7 ? 2 : 1) : String(Math.round(value / 1e6)))}M`;
  return `${trim((value / 1e9).toFixed(2))}B`;
}

/** A count with thousands separators. */
export function formatCount(value: number | null | undefined): string {
  return value == null ? DASH : new Intl.NumberFormat('en-US').format(value);
}

/** Whole numbers; one decimal under 10 % when it matters. */
export function formatPercent(value: number | null | undefined): string {
  if (value == null) return DASH;
  return `${value < 10 && value % 1 ? value.toFixed(1) : Math.round(value)}%`;
}

/** At most two units: `47s`, `4m 12s`, `2h 14m`, `1d 3h`. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null) return DASH;
  const total = Math.max(0, Math.round(seconds));
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (d) return `${d}d${h ? ` ${h}h` : ''}`;
  if (h) return `${h}h${m ? ` ${m}m` : ''}`;
  if (m) return `${m}m${s ? ` ${s}s` : ''}`;
  return `${s}s`;
}

const parts = (date: Date, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('en-GB', { timeZone: DISPLAY_ZONE, hour12: false, ...options }).format(
    date,
  );

const toDate = (value: string | number | Date): Date =>
  value instanceof Date ? value : new Date(value);

/** `12:42` today, `11 Oct 22:10` on another day, seconds when asked (event streams). The date is read in the display zone. */
export function formatTime(
  value: string | number | Date | null | undefined,
  options: { seconds?: boolean; now?: Date } = {},
): string {
  if (value == null || value === '') return DASH;
  const date = toDate(value);
  if (Number.isNaN(date.getTime())) return String(value);
  const clock = parts(date, {
    hour: '2-digit',
    minute: '2-digit',
    second: options.seconds ? '2-digit' : undefined,
  });
  const day = (d: Date) => parts(d, { day: 'numeric', month: 'short', year: 'numeric' });
  if (day(date) === day(options.now ?? new Date())) return clock;
  return `${parts(date, { day: 'numeric', month: 'short' })} ${clock}`;
}

/** The clock alone, for a time that recurs daily (when a cap resets). */
export function formatClock(value: string | number | Date | null | undefined): string {
  if (value == null || value === '') return DASH;
  const date = toDate(value);
  return Number.isNaN(date.getTime())
    ? String(value)
    : parts(date, { hour: '2-digit', minute: '2-digit' });
}

/** The full local date and time, for a title or a long hover. */
export function formatDateTime(value: string | number | Date | null | undefined): string {
  if (value == null || value === '') return DASH;
  const date = toDate(value);
  return Number.isNaN(date.getTime())
    ? String(value)
    : `${parts(date, { day: 'numeric', month: 'short', year: 'numeric' })} ${parts(date, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
}

/** `4m ago`, for toasts and the recent merges only; the absolute time goes in the title. */
export function formatAgo(value: string | number | Date, now: Date = new Date()): string {
  const seconds = (now.getTime() - toDate(value).getTime()) / 1000;
  if (Number.isNaN(seconds)) return DASH;
  return seconds < 45 ? 'just now' : `${formatDuration(seconds)} ago`;
}

/** Lists show the six-hex suffix of a run id; headers and copies carry the whole id. */
export function shortId(id: string | null | undefined): string {
  if (!id) return DASH;
  return /([0-9a-f]{6})$/.exec(id)?.[1] ?? id;
}

/** A path or `owner/name` by its last segment. */
export function baseName(path: string | null | undefined): string {
  return path ? (path.split(/[\\/]/).filter(Boolean).pop() ?? path) : DASH;
}

/** A repository for a table cell: `owner/name` as is, a checkout path by its last two segments (the full path stays in the cell's title). */
export function repoLabel(repository: string): string {
  return repository.startsWith('/')
    ? repository.split('/').filter(Boolean).slice(-2).join('/')
    : repository;
}

/** `ok` when the figure is the share `value / cap`: none under 70 %, `warn` from 70 %, `danger` from 90 % (CapMeter). */
export function capLevel(percent: number): 'warn' | 'danger' | undefined {
  return percent >= 90 ? 'danger' : percent >= 70 ? 'warn' : undefined;
}

/** Middle-truncates a branch or path so both ends stay readable; the full text belongs in the title. */
export function middleTruncate(text: string, max = 40): string {
  if (text.length <= max) return text;
  const keep = max - 1;
  const head = Math.ceil(keep / 2);
  return `${text.slice(0, head)}…${text.slice(text.length - (keep - head))}`;
}
