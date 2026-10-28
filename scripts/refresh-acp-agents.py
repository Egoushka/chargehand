#!/usr/bin/env python3
"""Bring the pinned versions in acp-agents.json up to date with the ACP registry (ADR 0086, decision 8).

Usage: scripts/refresh-acp-agents.py [--registry URL_OR_FILE] [--write]
Only agents that name a "registry_id" are touched, and only their "version" and launch "args" (the npm package with its version, then the
registry's own arguments, such as --acp). Everything else in an entry (the key variable, the read-only mode, whether it reports usage) is a
reviewed fact the registry does not carry, so the script never changes it and never adds an agent. Without --write it prints the changes.
chargehand never reads the registry at run time: review the diff, run the live check for the agent, then commit.
"""
import argparse, json, os, sys, urllib.request

REGISTRY = 'https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json'
FILE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'acp-agents.json')


def load_registry(source):
    if source.startswith(('http://', 'https://')):
        with urllib.request.urlopen(source, timeout=30) as r:
            return json.load(r)
    with open(source, encoding='utf-8') as f:
        return json.load(f)


def refreshed(entry, registry_agents):
    listed = registry_agents.get(entry['registry_id'])
    if listed is None:
        return None, f"{entry['id']}: '{entry['registry_id']}' is not in the registry"
    npx = listed.get('distribution', {}).get('npx')
    if npx is None:
        return None, f"{entry['id']}: the registry lists no npx distribution (only {', '.join(listed.get('distribution', {})) or 'none'}); update it by hand"
    return {**entry, 'version': listed['version'], 'args': ['-y', npx['package'], *npx.get('args', [])]}, None


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--registry', default=REGISTRY)
    ap.add_argument('--write', action='store_true')
    args = ap.parse_args()

    with open(FILE, encoding='utf-8') as f:
        pinned = json.load(f)
    registry = {a['id']: a for a in load_registry(args.registry)['agents']}
    problems, changed = [], False
    for i, entry in enumerate(pinned['agents']):
        if 'registry_id' not in entry:
            continue
        new, problem = refreshed(entry, registry)
        if problem:
            problems.append(problem)
        elif new != entry:
            changed = True
            print(f"{entry['id']}: {entry['version']} -> {new['version']}; args {entry['args']} -> {new['args']}")
            pinned['agents'][i] = new
    for p in problems:
        print(p, file=sys.stderr)
    if changed and args.write:
        with open(FILE, 'w', encoding='utf-8') as f:
            json.dump(pinned, f, indent=2)
            f.write('\n')
        print(f'wrote {FILE}')
    elif not changed and not problems:
        print('acp-agents.json is current')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
