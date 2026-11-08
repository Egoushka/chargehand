#!/usr/bin/env python3
"""Replay finished work through the budget-aware model router (ADR 0095), offline: no model call, no network, nothing written.

Usage: scripts/router-replay.py [--run-log runs/run-log.jsonl] [--learning runs/learning.jsonl] [--profile profiles/example.json] [--states STATES.json]
       [--free-daily-requests N] [--json]

What it replays, in time order:
  - every priced call in the run log (intake, worker, draft); a worker call of a review preset is a `review`, any other a `research`;
  - every driven task in the learning records, as `code` with its size class.
Each unit gets the tier `pools.routes` gives it and the first pool, in drain order, with room that day; it is re-priced at the chosen model's price per
million tokens from `prices` (a free pool costs nothing; a model with no price keeps the unit's actual cost). A unit no pool can take waits until the
next day or refill, as the router would make it.

It prints per pool the projected spend and the days a pool's allowance ran out, and per pull request the projected against the actual cost.
--states is a JSON object of pull request URL to state (merged, closed, open); without it every pull request the records name counts as merged, and
the output says so. Escalation is not replayed: the records keep no per-attempt verify results.

Exit code 1: the projected cost of the merged set is above its actual cost. 2: bad input. Standard library only.
"""
import argparse, bisect, calendar, json, sys
from collections import defaultdict
from datetime import date, datetime, time, timedelta
from zoneinfo import ZoneInfo

WEEKDAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday']
MAX_WAIT = timedelta(days=62)


def parse_time(text):
    return datetime.fromisoformat(text.replace('Z', '+00:00'))


def local(wall, zone):
    return wall.replace(tzinfo=zone)


def day_start(at, zone):
    return local(datetime.combine(at.astimezone(zone).date(), time()), zone)


class Pool:
    def __init__(self, raw):
        self.name, self.kind, self.models = raw['name'], raw['kind'], raw['models']
        self.total, self.quota, self.daily_requests = raw.get('total_usd'), raw.get('quota_usd'), raw.get('daily_requests')
        self.start = date.fromisoformat(raw['start']) if raw.get('start') else None
        self.spread, self.expires = raw.get('spread_days'), (date.fromisoformat(raw['expires']) if raw.get('expires') else None)
        self.refill_day, self.weekday = raw.get('refill_day', 1), raw.get('refill_weekday')
        self.refill_at = time.fromisoformat(raw.get('refill_time', '00:00'))

    def refill_in(self, year, month, zone):
        day = min(self.refill_day, calendar.monthrange(year, month)[1])
        return local(datetime.combine(date(year, month, day), self.refill_at), zone)

    def period(self, now, zone):
        """(start, refill, horizon, balance) for the period now falls in; mirrors PoolBalance.Period."""
        if self.kind == 'fixed':
            start = local(datetime.combine(self.start, time()), zone) if self.start else None
            ends = [local(datetime.combine(d, time()), zone) for d in (self.start + timedelta(days=self.spread) if self.start and self.spread else None, self.expires) if d]
            horizon = min(ends) if ends else None
            return start, None, horizon if horizon and horizon > now else None, self.total
        if self.kind == 'monthly':
            wall = now.astimezone(zone)
            last = self.refill_in(wall.year, wall.month, zone)
            if last > now:
                y, m = (wall.year, wall.month - 1) if wall.month > 1 else (wall.year - 1, 12)
                last = self.refill_in(y, m, zone)
            y, m = (last.year, last.month + 1) if last.month < 12 else (last.year + 1, 1)
            nxt = self.refill_in(y, m, zone)
            return last, nxt, nxt, self.total
        if self.kind == 'weekly':
            wall = now.astimezone(zone)
            back = (wall.weekday() - WEEKDAYS.index(self.weekday)) % 7
            last = local(datetime.combine(wall.date() - timedelta(days=back), self.refill_at), zone)
            if last > now:
                last -= timedelta(days=7)
            return last, last + timedelta(days=7), last + timedelta(days=7), self.quota
        return day_start(now, zone), None, None, None

    def room(self, spend, now, zone, estimate):
        """(has room, why, refill); mirrors PoolBalance.Of without the exhausted flag."""
        today = day_start(now, zone)
        start, refill, horizon, balance = self.period(now, zone)
        if self.kind == 'fixed' and self.expires and now >= local(datetime.combine(self.expires, time()), zone):
            return False, 'expired', None
        if start and start > now:
            return False, 'not started', start
        mine = [(at, usd) for at, usd in spend if (start is None or at >= start) and at <= now]
        if self.kind == 'free':
            used = sum(1 for at, _ in mine if at >= today)
            return (self.daily_requests is None or used < self.daily_requests), 'requests used', refill
        if balance is None:
            return True, None, refill
        before = sum(usd for at, usd in mine if at < today)
        spent_today = sum(usd for at, usd in mine if at >= today)
        remaining = balance - before - spent_today
        days = max(1, -(-(horizon - today) // timedelta(days=1))) if horizon else 1
        allowance = (balance - before) / days if horizon else balance - before
        room = min(allowance - spent_today, remaining)
        if room > 0 and room >= estimate:
            return True, None, refill
        return False, 'empty' if remaining <= 0 else 'allowance spent', refill


def price(prices, model, tokens, actual):
    p = prices.get(model)
    if p is None:
        return actual, False
    return sum(tokens.get(k, 0) * p.get(k, 0) for k in ('input', 'output', 'cache_read', 'cache_write')) / 1e6, True


def load_units(run_log, learning):
    presets, units, prs = {}, [], defaultdict(set)
    rows = [json.loads(line) for line in open(run_log) if line.strip()]
    for r in rows:
        if r.get('type') in ('start', 'run'):
            presets[r['run_id']] = r.get('preset') or (r.get('request') or {}).get('context', {}).get('preset')
        if r.get('type') == 'run':
            for a in (r.get('result') or {}).get('artifacts') or []:
                content = json.loads(a['content']) if isinstance(a.get('content'), str) and a['content'].startswith('{') else {}
                if a.get('kind') == 'pull-request' and content.get('url'):
                    prs[r['run_id']].add(content['url'])
                for t in (content.get('tasks') or []) if a.get('kind') == 'driven-batch' else []:
                    if t.get('pr_url') and t.get('run_id'):
                        prs[t['run_id']].add(t['pr_url'])
    tasks = [json.loads(line) for line in open(learning) if line.strip()] if learning else []
    driven = {t['run_id'] for t in tasks}
    for r in rows:
        if r.get('type') != 'call' or r.get('usd') is None or r['run_id'] in driven:
            continue
        kind = {'intake': 'intake', 'draft': 'draft'}.get(r.get('kind'), 'review' if 'review' in (presets.get(r['run_id']) or '') else 'research')
        units.append(dict(run=r['run_id'], at=parse_time(r['started']), kind=kind, size=None, tokens=r.get('tokens') or {}, actual=r['usd']))
    for t in tasks:
        s = t.get('spend') or {}
        tokens = {'input': s.get('input_tokens', 0), 'output': s.get('output_tokens', 0), 'cache_read': s.get('cache_read_tokens', 0), 'cache_write': s.get('cache_write_tokens', 0)}
        units.append(dict(run=t['run_id'], at=parse_time(t['at']), kind='code', size=t.get('size_class'), tokens=tokens, actual=s.get('usd_estimate') or 0))
    return sorted(units, key=lambda u: u['at']), prs


def tier_for(cfg, kind, size):
    routes = cfg.get('routes') or {}
    name = (routes.get(f'{kind}/{size}') if size else None) or routes.get(kind) or cfg.get('default_tier')
    names = [t['name'] for t in cfg['tiers']]
    return names.index(name) if name in names else 0


def replay(units, cfg, prices):
    zone = ZoneInfo(cfg.get('time_zone', 'UTC'))
    pools = [Pool(p) for p in cfg['order']]
    spend = {p.name: [] for p in pools}
    out = {p.name: dict(usd=0.0, units=0, out_days=set()) for p in pools}
    waits, dropped, unpriced = 0, 0, 0
    queue = [(u['at'], i) for i, u in enumerate(units)]
    while queue:
        now, i = queue.pop(0)
        u = units[i]
        tier = cfg['tiers'][tier_for(cfg, u['kind'], u['size'])]
        until = day_start(now, zone) + timedelta(days=1)
        for pool in pools:
            model = next((m for m in tier['models'] if m in pool.models), None)
            if model is None:
                continue
            cost, priced = (0.0, True) if pool.kind == 'free' else price(prices, model, u['tokens'], u['actual'])
            ok, why, refill = pool.room(spend[pool.name], now, zone, cost)
            if ok:
                spend[pool.name].append((now, cost))
                out[pool.name]['usd'] += cost
                out[pool.name]['units'] += 1
                unpriced += not priced
                u.update(pool=pool.name, model=model, projected=cost, ran=now)
                break
            out[pool.name]['out_days'].add(now.astimezone(zone).date())
            if refill and now < refill < until:
                until = refill
        else:
            waits += 1
            if until - u['at'] > MAX_WAIT:
                dropped += 1
                u.update(pool=None, model=None, projected=0.0, ran=None)
                continue
            bisect.insort(queue, (until, i))
    return out, waits, dropped, unpriced


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--run-log', default='runs/run-log.jsonl')
    ap.add_argument('--learning', default='runs/learning.jsonl')
    ap.add_argument('--profile', default='profiles/example.json')
    ap.add_argument('--states', help='JSON object: pull request URL to merged, closed or open')
    ap.add_argument('--free-daily-requests', type=int, help='assume this daily quota for a free pool that sets none (the real quotas are unknown)')
    ap.add_argument('--json', action='store_true')
    a = ap.parse_args()
    try:
        profile = json.load(open(a.profile))
        cfg, prices = profile.get('pools') or {}, profile.get('prices') or {}
        if not cfg.get('tiers') or not cfg.get('order'):
            raise ValueError(f'{a.profile} has no pools.tiers or pools.order')
        if a.free_daily_requests:
            for pool in cfg['order']:
                if pool['kind'] == 'free' and pool.get('daily_requests') is None:
                    pool['daily_requests'] = a.free_daily_requests
        units, prs = load_units(a.run_log, a.learning)
        states = json.load(open(a.states)) if a.states else None
    except (OSError, ValueError, KeyError) as e:
        print(f'router-replay: {e}', file=sys.stderr)
        return 2
    pools, waits, dropped, unpriced = replay(units, cfg, prices)
    per_pr = defaultdict(lambda: [0.0, 0.0])
    for u in units:
        for url in prs.get(u['run'], ()):
            if states is None or states.get(url) == 'merged':
                per_pr[url][0] += u['projected']
                per_pr[url][1] += u['actual']
    projected, actual = sum(v[0] for v in per_pr.values()), sum(v[1] for v in per_pr.values())
    report = dict(units=len(units), waits=waits, dropped=dropped, unpriced=unpriced, merged_assumed=states is None,
                  pools={k: dict(usd=round(v['usd'], 4), units=v['units'], out_days=sorted(str(d) for d in v['out_days'])) for k, v in pools.items()},
                  pull_requests={k: dict(projected=round(v[0], 4), actual=round(v[1], 4)) for k, v in sorted(per_pr.items())},
                  merged_projected=round(projected, 4), merged_actual=round(actual, 4),
                  total_projected=round(sum(u['projected'] for u in units), 4), total_actual=round(sum(u['actual'] for u in units), 4))
    if a.json:
        print(json.dumps(report, indent=2))
    else:
        print(f"units replayed: {report['units']} (waited {waits}, dropped after {MAX_WAIT.days} days {dropped}, priced at actual for want of a price {unpriced})")
        print(f"all units: projected ${report['total_projected']:.4f}, actual ${report['total_actual']:.4f}\n")
        print(f"{'pool':<16}{'units':>7}{'projected $':>14}  days its allowance ran out")
        for name, p in report['pools'].items():
            print(f"{name:<16}{p['units']:>7}{p['usd']:>14.4f}  {', '.join(p['out_days']) or '-'}")
        print(f"\npull requests ({'every one counted as merged: no --states' if states is None else 'merged per --states'}):")
        for url, v in report['pull_requests'].items():
            print(f"  {url}  projected ${v['projected']:.4f}  actual ${v['actual']:.4f}")
        print(f"merged set: projected ${projected:.4f}, actual ${actual:.4f} -> {'FAIL: projected above actual' if projected > actual else 'ok'}")
    return 1 if projected > actual else 0


if __name__ == '__main__':
    sys.exit(main())
