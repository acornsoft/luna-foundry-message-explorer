#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Drives lfme-mcp over stdio as a client would: discover, list, call, an old version, and the legacy handshake."""
import json, subprocess, sys

cmd = sys.argv[1:] or ["dotnet", "src/ME.McpServer/bin/Release/net8.0/lfme-mcp.dll", "--synthetic"]
meta = {"io.modelcontextprotocol/protocolVersion": "2026-07-28", "io.modelcontextprotocol/clientCapabilities": {}}

def session(messages):
    lines = "".join(json.dumps(m) + "\n" for m in messages)
    done = subprocess.run(cmd, input=lines, capture_output=True, text=True, timeout=120, check=True)
    out = [json.loads(l) for l in done.stdout.splitlines() if l]
    print(done.stderr.strip(), file=sys.stderr)
    return {r["id"]: r for r in out}

modern = session([
    {"jsonrpc": "2.0", "id": 1, "method": "server/discover", "params": {"_meta": meta}},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {"_meta": meta}},
    {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"_meta": meta, "name": "check_limits", "arguments": {"maxDeadLetter": 0}}},
    {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"_meta": meta, "name": "purge_queue"}},
    {"jsonrpc": "2.0", "id": 5, "method": "tools/list", "params": {"_meta": dict(meta, **{"io.modelcontextprotocol/protocolVersion": "1999-01-01"})}},
])
assert modern[1]["result"]["supportedVersions"] == ["2026-07-28"], modern[1]
tools = modern[2]["result"]["tools"]
assert len(tools) == 4 and all(t["annotations"]["readOnlyHint"] and not t["annotations"]["destructiveHint"] for t in tools), tools
assert len(modern[3]["result"]["structuredContent"]["breaches"]) == 2, modern[3]
assert modern[4]["error"]["code"] == -32602, modern[4]
assert modern[5]["error"]["code"] == -32022 and modern[5]["error"]["data"]["supported"] == ["2026-07-28"], modern[5]

legacy = session([
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "summarize_dead_letters"}},
])
assert legacy[1]["result"]["protocolVersion"] == "2025-11-25", legacy[1]
assert legacy[2]["result"]["structuredContent"]["total"] == 4, legacy[2]
print("lfme-mcp smoke passed: discover, list, call, refusal, version error, legacy handshake")
