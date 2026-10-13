#!/usr/bin/env python3
"""A stand-in for the pull request endpoints of GitHub, for a local benchmark run (docs/guide/benchmark.md, "Run it on one machine").

With `--remote file:///...` the server pushes branches to a bare repository on this machine, but opening the draft pull request is still an HTTP
call. This answers it: POST /repos/{owner}/{repo}/pulls gets a pull request number and URL, PATCH edits it, GET lists none. It keeps nothing, checks
no credential and listens on loopback only. Start the server with CHARGEHAND_E2E_GITHUB_API=http://127.0.0.1:<port>/ and CHARGEHAND_E2E_LOCAL_REMOTE=1.

usage: scripts/bench-forge-stub.py [port]      (default 4391)
"""
import http.server
import itertools
import json
import sys

numbers = itertools.count(1)


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        length = int(self.headers.get("Content-Length", 0))
        return json.loads(self.rfile.read(length) or b"{}")

    def do_POST(self):
        number = next(numbers)
        self.body()
        self.reply(201, {"html_url": f"https://pulls.example.invalid/{number}", "number": number})

    def do_PATCH(self):
        self.body()
        last = self.path.split("?")[0].rstrip("/").split("/")[-1]
        self.reply(200, {"number": int(last) if last.isdigit() else 0})

    def do_GET(self):
        self.reply(200, [])


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 4391
    print(f"pull request stub on http://127.0.0.1:{port}/", flush=True)
    http.server.HTTPServer(("127.0.0.1", port), Handler).serve_forever()
