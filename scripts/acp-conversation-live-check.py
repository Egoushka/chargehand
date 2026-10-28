#!/usr/bin/env python3
"""Live check of a conversation on an ACP agent (ADR 0086, Cycle A task 5): start, stream, a second message, an approval answered both ways, end.

Usage: scripts/acp-conversation-live-check.py --agent codex --repository OWNER/NAME [--commit SHA] [--url http://127.0.0.1:4300]
The server is `chargehand serve` with `http.conversations.runtime: "acp"` and a key for the agent (docs/guide/acp.md, "Conversations"). The server's API key is read from
the environment variable CHARGEHAND_API_KEY and never printed. Standard library only. The transcript it prints has the conversation id, the commit and absolute paths
replaced, so it can be attached to the pull request as it is. Exit code 0: every check passed (a check marked NOT EXERCISED does not fail the run).
Not run against Codex or Gemini CLI by this repository's maintainer: no key was bought for it. Run it on your machine and attach the output.
"""
import argparse, json, os, re, subprocess, sys, threading, time, urllib.error, urllib.request

QUESTIONS = [
    'Reply with the single word: ready.',
    'Read README.md and tell me, in one sentence, what this repository is. Name the file you read.',
    'Run `git log --oneline -1` in a shell and show me the one line it prints.',
    'Try that command again.',
]


class Stream(threading.Thread):
    """Reads /events with Server-Sent Events and keeps every event."""

    def __init__(self, base, key, conversation):
        super().__init__(daemon=True)
        self.events, self.lock, self.response = [], threading.Condition(), None
        request = urllib.request.Request(f'{base}/v1/conversations/{conversation}/events', headers={'Authorization': f'Bearer {key}'})
        self.response = urllib.request.urlopen(request, timeout=300)

    def run(self):
        kind = data = None
        try:
            for raw in self.response:
                line = raw.decode().rstrip('\n')
                if line.startswith('event: '):
                    kind = line[7:]
                elif line.startswith('data: '):
                    data = line[6:]
                elif not line and kind:
                    with self.lock:
                        self.events.append((kind, json.loads(data)['data']))
                        self.lock.notify_all()
                    kind = data = None
        except (OSError, ValueError) as e:
            reason = f'{type(e).__name__}: {e}'
        else:
            reason = 'the server closed the stream'
        with self.lock:
            self.events.append(('closed', {'reason': reason}))
            self.lock.notify_all()

    def wait(self, after, found, seconds):
        """The events from index `after` on, once `found(event)` holds for one of them; None on timeout."""
        deadline = time.monotonic() + seconds
        with self.lock:
            while True:
                if any(found(e) for e in self.events[after:]):
                    return self.events[after:]
                if time.monotonic() >= deadline or self.events and self.events[-1][0] == 'closed':
                    return None
                self.lock.wait(1)


def idle_after_turn(event):
    return event[0] == 'state' and event[1].get('state') == 'idle' and 'stop_reason' in event[1]


def asked(event):
    return event[0] == 'approval_requested'


def said(events):
    return ''.join(e[1]['text'] for e in events if e[0] == 'message_chunk' and e[1]['role'] == 'assistant')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--url', default='http://127.0.0.1:4300')
    ap.add_argument('--agent', required=True, help='the id in acp-agents.json, such as codex')
    ap.add_argument('--repository', required=True, help='a checkout the server reads, as owner/name or a path')
    ap.add_argument('--commit', help='default: HEAD of the current directory')
    ap.add_argument('--model', help='default: the agent\'s own')
    ap.add_argument('--preset', help='default: the server\'s default preset')
    ap.add_argument('--timeout', type=int, default=180, help='seconds each turn may take')
    ap.add_argument('--message', action='append', help='replaces the four messages, in order (the third and fourth should ask for a command)')
    args = ap.parse_args()
    key = os.environ.get('CHARGEHAND_API_KEY') or sys.exit('set CHARGEHAND_API_KEY to the server\'s API key (it is not printed)')
    commit = args.commit or subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True, check=True).stdout.strip()
    questions = args.message or QUESTIONS
    results, secrets = [], [commit, os.path.expanduser('~'), os.getcwd()]

    def call(method, path, body=None):
        request = urllib.request.Request(args.url + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                         headers={'Authorization': f'Bearer {key}', 'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=60) as r:
                return r.status, json.load(r)
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b'{}')

    def check(name, ok, detail=''):
        results.append((name, ok))
        print(f'{"PASS" if ok else "FAIL"}: {name}{" - " + detail if detail else ""}')
        return ok

    def scrub(text):
        for secret in sorted(secrets, key=len, reverse=True):
            text = text.replace(secret, '<scrubbed>')
        return re.sub(r'conv-[0-9a-z-]+', '<id>', text)

    start = {'repository': args.repository, 'commit': commit, 'runtime': f'acp:{args.agent}'}
    start.update({k: v for k, v in (('model', args.model), ('preset', args.preset)) if v})
    status, view = call('POST', '/v1/conversations', start)
    if not check('start: 201 and idle', status == 201 and view.get('state') == 'idle', scrub(f'status {status}, {json.dumps(view)[:300]}')):
        return 1
    conversation = view['id']
    secrets.append(conversation)
    print(f'model: {view.get("model")}')
    stream = Stream(args.url, key, conversation)
    stream.start()

    def turn(text, name):
        before = len(stream.events)
        status, body = call('POST', f'/v1/conversations/{conversation}/messages', {'text': text})
        if not check(f'{name}: message accepted (202)', status == 202, scrub(json.dumps(body)[:200])):
            return None, before
        events = stream.wait(before, lambda e: idle_after_turn(e) or asked(e), args.timeout)
        return events, before

    for number, name in ((0, 'first message'), (1, 'second message')):
        events, _ = turn(questions[number], name)
        ok = events is not None and any(idle_after_turn(e) for e in events) and bool(said(events).strip())
        check(f'{name}: the agent answers and the turn ends', ok)
        print(f'  agent: {scrub(said(events or []))[:300]!r}')
    tools = [e for e in stream.events if e[0] == 'tool_call']
    print(f'  tool calls so far: {[(t[1]["kind"], t[1]["title"][:60]) for t in tools]}')
    _, view = call('GET', f'/v1/conversations/{conversation}')
    check('read: two messages counted, idle', view.get('messages') == 2 and view.get('state') == 'idle', f'messages {view.get("messages")}, state {view.get("state")}')
    print(f'  usage: {view.get("usage") or "UNKNOWN: the agent reported none"}')

    for decision, number in (('reject', 2), ('allow_once', 3)):
        if number >= len(questions):
            continue
        events, before = turn(questions[number], f'message {number + 1}')
        request = next((e for e in events or [] if asked(e)), None)
        if request is None:
            check(f'approval {decision}', True, 'NOT EXERCISED: the agent did not ask (a read-only agent often answers a command itself or declines it)')
            print(f'  agent: {scrub(said(events or []))[:300]!r}')
            continue
        call_id = request[1]['call_id']
        status, body = call('POST', f'/v1/conversations/{conversation}/approvals/{call_id}', {'decision': decision})
        check(f'approval {decision}: asked for {request[1]["kind"]} "{scrub(request[1]["title"])[:60]}" and answered', status == 200, f'status {status}')
        events = stream.wait(before, idle_after_turn, args.timeout)
        check(f'approval {decision}: the turn goes on and ends', events is not None)
        print(f'  agent: {scrub(said(events or []))[:300]!r}')

    status, body = call('DELETE', f'/v1/conversations/{conversation}')
    check('end: ended by the user', status == 200 and body.get('state') == 'ended', f'status {status}')
    ended = stream.wait(0, lambda e: e[0] == 'ended', 30)
    check('stream: closes with an ended event', ended is not None)
    print('\ntranscript (ids and paths replaced):')
    for kind, data in stream.events:
        if kind == 'message_chunk':
            continue
        print(' ', scrub(f'{kind}: {json.dumps(data)[:200]}'))
    failed = [n for n, ok in results if not ok]
    print(f'\n{len(results) - len(failed)} of {len(results)} checks passed' + (f'; failed: {failed}' if failed else ''))
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
