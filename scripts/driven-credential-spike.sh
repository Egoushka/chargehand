#!/usr/bin/env bash
# Spike for ADR 0039 decision 9 (plan Task 1): can Claude Code run on a substituted credential?
# A local listener sits where the model API would be. It logs header NAMES, booleans and destination hosts, never a header value or a body,
# and (in forward mode) swaps the dummy credential for the real one and relays the request to the real API. The real token is read from the
# macOS Keychain into the listener's memory and goes nowhere else. Output goes to a log file outside the repository.
#
#   driven-credential-spike.sh listen  <log> [port]   log only; answers every API call with 401; refuses CONNECTs (logged)
#   driven-credential-spike.sh forward <log> [port]   swap the dummy for the real token and relay to the API (needs CHARGEHAND_SPIKE_OAUTH_ITEM)
#   driven-credential-spike.sh run <listen|forward> [extra env, e.g. CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1]
#       starts the listener, runs one trivial `claude -p` against it in an empty config dir, prints the log's summary, stops the listener.
# env: CHARGEHAND_SPIKE_OAUTH_ITEM  Keychain item name holding the subscription token (forward mode)
#      CHARGEHAND_SPIKE_DIR         where logs and the throwaway config go (default: a mktemp dir, kept)
set -euo pipefail

mode="${1:?usage: $0 listen|forward|run ...}"; shift

serve() { # $1 = listen|forward, $2 = log, $3 = port
exec python3 - "$1" "$2" "${3:-0}" <<'PY'
import http.client, http.server, json, os, ssl, subprocess, sys, threading, time

kind, log_path, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
dummy = os.environ["CHARGEHAND_SPIKE_DUMMY"]
real = None
if kind == "forward":
    item = os.environ["CHARGEHAND_SPIKE_OAUTH_ITEM"]
    real = subprocess.run(["security", "find-generic-password", "-s", item, "-w"],
                          capture_output=True, text=True, check=True).stdout.strip()
lock = threading.Lock()

def log(**ev):
    ev["t"] = round(time.time(), 2)
    with lock, open(log_path, "a") as f:
        f.write(json.dumps(ev) + "\n")

def is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)

class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"
    def log_message(self, *a): pass

    def do_CONNECT(self):
        log(ev="connect", host=self.path)  # an HTTPS_PROXY client names the host it wants; nothing is tunnelled
        self.send_response(403); self.end_headers()

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n: return self.rfile.read(n)
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            out = b""
            while True:
                size = int(self.rfile.readline().strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline(); return out
                out += self.rfile.read(size); self.rfile.readline()
        return b""

    def handle_any(self):
        body = self._body()
        names = sorted({k.lower() for k in self.headers.keys()})
        auth = self.headers.get("Authorization") or ""
        scheme = auth.split(" ", 1)[0] if auth else None
        tok = auth.split(" ", 1)[1] if " " in auth else ""
        xkey = self.headers.get("x-api-key")
        beta = self.headers.get("anthropic-beta") or ""
        log(ev="request", method=self.command, path=self.path.split("?")[0], header_names=names,
            authorization_present=bool(auth), authorization_scheme=scheme, authorization_equals_dummy=(tok == dummy),
            x_api_key_present=xkey is not None, x_api_key_equals_dummy=(xkey == dummy) if xkey is not None else None,
            anthropic_beta_has_oauth=("oauth" in beta), body_bytes=len(body))
        if kind == "listen":
            msg = b'{"type":"error","error":{"type":"authentication_error","message":"spike listener"}}'
            self.send_response(401); self.send_header("content-type", "application/json")
            self.send_header("content-length", str(len(msg))); self.end_headers(); self.wfile.write(msg)
            return
        hdr = {k: v for k, v in self.headers.items()
               if k.lower() not in ("host", "authorization", "x-api-key", "accept-encoding", "content-length", "connection")}
        hdr["Authorization"] = "Bearer " + real
        hdr["Accept-Encoding"] = "identity"
        hdr["Content-Length"] = str(len(body))
        c = http.client.HTTPSConnection("api.anthropic.com", 443, context=ssl.create_default_context(), timeout=300)
        try:
            c.request(self.command, self.path, body=body, headers=hdr)
            r = c.getresponse()
            self.send_response(r.status)
            for k, v in r.getheaders():
                if k.lower() not in ("transfer-encoding", "connection", "content-length", "content-encoding"):
                    self.send_header(k, v)
            self.end_headers()
            is_sse = "text/event-stream" in (r.getheader("content-type") or "")
            seen = {"events": 0, "message_start_usage_ints": None, "message_delta_usage_ints": None, "cache_fields_ints": None}
            buf = b""
            while True:
                chunk = r.read1(65536) if hasattr(r, "read1") else r.read(65536)
                if not chunk: break
                self.wfile.write(chunk); self.wfile.flush()
                if is_sse:
                    buf += chunk
                    while b"\n" in buf:
                        line, buf = buf.split(b"\n", 1)
                        if not line.startswith(b"data:"): continue
                        try: d = json.loads(line[5:])
                        except Exception: continue
                        seen["events"] += 1
                        t = d.get("type")
                        if t == "message_start":
                            u = (d.get("message") or {}).get("usage") or {}
                            seen["message_start_usage_ints"] = is_int(u.get("input_tokens")) and is_int(u.get("output_tokens"))
                            seen["cache_fields_ints"] = all(is_int(u.get(k)) for k in ("cache_creation_input_tokens", "cache_read_input_tokens"))
                        elif t == "message_delta":
                            u = d.get("usage") or {}
                            seen["message_delta_usage_ints"] = is_int(u.get("output_tokens"))
            log(ev="response", path=self.path.split("?")[0], status=r.status, sse=is_sse, **seen)
        except Exception as e:
            log(ev="forward_error", error=type(e).__name__)
            try: self.send_response(502); self.end_headers()
            except Exception: pass
        finally:
            c.close()

    do_GET = do_POST = do_PUT = do_DELETE = do_HEAD = handle_any

srv = http.server.ThreadingHTTPServer(("127.0.0.1", port), H)
log(ev="listening", port=srv.server_address[1], kind=kind)
print(srv.server_address[1], flush=True)
srv.serve_forever()
PY
}

case "$mode" in
  listen|forward)
    export CHARGEHAND_SPIKE_DUMMY="${CHARGEHAND_SPIKE_DUMMY:?set by the run mode, or any random string}"
    serve "$mode" "${1:?log path}" "${2:-0}" ;;
  run)
    kind="${1:?listen|forward}"; shift
    dir="${CHARGEHAND_SPIKE_DIR:-$(mktemp -d)}"; mkdir -p "$dir/cfg" "$dir/home"
    log="$dir/$kind-$(date +%H%M%S).log"
    dummy="spike-dummy-$(openssl rand -hex 8)"; export CHARGEHAND_SPIKE_DUMMY="$dummy"
    portfile="$dir/port"
    "$0" "$kind" "$log" 0 > "$portfile" &
    lpid=$!; trap 'kill $lpid 2>/dev/null || true' EXIT
    for _ in $(seq 50); do [ -s "$portfile" ] && break; sleep 0.2; done
    port="$(cat "$portfile")"; [ -n "$port" ] || { echo "listener did not start" >&2; exit 1; }
    # An HTTPS_PROXY pointing at the listener makes every other host the process wants show up as a logged CONNECT (it gets a 403).
    env -i PATH="$PATH" HOME="$dir/home" CLAUDE_CONFIG_DIR="$dir/cfg" \
      CLAUDE_CODE_OAUTH_TOKEN="$dummy" ANTHROPIC_BASE_URL="http://127.0.0.1:$port" \
      HTTPS_PROXY="http://127.0.0.1:$port" HTTP_PROXY="http://127.0.0.1:$port" NO_PROXY="127.0.0.1" \
      "$@" \
      claude -p "Reply with the single word: ok" --model haiku --max-turns 1 --tools "" \
        --output-format stream-json --verbose --include-partial-messages > "$dir/out-$kind.jsonl" 2> "$dir/err-$kind.txt" || echo "claude exit: $?"
    sleep 1
    echo "log: $log"; echo "claude version: $(claude --version)"
    # Summary only: names, booleans, hosts, statuses. The stream output is not printed.
    python3 - "$log" "$dir/out-$kind.jsonl" <<'PY'
import json, sys, collections
ev = [json.loads(l) for l in open(sys.argv[1])]
for e in ev:
    e.pop("t", None)
    print(json.dumps(e))
hosts = collections.Counter(e["host"] for e in ev if e["ev"] == "connect")
print("connect hosts:", dict(hosts))
n = 0; ok = False
for l in open(sys.argv[2]):
    try: d = json.loads(l)
    except Exception: continue
    n += 1
    if d.get("type") == "result": ok = (d.get("is_error") is False); print("result is_error:", d.get("is_error"), "subtype:", d.get("subtype"))
print("stream lines:", n, "result ok:", ok)
PY
    ;;
  *) echo "unknown mode" >&2; exit 2 ;;
esac
