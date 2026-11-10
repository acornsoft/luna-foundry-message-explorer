#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Publishes the desktop app for one runtime (osx-x64, win-x64, win-arm64, linux-x64, linux-arm64),
# self-contained, then checks the output has the app host and the native Skia and HarfBuzz libraries
# for that platform before archiving it. Unsigned: these are CI build checks, not releases.
#
#   bash eng/platforms/package-desktop.sh <rid> [out-dir]

set -euo pipefail

rid="${1:?usage: package-desktop.sh <rid> [out-dir]}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${2:-$root/artifacts/platforms}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
name="LunaFoundryMessageExplorer-$version-$rid"
stage="$out/$name"

case "$rid" in
  win-*)   host="Acornsoft.MessageExplorer.Desktop.exe"; skia="libSkiaSharp.dll"; harf="libHarfBuzzSharp.dll" ;;
  osx-*)   host="Acornsoft.MessageExplorer.Desktop"; skia="libSkiaSharp.dylib"; harf="libHarfBuzzSharp.dylib" ;;
  linux-*) host="Acornsoft.MessageExplorer.Desktop"; skia="libSkiaSharp.so"; harf="libHarfBuzzSharp.so" ;;
  *) echo "unsupported runtime: $rid" >&2; exit 2 ;;
esac

rm -rf "$stage"
mkdir -p "$stage"
dotnet publish "$root/src/ME.App.Desktop/ME.App.Desktop.csproj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o "$stage" --nologo -v:q

missing=0
for file in "$host" "$skia" "$harf" Acornsoft.MessageExplorer.Shell.dll; do
  if [[ ! -f "$stage/$file" ]]; then
    echo "missing from $rid output: $file" >&2
    missing=1
  fi
done
[[ $missing -eq 0 ]] || exit 1

# The app host must be built for the right OS (PE for Windows, Mach-O for macOS, ELF for Linux).
magic="$(head -c 4 "$stage/$host" | od -An -tx1 | tr -d ' \n')"
case "$rid" in
  win-*)   [[ "$magic" == 4d5a* ]] || { echo "$host is not a Windows executable ($magic)" >&2; exit 1; } ;;
  osx-*)   [[ "$magic" == cffaedfe || "$magic" == cafebabe ]] || { echo "$host is not Mach-O ($magic)" >&2; exit 1; } ;;
  linux-*) [[ "$magic" == 7f454c46 ]] || { echo "$host is not ELF ($magic)" >&2; exit 1; } ;;
esac

cp "$root/LICENSE" "$stage/LICENSE"
cd "$out"
case "$rid" in
  win-*) rm -f "$name.zip"; (cd "$out" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.', sys.argv[1])" "$name") ;;
  *)     tar -czf "$name.tar.gz" "$name" ;;
esac
rm -rf "$stage"
ls -l "$out" | grep -F "$name"
