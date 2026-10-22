import type { AccountBody, KeyBody } from '../../api/common';
import { baseName, capLevel, formatPercent, formatTokens, formatUsd } from '../../lib/format';
import type { Status } from '../../lib/status';

export type SpendWindow = 'day' | 'month';

export interface Meter {
  value: number;
  /** Null: no cap, so the track is dashed and empty, never full. */
  cap: number | null;
  /** The left of the meter's head: "$9.40 of $10.00". */
  head: string;
  /** The right of the head: "94%". Absent: the meter says "no cap". */
  text?: string;
  level?: 'warn' | 'danger';
}

/** An account’s spend in a window against its cap: dollars when it has a dollar cap, else tokens, else "no cap". */
export function windowMeter(
  account: Pick<AccountBody, 'limits' | 'spend'>,
  window: SpendWindow,
): Meter {
  const { limits, spend } = account;
  const used = spend[window];
  const usdCap = (window === 'day' ? limits.max_usd_per_day : limits.max_usd_per_month) ?? null;
  const tokenCap =
    (window === 'day' ? limits.max_tokens_per_day : limits.max_tokens_per_month) ?? null;
  if (usdCap != null && usdCap > 0) {
    const pct = Math.round((used.usd / usdCap) * 100);
    return {
      value: used.usd,
      cap: usdCap,
      head: `${formatUsd(used.usd)} of ${formatUsd(usdCap)}`,
      text: formatPercent(pct),
      level: capLevel(pct),
    };
  }
  if (tokenCap != null && tokenCap > 0) {
    const pct = Math.round((used.tokens / tokenCap) * 100);
    return {
      value: used.tokens,
      cap: tokenCap,
      head: `${formatTokens(used.tokens)} of ${formatTokens(tokenCap)} tokens`,
      text: formatPercent(pct),
      level: capLevel(pct),
    };
  }
  return { value: used.usd, cap: null, head: formatUsd(used.usd) };
}

/** The directories an account may name: none listed means any the server allows. */
export function repositoriesText(account: AccountBody): { text: string; title: string } {
  const list = account.repositories;
  if (!list?.length)
    return {
      text: account.owner ? 'any' : 'any the server allows',
      title: 'No restriction beyond repository_roots',
    };
  return { text: list.map(baseName).join(', '), title: list.join('\n') };
}

export function runsText(account: AccountBody): string {
  const cap = account.limits.max_concurrent_runs;
  return `${account.driven ? 'driven' : 'read-only'} · ${account.unfinished_runs} of ${cap ?? 'no cap'}`;
}

export interface CredentialView {
  status: Status;
  label: string;
  note: string;
  /** The secret item resolves right now (never its value): `unknown` where no item is named or it was not checked. */
  presence: 'present' | 'missing' | 'unknown';
  /** `allow_server_credential`: not an owner setting; unknown for an account with a model block, which the route does not report. */
  allowServer: boolean | null;
}

export function credentialView(account: AccountBody): CredentialView {
  const { mode, present } = account.credential;
  const presence = present === true ? 'present' : present === false ? 'missing' : 'unknown';
  const own = (label: string, note: string): CredentialView =>
    present === false
      ? {
          status: 'failed',
          label: `${label}: item missing`,
          note: `${note} The secret item the profile names does not resolve, so a request that needs it is refused.`,
          presence,
          allowServer: null,
        }
      : { status: 'done', label, note, presence, allowServer: null };
  switch (mode) {
    case 'own_key':
      return own(
        'Own API key',
        'Its read-only runs and its batches use its own key; the server credential is never spent.',
      );
    case 'own_gateway':
      return own(
        'Gateway key',
        'Uses a key an Anthropic-compatible gateway issued, through the batch’s egress container.',
      );
    case 'server':
      return account.owner
        ? {
            status: 'asked',
            label: 'Server credential',
            note: 'The owner spends the server’s credential. This is not an account setting.',
            presence,
            allowServer: null,
          }
        : {
            status: 'asked',
            label: 'Server (allowed)',
            note: 'allow_server_credential is true: it spends the server’s credential and shares its rate limit.',
            presence,
            allowServer: true,
          };
    case 'none':
      return {
        status: 'failed',
        label: 'Missing',
        note: 'No model block and allow_server_credential is false: every request is refused with 403 model_credential_missing.',
        presence,
        allowServer: false,
      };
    default:
      return {
        status: 'queued',
        label: mode,
        note: 'A credential mode this console does not know.',
        presence,
        allowServer: null,
      };
  }
}

export const presenceText = (view: CredentialView): string =>
  view.presence === 'present'
    ? 'The secret item resolves.'
    : view.presence === 'missing'
      ? 'The secret item does not resolve.'
      : 'No secret item to check.';

export interface KeyView {
  id: string;
  label: string;
  created: string;
  revoked: string | null;
}

/** A key as the key file keeps it: its public id and label, never its value. */
export const keyViews = (keys: KeyBody[]): KeyView[] =>
  keys.map((key) => ({
    id: key.id,
    label: key.label?.trim() || 'unlabelled',
    created: key.created,
    revoked: key.revoked ?? null,
  }));

/** The account the page opens on: the first one that is not the owner, else the owner. */
export const firstSelection = (accounts: AccountBody[]): string | undefined =>
  (accounts.find((a) => !a.owner) ?? accounts[0])?.name;

export const capsRows = (account: AccountBody): Array<[string, string]> => {
  const { limits, spend } = account;
  const row = (cap: number | undefined, show: (n: number) => string): string =>
    cap == null ? 'no cap' : show(cap);
  return [
    ['Dollars per day', `${formatUsd(spend.day.usd)} of ${row(limits.max_usd_per_day, formatUsd)}`],
    [
      'Dollars per month',
      `${formatUsd(spend.month.usd)} of ${row(limits.max_usd_per_month, formatUsd)}`,
    ],
    [
      'Tokens per day',
      `${formatTokens(spend.day.tokens)} of ${row(limits.max_tokens_per_day, formatTokens)}`,
    ],
    [
      'Tokens per month',
      `${formatTokens(spend.month.tokens)} of ${row(limits.max_tokens_per_month, formatTokens)}`,
    ],
    ['Concurrent runs', `${account.unfinished_runs} of ${limits.max_concurrent_runs ?? 'no cap'}`],
  ];
};

/** What the month forecast says, in words: null when the server sent none. */
export function forecastView(account: AccountBody) {
  const f = account.forecast;
  if (!f) return null;
  const cap = f.cap_usd ?? undefined;
  return {
    projected: f.projected_usd,
    cap,
    over: f.over_cap,
    text:
      cap == null
        ? `≈ ${formatUsd(f.projected_usd)}`
        : `≈ ${formatUsd(f.projected_usd)} of ${formatUsd(cap)}`,
    rate: `${formatUsd(f.rate_per_day)} a day over the last 7 days; ${formatUsd(account.spend.month.usd)} so far this month.`,
    reached: f.cap_reached_at ?? null,
  };
}
