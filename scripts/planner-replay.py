#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""Replay prototype for the batch planner (ADR 0094, issue 756). Read-only, no model calls, no cost.

Usage:
  scripts/planner-replay.py fetch [--since 2026-11-06] [--out runs/planner-replay.json]   needs gh
  scripts/planner-replay.py replay [--in runs/planner-replay.json] [--json]

`fetch` records, for every pull request chargehand opened since a date, its issue, files, commit subjects and failed checks, plus the issue
text. `replay` runs heuristics over the issue text only (what a planner sees before work) and scores them against what the run did.
"""
import argparse, json, re, subprocess, sys
from itertools import combinations

REPO = 'Egoushka/chargehand'
SCRATCH = re.compile(r'(^|/)(scratch|tmp|notes?|todo|plan|debug)[^/]*$|\.(log|orig|rej|bak)$|(Before|After|Tmp|Spike)\.[a-z]+$', re.I)
SUBJECT_MAX = 100
CONVENTIONAL = re.compile(r'^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: .{1,%d}$' % SUBJECT_MAX)
PATH_RE = re.compile(r'`?((?:src|docs|scripts|schemas|tests|deploy|web|evals|profiles)/[\w./-]+|[\w-]+\.(?:cs|md|json|sh|py|yml|yaml))`?')
IDENT_RE = re.compile(r'`([A-Z][A-Za-z0-9]{3,}(?:\.[A-Za-z]+)?)`')
WORD_RE = re.compile(r'[a-z]{5,}')
STOP = set('should would which their there about after before issue chargehand pull request requests intake driven which when with that this from into does'.split())
# Events no pull request shows (from the owner's bulk-run notes); listed so the score says what a replay over gh cannot see.
UNSEEN = ['exit-126 chronicle run (no pull request)', 'two stalled runs (no pull request)']


def gh(*args):
    r = subprocess.run(['gh', *args], capture_output=True, text=True)
    if r.returncode:
        sys.exit(f'gh {" ".join(args[:3])}: {r.stderr.strip()}')
    return json.loads(r.stdout)


def fetch(since, out):
    prs = gh('pr', 'list', '-R', REPO, '--state', 'all', '--limit', '100', '--search', f'head:chargehand/ created:>={since}',
             '--json', 'number')
    rows = []
    for n in prs:
        p = gh('pr', 'view', str(n['number']), '-R', REPO, '--json', 'number,title,state,mergedAt,body,files,commits,statusCheckRollup')
        m = re.search(r'[Ii]ssue #(\d+)', p['body'] or '') or re.search(r'(?:Closes|Fixes) #(\d+)', p['body'] or '')
        issue = int(m.group(1)) if m else None
        rows.append({
            'pr': p['number'], 'issue': issue, 'title': p['title'], 'state': p['state'], 'merged': bool(p['mergedAt']),
            'partial': p['title'].startswith('[partial]'),
            'files': [f['path'] for f in p['files']],
            'subjects': [c['messageHeadline'] for c in p['commits']],
            'failed_checks': sorted({c['name'] for c in p['statusCheckRollup'] if c.get('conclusion') == 'FAILURE'}),
        })
    issues = {}
    for r in rows:
        if r['issue'] and r['issue'] not in issues:
            i = gh('issue', 'view', str(r['issue']), '-R', REPO, '--json', 'number,title,body,state,labels')
            issues[r['issue']] = {'number': i['number'], 'title': i['title'], 'body': i['body'], 'state': i['state'],
                                  'labels': [l['name'] for l in i['labels']]}
    tree = subprocess.run(['git', 'ls-files'], capture_output=True, text=True).stdout.split()
    json.dump({'since': since, 'runs': rows, 'issues': issues, 'tree': tree}, open(out, 'w'), indent=1)
    print(f'{len(rows)} runs, {len(issues)} issues -> {out}')


def scope(issue, tree):
    text = (issue['title'] or '') + '\n' + (issue['body'] or '')
    files = set()
    for m in PATH_RE.findall(text):
        files |= {t for t in tree if t == m or t.endswith('/' + m)}
    for m in IDENT_RE.findall(text):
        files |= {t for t in tree if t.rsplit('/', 1)[-1].split('.')[0] == m.split('.')[0]}
    return files


def words(issue):
    t = (issue['title'] or '').lower()
    return {w for w in WORD_RE.findall(t) if w not in STOP}


def plan(issues, tree):
    """The planner's verdict per issue, from issue text and the repository tree only."""
    out = {}
    for n, i in issues.items():
        body = i['body'] or ''
        subj = f'feat(intake): {i["title"]}'
        out[n] = {
            'scope': scope(i, tree), 'words': words(i),
            'needs_spec': not re.search(r'(^|\n)#+\s*(check|acceptance|(?:behavior|behaviour)|scope|done)', body, re.I) and len(body) < 400,
            'long_subject': len(subj) > SUBJECT_MAX,
            'epic': len(re.findall(r'\n\s*(?:[-*]|\d+\.)\s', body)) > 12,
            'delivered': i['state'] == 'CLOSED',
        }
    for a, b in combinations(out, 2):
        pa, pb = out[a], out[b]
        shared = pa['scope'] & pb['scope']
        sim = len(pa['words'] & pb['words'])
        if shared or sim >= 3:
            pa.setdefault('with', set()).add(b)
            pb.setdefault('with', set()).add(a)
    return out


def replay(path, as_json):
    d = json.load(open(path))
    issues = {int(k): v for k, v in d['issues'].items()}
    plans = plan(issues, d['tree'])
    runs = [r for r in d['runs'] if r['issue']]
    byissue = {r['issue']: r for r in runs}
    rows, hit, total = [], 0, 0

    def code(r):  # source files only: docs, tests, schemas and snapshots collide too, but a rebase fixes them
        return {f for f in r['files'] if f.startswith('src/')}
    real = {n: {m for m, o in byissue.items() if m != n and code(o) & code(r)} for n, r in byissue.items()}
    for n, r in byissue.items():
        p = plans.get(n)
        if not p:
            continue
        wasted = bool(r['failed_checks']) or r['partial'] or (not r['merged'] and r['state'] != 'OPEN')
        flags = {'title_check': 'title' in r['failed_checks'], 'other_check': bool(set(r['failed_checks']) - {'title'}),
                 'partial': r['partial'], 'scratch': any(SCRATCH.search(f) for f in r['files']), 'conflict': bool(real[n])}
        predicted = {'title_check': not CONVENTIONAL.match(issues[n]['title']), 'other_check': False,
                     'partial': p['epic'] or p['needs_spec'], 'scratch': False, 'conflict': bool(p.get('with'))}
        for k in flags:
            total += 1
            hit += flags[k] == predicted[k]
        rows.append({'issue': n, 'pr': r['pr'], 'state': r['state'], 'merged': r['merged'], 'wasted': wasted, 'actual': flags, 'predicted': predicted,
                     'cluster_with': sorted(p.get('with', [])), 'real_overlap': sorted(real[n])})
    tp = sum(1 for r in rows for k in r['actual'] if r['actual'][k] and r['predicted'][k])
    fp = sum(1 for r in rows for k in r['actual'] if not r['actual'][k] and r['predicted'][k])
    fn = sum(1 for r in rows for k in r['actual'] if r['actual'][k] and not r['predicted'][k])
    summary = {'runs': len(rows), 'wasted_runs': sum(r['wasted'] for r in rows), 'accuracy': round(hit / total, 2) if total else None,
               'caught': tp, 'false_alarms': fp, 'missed': fn, 'unseen_by_gh': UNSEEN,
               'cost_usd': 0.0, 'rows': rows}
    if as_json:
        print(json.dumps(summary, indent=1))
        return
    for r in rows:
        a = ','.join(k for k, v in r['actual'].items() if v) or '-'
        p = ','.join(k for k, v in r['predicted'].items() if v) or '-'
        print(f'#{r["issue"]:<4} PR{r["pr"]:<4} {"merged" if r["merged"] else r["state"]:<7} actual[{a}] predicted[{p}] cluster{r["cluster_with"]} real{r["real_overlap"]}')
    print({k: v for k, v in summary.items() if k != 'rows'})


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    f = sub.add_parser('fetch')
    f.add_argument('--since', default='2026-11-06')
    f.add_argument('--out', default='runs/planner-replay.json')
    r = sub.add_parser('replay')
    r.add_argument('--in', dest='inp', default='runs/planner-replay.json')
    r.add_argument('--json', action='store_true')
    a = ap.parse_args()
    fetch(a.since, a.out) if a.cmd == 'fetch' else replay(a.inp, a.json)


if __name__ == '__main__':
    main()
