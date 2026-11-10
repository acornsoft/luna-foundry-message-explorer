#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Minimal Python plugin: a seeded generator. JSON-RPC 2.0 over stdio, one object per line.

Standard library only. Logs go to stderr; stdout carries protocol messages only.
"""
import json
import random
import sys
from datetime import datetime, timedelta, timezone

NAME = "hello-generator"
VERSION = "0.1.0"
FMT = "%Y-%m-%dT%H:%M:%SZ"


def parse(value):
    return datetime.strptime(value, FMT).replace(tzinfo=timezone.utc)


def send(message):
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def reply(id_, result):
    send({"jsonrpc": "2.0", "id": id_, "result": result})


schedule = []
emitted = 0

for line in sys.stdin:
    if not line.strip():
        continue
    msg = json.loads(line)
    method, params, id_ = msg.get("method"), msg.get("params") or {}, msg.get("id")
    if method == "initialize":
        rng = random.Random(params["seed"])
        config = params.get("config") or {}
        count = int(config.get("count", 5))
        every = int(config.get("everySeconds", 60))
        start = parse(params["clockStart"])
        schedule = [
            (start + timedelta(seconds=i * every),
             {"correlationId": f"py-{i + 1:04d}",
              "body": {"greeting": "hello", "n": i + 1, "amountCents": rng.randrange(100, 5000)}})
            for i in range(count)
        ]
        emitted = 0
        print(f"{NAME} ready with {count} messages", file=sys.stderr, flush=True)
        reply(id_, {"protocol": "lfme-plugin/1.0", "name": NAME, "version": VERSION,
                    "roles": ["generator"], "capabilities": {"timeline": True}})
    elif method == "tick":
        now = parse(params["now"])
        due = []
        while emitted < len(schedule) and schedule[emitted][0] <= now:
            due.append(schedule[emitted][1])
            emitted += 1
        reply(id_, {"messages": due})
    elif method == "nextBatch":
        batch = [m for _, m in schedule[emitted:emitted + int(params["max"])]]
        emitted += len(batch)
        reply(id_, {"messages": batch})
    elif method == "shutdown":
        reply(id_, {})
        break
    else:
        send({"jsonrpc": "2.0", "id": id_, "error": {"code": -32601, "message": "method not found"}})
