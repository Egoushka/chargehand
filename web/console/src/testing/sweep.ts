// Server answers for the Sweep screen and its settings, with invented repositories.
import type {
  SweepBody,
  SweepRepositoryBody,
  SweepSettingsBody,
  SweepSettingsChange,
} from '../api/sweep';

const iso = (now: number, minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();

export const SWEEP_ID = 'sweep-20261114-150000-a1b2c3';

export const repoRow = (over: Partial<SweepRepositoryBody> = {}): SweepRepositoryBody => ({
  repository: 'example/app',
  ready: 3,
  needs_you: 1,
  blocked: 0,
  epic: 0,
  skipped: 0,
  enqueued: 2,
  deferred: 0,
  running: 1,
  ...over,
});

export function sweep(over: Partial<SweepBody> = {}, now = Date.now()): SweepBody {
  return {
    contract_version: 'sweep/v1',
    configured: true,
    schedule_on: true,
    time_zone: 'Europe/Berlin',
    last: {
      id: SWEEP_ID,
      trigger: 'schedule',
      started: iso(now, 5),
      finished: iso(now, 4),
      repositories: [
        repoRow(),
        repoRow({
          repository: 'example/infra',
          ready: 0,
          needs_you: 0,
          enqueued: 0,
          running: 0,
          problem: 'checkout refused: tracks a denied file (fix: public_files)',
        }),
      ],
    },
    next: new Date(now + 3 * 3_600_000).toISOString(),
    pool_holds: [],
    needs_you_items: [
      {
        repository: 'example/app',
        issue: 42,
        title: 'Pick a retention period for old exports',
        reason: 'The issue asks which retention to keep; that is the owner’s call.',
        url: 'https://github.com/example/app/issues/42',
      },
    ],
    ...over,
  };
}

export function sweepSettings(over: Partial<SweepSettingsBody> = {}): SweepSettingsBody {
  return {
    contract_version: 'sweep-settings/v1',
    configured: true,
    time_zone: 'Europe/Berlin',
    enabled: { profile: true, effective: true },
    at: { profile: ['08:00', '20:00'], effective: ['08:00', '20:00'] },
    days: {
      profile: ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'],
      effective: ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'],
    },
    repositories: {
      'example/app': { profile: true, effective: true },
      'example/infra': { profile: true, effective: true },
    },
    subscription_hold_percent: { profile: 0, effective: 0 },
    ...over,
  };
}

/** What `PUT /v1/sweep/settings` answers: the settings with `change` applied; a field left out clears its override. */
export function sweepSettingsSet(
  settings: SweepSettingsBody,
  change: SweepSettingsChange,
): SweepSettingsBody {
  const pick = <T>(old: { profile: T; effective: T }, override: T | undefined) =>
    override === undefined
      ? { profile: old.profile, effective: old.profile }
      : { profile: old.profile, override, effective: override };
  return {
    ...settings,
    enabled: pick(settings.enabled, change.enabled),
    at: pick(settings.at, change.at),
    days: pick(settings.days, change.days),
    repositories: Object.fromEntries(
      Object.entries(settings.repositories).map(([name, old]) => [
        name,
        pick(old, change.repositories?.[name]),
      ]),
    ),
    subscription_hold_percent: pick(
      settings.subscription_hold_percent,
      change.subscription_hold_percent,
    ),
  };
}
