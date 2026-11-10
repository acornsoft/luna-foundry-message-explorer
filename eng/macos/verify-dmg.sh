#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Structural check of a DMG made by eng/package-macos.sh:
#   eng/macos/verify-dmg.sh <file.dmg> <arm64|x64>
# Lists the volume, checks the /Applications link, Info.plist keys, executable bits, the Mach-O
# architecture of every binary, the bundled samples, and the code signature.
# macOS uses hdiutil, plutil, lipo and codesign; Linux uses dmg, hfsplus, file and rcodesign.
set -euo pipefail
DMG="$1"; ARCH="$2"
APP="Luna Foundry Message Explorer.app"
EXE="Acornsoft.MessageExplorer.Desktop"
[[ -n "${LFME_MAC_TOOLS:-}" ]] && export PATH="$LFME_MAC_TOOLS:$PATH"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
case "$ARCH" in arm64) FILE_ARCH=arm64; LIPO_ARCH=arm64 ;; x64) FILE_ARCH=x86_64; LIPO_ARCH=x86_64 ;; *) fail "arch is arm64 or x64" ;; esac

check_plist() {
  local plist="$1" key value
  for key in CFBundleIdentifier=ai.acornsoft.lfme CFBundleExecutable="$EXE" LSMinimumSystemVersion=12.0 \
             CFBundleIconFile=AppIcon CFBundlePackageType=APPL; do
    value="${key#*=}"; key="${key%%=*}"
    grep -q "<key>$key</key><string>$value</string>" "$plist" || fail "Info.plist $key is not $value"
  done
  grep -q '<key>NSHighResolutionCapable</key><true/>' "$plist" || fail "NSHighResolutionCapable is not true"
  grep -o '<key>CFBundleShortVersionString</key><string>[^<]*' "$plist" | sed 's/.*<string>/version: /'
  echo "Info.plist: ok"
}

if [[ "$(uname -s)" == Darwin ]]; then
  MNT="$WORK/mnt"; mkdir -p "$MNT"
  hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$DMG" >/dev/null
  trap 'hdiutil detach "$MNT" >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT
  ls -la "$MNT"
  [[ "$(readlink "$MNT/Applications")" == /Applications ]] || fail "no /Applications link"
  plutil -lint "$MNT/$APP/Contents/Info.plist"
  check_plist "$MNT/$APP/Contents/Info.plist"
  for f in "$MNT/$APP/Contents/MacOS"/*; do
    [[ -x "$f" ]] || fail "not executable: $f"
    lipo -archs "$f" | grep -qw "$LIPO_ARCH" || fail "$f lacks $LIPO_ARCH"
    echo "$(basename "$f"): $(lipo -archs "$f")"
  done
  [[ -f "$MNT/$APP/Contents/Resources/samples/jobs/erp-standin/collection.yaml" ]] || fail "samples missing"
  codesign --verify --deep --strict --verbose=2 "$MNT/$APP"
  codesign -dv "$MNT/$APP" 2>&1 | grep -E 'Identifier|Signature|flags|TeamIdentifier' || true
  spctl --assess --type execute -vv "$MNT/$APP" 2>&1 || echo "Gatekeeper: not accepted (expected without Developer ID and notarization)"
else
  IMG="$WORK/volume.hfs"
  dmg extract "$DMG" "$IMG" >/dev/null
  hfsplus "$IMG" ls / | tee "$WORK/root.txt"
  grep -q 'Applications' "$WORK/root.txt" || fail "no Applications entry"
  grep 'Applications' "$WORK/root.txt" | grep -q '^120' || fail "Applications is not a symlink"
  hfsplus "$IMG" ls "/$APP/Contents/MacOS" | tee "$WORK/macos.txt"
  mkdir -p "$WORK/x"
  hfsplus "$IMG" cat "/$APP/Contents/Info.plist" > "$WORK/x/Info.plist"
  check_plist "$WORK/x/Info.plist"
  while read -r mode _ _ _ _ _ name; do
    [[ "$mode" == 100* ]] || continue
    [[ "$mode" == 100755 ]] || fail "not executable: $name"
    hfsplus "$IMG" cat "/$APP/Contents/MacOS/$name" > "$WORK/x/$name"
    file -b "$WORK/x/$name" | grep -q "$FILE_ARCH" || fail "$name lacks $FILE_ARCH"
    echo "$name: $(file -b "$WORK/x/$name" | cut -c1-90)"
  done < <(grep -E '^[0-9]{6} ' "$WORK/macos.txt")
  hfsplus "$IMG" ls "/$APP/Contents/Resources/samples/jobs/erp-standin" | grep -q collection.yaml || fail "samples missing"
  hfsplus "$IMG" ls "/$APP/Contents/Resources/artifacts/plugins/ErpStandin" | grep -q ErpStandin.dll || fail "sample plugin missing"
  hfsplus "$IMG" ls "/$APP/Contents/_CodeSignature" | grep -q CodeResources || fail "bundle is not sealed"
  rcodesign print-signature-info "$WORK/x/$EXE" > "$WORK/sig.txt"
  grep -q code_directory "$WORK/sig.txt" || fail "no code signature on $EXE"
  grep -E 'identifier:|flags:|cms:|subject:' "$WORK/sig.txt" | sort -u | head -8
fi
echo "OK: $DMG ($ARCH)"
