import type {
  SweepBody,
  SweepRepositoryBody,
  SweepSettingsBody,
  SweepSettingsChange,
} from '../../api/sweep';

export const DAYS = [
  ['monday', 'Mon'],
  ['tuesday', 'Tue'],
  ['wednesday', 'Wed'],
  ['thursday', 'Thu'],
  ['friday', 'Fri'],
  ['saturday', 'Sat'],
  ['sunday', 'Sun'],
] as const;

export const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

/** A sweep is going: the button waits for it and the screen names it. */
export const isScanning = (sweep: SweepBody): boolean => sweep.running !== undefined;

/** The repositories of the last sweep, the ones with a problem first. */
export const sweepRows = (sweep: SweepBody): SweepRepositoryBody[] =>
  [...(sweep.last?.repositories ?? [])].sort(
    (a, b) => Number(b.problem !== undefined) - Number(a.problem !== undefined),
  );

export const needsYouTotal = (rows: SweepRepositoryBody[]): number =>
  rows.reduce((sum, row) => sum + row.needs_you, 0);

/** How many needs-you issues the list shows before "and N more". */
export const NEEDS_YOU_SHOWN = 20;

/** "1 issue waits for you", "3 issues wait for you". */
export const waitText = (count: number): string =>
  count === 1 ? '1 issue waits for you' : `${count} issues wait for you`;

/** What the owner has set; `null` is "no override: the profile's value". */
export interface SweepDraft {
  enabled: boolean | null;
  at: string[] | null;
  days: string[] | null;
  repositories: Record<string, boolean | null>;
  /** The box as typed; empty is no override. */
  hold: string;
}

export const draftOf = (settings: SweepSettingsBody): SweepDraft => ({
  enabled: settings.enabled.override ?? null,
  at: settings.at.override ?? null,
  days: settings.days.override ?? null,
  repositories: Object.fromEntries(
    Object.entries(settings.repositories).map(([name, s]) => [name, s.override ?? null]),
  ),
  hold: settings.subscription_hold_percent.override?.toString() ?? '',
});

/** The time as `HH:mm`, or the problem with it. */
export function timeProblem(text: string, existing: string[]): string | null {
  if (!TIME.test(text)) return 'Use HH:mm, 24 hours, such as 08:30.';
  return existing.includes(text) ? 'That time is already there.' : null;
}

export const addTime = (times: string[], text: string): string[] => [...times, text].sort();

/** The problem with the hold box: whole percent from 1 to 100, or empty. */
export function holdProblem(text: string): string | null {
  if (text.trim() === '') return null;
  const value = Number(text);
  if (!Number.isInteger(value)) return 'A whole number.';
  return value < 1 || value > 100 ? 'From 1 to 100.' : null;
}

export const hasProblem = (draft: SweepDraft): boolean => holdProblem(draft.hold) !== null;

/** The body of the PUT: only what is overridden, since the server clears whatever is left out. */
export function changeOf(draft: SweepDraft): SweepSettingsChange {
  const repositories = Object.fromEntries(
    Object.entries(draft.repositories).filter((e): e is [string, boolean] => e[1] !== null),
  );
  return {
    ...(draft.enabled === null ? {} : { enabled: draft.enabled }),
    ...(draft.at === null ? {} : { at: draft.at }),
    ...(draft.days === null ? {} : { days: draft.days }),
    ...(Object.keys(repositories).length === 0 ? {} : { repositories }),
    ...(draft.hold.trim() === '' ? {} : { subscription_hold_percent: Number(draft.hold) }),
  };
}

export const isUnchanged = (draft: SweepDraft, settings: SweepSettingsBody): boolean =>
  JSON.stringify(changeOf(draft)) === JSON.stringify(changeOf(draftOf(settings)));

/** "profile 08:00, 20:00 · yours not set · in force 08:00, 20:00", the line each setting carries as Limits does. */
export function inForce<T>(
  setting: { profile: T; override?: T; effective: T },
  show: (value: T) => string,
): string {
  const yours = setting.override === undefined ? 'not set' : show(setting.override);
  return `Profile ${show(setting.profile)} · yours ${yours} · in force ${show(setting.effective)}`;
}

export const dayNames = (days: string[]): string =>
  days.length === 7
    ? 'every day'
    : DAYS.filter(([id]) => days.includes(id))
        .map(([, short]) => short)
        .join(', ') || 'no day';

export const onOff = (value: boolean): string => (value ? 'on' : 'off');
