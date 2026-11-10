#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# macOS only. Copy the app out of a DMG, launch it, and let it run the bundled default sample
# (erp-standin, in-memory broker) and quit. Passes when the app writes a report that starts with "Passed".
#   eng/macos/smoke-test.sh <file.dmg> <report.txt> [screenshot.png] [arch -x86_64]
set -euo pipefail
DMG="$1"; REPORT="$2"; SHOT="${3:-}"; shift $(( $# < 3 ? $# : 3 ))
LAUNCH=("$@")
APP="Luna Foundry Message Explorer.app"
EXE="Acornsoft.MessageExplorer.Desktop"
WORK="$(mktemp -d)"
MNT="$WORK/mnt"; mkdir -p "$MNT" "$WORK/Applications"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$DMG" >/dev/null
cp -R "$MNT/$APP" "$WORK/Applications/"
hdiutil detach "$MNT" >/dev/null
rm -f "$REPORT"
mkdir -p "$(dirname "$REPORT")"

# A slow pace keeps the window up long enough for a screenshot, then the run finishes and the app quits.
"${LAUNCH[@]}" "$WORK/Applications/$APP/Contents/MacOS/$EXE" --tab jobs --pace 600 --run --exit-after-run "$REPORT" \
  > "$WORK/app.log" 2>&1 &
pid=$!
if [[ -n "$SHOT" ]]; then
  sleep 12
  screencapture -x "$SHOT" 2>/dev/null || echo "screencapture failed (no screen recording permission?)"
fi
for _ in $(seq 1 180); do
  kill -0 "$pid" 2>/dev/null || break
  sleep 1
done
if kill -0 "$pid" 2>/dev/null; then
  kill "$pid"; cat "$WORK/app.log"; echo "FAIL: app did not finish within the time limit" >&2; exit 1
fi
wait "$pid" && code=0 || code=$?
cat "$WORK/app.log"
[[ -f "$REPORT" ]] || { echo "FAIL: no report (exit $code)" >&2; exit 1; }
cat "$REPORT"
head -1 "$REPORT" | grep -qx Passed || { echo "FAIL: run did not pass (exit $code)" >&2; exit 1; }
echo "OK: app launched, ran the bundled sample and quit (exit $code)"
rm -rf "$WORK"
