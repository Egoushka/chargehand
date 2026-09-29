#!/usr/bin/env python3
"""Stdio MCP server for the goal 0.6 spike: two tools, no dependencies. Messages are newline-delimited JSON-RPC."""
import json
import sys

TOOLS = [
    {"name": "echo_fact", "description": "Returns a fixed fact.",
     "inputSchema": {"type": "object", "properties": {"topic": {"type": "string"}}},
     "annotations": {"readOnlyHint": True}},
    {"name": "write_note", "description": "Pretends to write a note.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}},
]


def reply(id_, result=None, error=None):
    message = {"jsonrpc": "2.0", "id": id_}
    message["error" if error else "result"] = error or result
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    request = json.loads(line)
    method, id_ = request.get("method"), request.get("id")
    if method == "initialize":
        reply(id_, {"protocolVersion": request["params"]["protocolVersion"], "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake", "version": "0"}})
    elif method == "tools/list":
        reply(id_, {"tools": TOOLS})
    elif method == "tools/call":
        reply(id_, {"content": [{"type": "text", "text": request["params"]["name"] + " ok: spike fact"}]})
    elif id_ is not None:
        reply(id_, error={"code": -32601, "message": "method not found"})
