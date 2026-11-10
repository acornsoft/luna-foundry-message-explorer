#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Crew ticket feed: a Generator and an Assertion for the crew-stock sample. Synthetic data only.

A made-up field-crew app changes job tickets that need stock items. Changes arrive as a steady
trickle plus a burst when crews sync at the end of a shift. Some changes are malformed (no item
code) and some are sent twice by the app (same changeId).

Assertion, per ticket:
  well formed: exactly one ticket event, one usage record, and one crew.ticket-updated carrying a
               reservationRef, within the SLA, with the update after the event.
  malformed:   dead-lettered on the stock branch, never updated, but the usage branch of the
               fan-out still gets its copy.

JSON-RPC 2.0 over stdio, one object per line. Standard library only. Logs go to stderr.
"""
import json
import random
import re
import sys
from datetime import datetime, timedelta, timezone

NAME = "crew-ticket-feed"
VERSION = "0.1.0"
FMT = "%Y-%m-%dT%H:%M:%SZ"
ITEMS = ["KIT-PUMP-SEAL", "KIT-VALVE-04", "KIT-FILTER-12", "KIT-HOSE-2M", "KIT-SENSOR-T1"]


def parse(value):
    return datetime.strptime(value, FMT).replace(tzinfo=timezone.utc)


def fmt(value):
    return value.astimezone(timezone.utc).strftime(FMT)


def duration(text):
    match = re.fullmatch(r"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?", text or "")
    if not match:
        raise ValueError(f"unsupported duration {text!r}; use PTnHnMnS")
    hours, minutes, seconds = (int(part or 0) for part in match.groups())
    return timedelta(hours=hours, minutes=minutes, seconds=seconds)


def send(message):
    sys.stdout.write(json.dumps(message, separators=(",", ":"), sort_keys=True) + "\n")
    sys.stdout.flush()


def reply(id_, result):
    send({"jsonrpc": "2.0", "id": id_, "result": result})


class Feed:
    def __init__(self):
        self.schedule = []
        self.emitted = 0
        self.malformed = set()
        self.sla = timedelta(minutes=30)
        self.sla_text = "PT30M"
        self.sinks = {}

    def initialize(self, params):
        config = params.get("config") or {}
        rng = random.Random(int(params["seed"]))
        start = parse(params["clockStart"])
        trickle = int(config.get("trickle", 30))
        window = duration(config.get("window", "PT4H"))
        burst = config.get("burst") or {}
        burst_count = int(burst.get("count", 0))
        burst_at = start + duration(burst.get("at", "PT0S"))
        burst_within = duration(burst.get("within", "PT1M"))
        malformed_rate = float(config.get("malformedRate", 0))
        duplicate_rate = float(config.get("duplicateRate", 0))
        self.sla_text = config.get("sla", "PT30M")
        self.sla = duration(self.sla_text)
        self.sinks = config.get("sinks") or {}
        self.schedule = []
        self.malformed = set()
        self.emitted = 0

        for i in range(1, trickle + burst_count + 1):
            if i <= trickle:
                at = start + timedelta(seconds=rng.randrange(int(window.total_seconds())))
            else:
                at = burst_at + timedelta(seconds=rng.randrange(max(1, int(burst_within.total_seconds()))))
            ticket = f"tkt-{i:04d}"
            body = {
                "ticketRef": ticket,
                "changeId": f"chg-{i:04d}-1",
                "crew": f"crew-{rng.randint(1, 12):02d}",
                "itemCode": rng.choice(ITEMS),
                "quantity": rng.randint(1, 4),
            }
            if rng.random() < malformed_rate:
                body["itemCode"] = ""
                self.malformed.add(ticket)
            self.schedule.append((at, i, {"correlationId": ticket, "body": body}))
            if rng.random() < duplicate_rate:
                again = dict(body, resent=True)
                self.schedule.append((at + timedelta(seconds=20), i, {"correlationId": ticket, "body": again}))

        self.schedule.sort(key=lambda entry: (entry[0], entry[1]))
        return {
            "protocol": "lfme-plugin/1.0",
            "name": NAME,
            "version": VERSION,
            "roles": ["generator", "assertion"],
            "capabilities": {"timeline": True, "count": len(self.schedule)},
        }

    def tick(self, params):
        now = parse(params["now"])
        due = []
        while self.emitted < len(self.schedule) and self.schedule[self.emitted][0] <= now:
            due.append(self.schedule[self.emitted][2])
            self.emitted += 1
        return {"messages": due}

    def next_batch(self, params):
        batch = []
        while self.emitted < len(self.schedule) and len(batch) < int(params["max"]):
            batch.append(self.schedule[self.emitted][2])
            self.emitted += 1
        return {"messages": batch}

    def expect(self, params):
        message = params["message"]
        if (message.get("body") or {}).get("resent"):
            return {"expectations": []}  # the original send carries the expectation
        ticket = message["correlationId"]
        bad = ticket in self.malformed
        text = (
            "no item code: dead-lettered on the stock branch, never updated, usage still recorded"
            if bad
            else f"one event, one usage record, one update with a reservationRef within {self.sla_text}"
        )
        return {"expectations": [{
            "correlationId": ticket,
            "sentAt": message["enqueuedAt"],
            "malformed": bad,
            "description": text,
        }]}

    def assert_(self, params):
        expected = params["expected"][0]
        actual = params["actual"]

        def seen(key):
            return [a for a in actual if a["sink"] == self.sinks.get(key)]

        events, usage, updated, dead = seen("events"), seen("usage"), seen("updated"), seen("deadLetter")
        if expected.get("malformed"):
            if not dead:
                return verdict("fail", "malformed ticket was never dead-lettered")
            if updated:
                return verdict("fail", "malformed ticket was dead-lettered but also updated")
            if len(usage) != 1:
                return verdict("fail", f"usage branch should still get one copy, got {len(usage)}")
            body = dead[0]["body"]
            return verdict("pass", f"dead-lettered after {body.get('deliveryCount')} deliveries; usage still recorded")

        if dead:
            return verdict("fail", "dead-lettered: " + str(dead[0]["body"].get("deadLetterErrorDescription")))
        if len(events) != 1:
            return verdict("fail", f"expected one ticket event after duplicate detection, got {len(events)}")
        if len(usage) != 1:
            return verdict("fail", f"expected one usage record, got {len(usage)}")
        if len(updated) != 1:
            return verdict("fail", f"expected one ticket update, got {len(updated)}")
        if not updated[0]["body"].get("reservationRef"):
            return verdict("fail", "ticket updated without a reservationRef")
        if parse(updated[0]["at"]) < parse(events[0]["at"]):
            return verdict("fail", "ticket updated before its event was published")
        took = parse(updated[0]["at"]) - parse(expected["sentAt"])
        if took > self.sla:
            return verdict("fail", f"updated after {int(took.total_seconds())} s, SLA {int(self.sla.total_seconds())} s")
        return verdict("pass", f"updated with {updated[0]['body']['reservationRef']} after {int(took.total_seconds())} s")


def verdict(status, reason):
    return {"status": status, "reason": reason}


def main():
    feed = Feed()
    handlers = {
        "initialize": feed.initialize,
        "tick": feed.tick,
        "nextBatch": feed.next_batch,
        "expect": feed.expect,
        "assert": feed.assert_,
    }
    for line in sys.stdin:
        request = json.loads(line)
        id_, method, params = request.get("id"), request.get("method"), request.get("params") or {}
        if method == "shutdown":
            reply(id_, {})
            return
        handler = handlers.get(method)
        if handler is None:
            send({"jsonrpc": "2.0", "id": id_, "error": {"code": -32601, "message": "method not found"}})
            continue
        try:
            reply(id_, handler(params))
        except Exception as error:  # report, do not crash the run
            print(f"{NAME}: {method} failed: {error}", file=sys.stderr)
            send({"jsonrpc": "2.0", "id": id_, "error": {"code": -32000, "message": str(error)}})


if __name__ == "__main__":
    main()
