#!/usr/bin/env python3
"""Smoke test of the packed dnx tool (ADR 0027): run it the way an MCP client does and list its tools.

Usage: scripts/mcp-smoke.py <dir-with-nupkg>
Runs `dotnet dnx Chargehand@<Version> --source <dir> -- mcp` from an empty directory, with packages restored into a
throwaway NUGET_PACKAGES, CHARGEHAND_RUNTIME=claude_code, a dummy token and a fake `claude` that only answers
--version. Sends initialize and tools/list over stdio and expects the orchestrate tool. No model is called.
Also checks that the packed .mcp/server.json carries the version from Directory.Build.props.
"""
import json, os, re, subprocess, sys, tempfile, zipfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.path.abspath(sys.argv[1])

def grep(path, pattern):
    with open(os.path.join(root, path)) as f:
        return re.search(pattern, f.read()).group(1)

version = grep('Directory.Build.props', r'<Version>(.*?)</Version>')
claude_version = grep('src/Chargehand/Config/Profile.cs', r'PinnedVersion = "(.*?)"')

with zipfile.ZipFile(os.path.join(source, f'Chargehand.{version}.nupkg')) as z:
    server = json.loads(z.read('.mcp/server.json'))
versions = [server['version']] + [p['version'] for p in server['packages']]
assert versions == [version] * len(versions), f'packed .mcp/server.json versions {versions}, expected {version}'

with tempfile.TemporaryDirectory() as tmp:
    bin_dir, work = os.path.join(tmp, 'bin'), os.path.join(tmp, 'work')
    os.makedirs(bin_dir)
    os.makedirs(work)
    fake = os.path.join(bin_dir, 'claude')
    with open(fake, 'w') as f:
        f.write(f'#!/bin/sh\necho "{claude_version} (Claude Code)"\n')
    os.chmod(fake, 0o755)
    env = {k: v for k, v in os.environ.items() if k not in ('ANTHROPIC_API_KEY', 'CHARGEHAND_PROFILE')}
    env.update(PATH=bin_dir + os.pathsep + env['PATH'], NUGET_PACKAGES=os.path.join(tmp, 'packages'),
               CHARGEHAND_RUNTIME='claude_code', CLAUDE_CODE_OAUTH_TOKEN='dummy', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    proc = subprocess.Popen(['dotnet', 'dnx', f'Chargehand@{version}', '--yes', '--source', source, '--', 'mcp'],
                            cwd=work, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)

    def send(message):
        proc.stdin.write(json.dumps(message) + '\n')
        proc.stdin.flush()

    def reply(id):
        for line in proc.stdout:
            # dnx itself prints "Skipping NuGet package signature verification." to stdout on a first install
            # (SDK 10.0.302); clients that drop non-JSON lines cope, so the smoke test notes it and reads on.
            if not line.startswith('{'):
                print(f'non-JSON line on stdout: {line.rstrip()}', file=sys.stderr)
                continue
            message = json.loads(line)
            if message.get('id') == id:
                assert 'result' in message, f'error reply: {message}'
                return message['result']
        sys.exit(f'chargehand mcp exited {proc.wait()} before replying to request {id}')

    send({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
        'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'mcp-smoke', 'version': '0'}}})
    info = reply(1)['serverInfo']
    assert info['name'] == 'chargehand' and info['version'].startswith(version), f'serverInfo {info}'
    send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
    send({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/list'})
    tools = [t['name'] for t in reply(2)['tools']]
    assert tools == ['orchestrate'], f'tools {tools}'
    proc.stdin.close()
    assert proc.wait(timeout=30) == 0, f'chargehand mcp exited {proc.returncode}'

print(f'ok: Chargehand {version} over dnx: serverInfo {info["version"]}, tools {tools}')
