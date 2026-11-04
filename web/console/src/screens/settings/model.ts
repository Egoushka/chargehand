import type { IntakeLimitsBody, LimitsChange } from '../../api/limits';

/** What the owner has typed: an empty box means "the profile's value". */
export interface Draft {
  max_concurrent: string;
  max_review_rounds: string;
  max_usd_total: string;
  paused: boolean;
}

export const draftOf = (limits: IntakeLimitsBody): Draft => ({
  max_concurrent: limits.max_concurrent.override?.toString() ?? '',
  max_review_rounds: limits.max_review_rounds.override?.toString() ?? '',
  max_usd_total: limits.max_usd_total.override?.toString() ?? '',
  paused: limits.paused,
});

export const MAX_USD = 100;

/** The range the server accepts for each cap; the same text it answers a refusal with. */
export const RANGES = {
  max_concurrent: { min: 1, max: 10, whole: true },
  max_review_rounds: { min: 0, max: 10, whole: true },
  max_usd_total: { min: 0.01, max: MAX_USD, whole: false },
} as const;

type Cap = keyof typeof RANGES;

/** The problem with one box, or null when it is empty (no override) or in range. */
export function problemWith(cap: Cap, text: string): string | null {
  if (text.trim() === '') return null;
  const value = Number(text);
  const { min, max, whole } = RANGES[cap];
  if (!Number.isFinite(value) || (whole && !Number.isInteger(value)))
    return whole ? 'A whole number.' : 'A number.';
  if (value < min || value > max) return `From ${min} to ${max}.`;
  return null;
}

export const hasProblem = (draft: Draft): boolean =>
  (Object.keys(RANGES) as Cap[]).some((cap) => problemWith(cap, draft[cap]) !== null);

export function changeOf(draft: Draft): LimitsChange {
  const number = (text: string) => (text.trim() === '' ? null : Number(text));
  return {
    max_concurrent: number(draft.max_concurrent),
    max_review_rounds: number(draft.max_review_rounds),
    max_usd_total: number(draft.max_usd_total),
    paused: draft.paused,
  };
}

export const isUnchanged = (draft: Draft, limits: IntakeLimitsBody): boolean => {
  const saved = draftOf(limits);
  return (Object.keys(saved) as (keyof Draft)[]).every((key) => saved[key] === draft[key]);
};
