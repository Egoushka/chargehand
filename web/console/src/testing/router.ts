import type { RouterBody } from '../api/router';

/** A `router/v1` body: a free pool and a fixed one, two routes. */
export function routerBody(over: Partial<RouterBody> = {}): RouterBody {
  return {
    contract_version: 'router/v1',
    generated_at: '2026-11-10T12:00:00Z',
    enabled: true,
    time_zone: 'UTC',
    default_tier: 'cheap',
    pools: [
      {
        name: 'free',
        kind: 'free',
        models: ['free/small'],
        spent_period_usd: 0,
        spent_today_usd: 0,
        has_room: true,
      },
      {
        name: 'bought',
        kind: 'fixed',
        models: ['paid/big'],
        period_start: '2026-11-01T00:00:00Z',
        remaining_usd: 23,
        allowance_usd: 1.77,
        room_usd: 0.77,
        spent_period_usd: 7,
        spent_today_usd: 1,
        has_room: true,
      },
    ],
    routes: [
      { key: 'driven', tier: 'cheap', models: ['free/small'] },
      { key: 'review', tier: 'strong', models: ['paid/big'] },
    ],
    ...over,
  };
}
