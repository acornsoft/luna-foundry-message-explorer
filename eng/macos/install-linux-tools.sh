#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Fetch and build the Linux tools eng/package-macos.sh needs, into one folder (default .tools/macos):
#   rcodesign (apple-codesign release, checksum pinned), hfsplus and dmg (libdmg-hfsplus, commit pinned).
# mkfs.hfsplus comes from the distro package hfsprogs (apt-get install hfsprogs on Ubuntu).
# Build needs gcc plus zlib and bzip2 headers (zlib1g-dev, libbz2-dev). None of these tools are
# committed to this repo; they keep their own licenses.
#   eng/macos/install-linux-tools.sh [dir]     then: LFME_MAC_TOOLS=<dir> eng/package-macos.sh
set -euo pipefail
DIR="$(mkdir -p "${1:-.tools/macos}" && cd "${1:-.tools/macos}" && pwd)"
RCODESIGN_VERSION=0.29.0
RCODESIGN_SHA256=dbe85cedd8ee4217b64e9a0e4c2aef92ab8bcaaa41f20bde99781ff02e600002
LIBDMG_COMMIT=e18579b391950589b9ed16ee630c54ce4319bb29
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [[ ! -x "$DIR/rcodesign" ]]; then
  name="apple-codesign-$RCODESIGN_VERSION-x86_64-unknown-linux-musl"
  curl -fsSL -o "$WORK/rcodesign.tar.gz" \
    "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F$RCODESIGN_VERSION/$name.tar.gz"
  echo "$RCODESIGN_SHA256  $WORK/rcodesign.tar.gz" | sha256sum -c -
  tar -xzf "$WORK/rcodesign.tar.gz" -C "$WORK"
  install -m 755 "$WORK/$name/rcodesign" "$DIR/rcodesign"
fi

if [[ ! -x "$DIR/dmg" || ! -x "$DIR/hfsplus" ]]; then
  git clone -q https://github.com/mozilla/libdmg-hfsplus.git "$WORK/libdmg"
  git -C "$WORK/libdmg" checkout -q "$LIBDMG_COMMIT"
  cd "$WORK/libdmg"
  hfs=(hfs/btree.c hfs/catalog.c hfs/extents.c hfs/xattr.c hfs/fastunicodecompare.c hfs/flatfile.c
       hfs/hfslib.c hfs/rawfile.c hfs/utility.c hfs/volume.c hfs/hfscompress.c)
  gcc -O2 -fno-strict-aliasing -w -Iincludes common/*.c "${hfs[@]}" hfs/hfs.c -lz -o "$DIR/hfsplus"
  gcc -O2 -fno-strict-aliasing -w -Iincludes common/*.c "${hfs[@]}" $(ls dmg/*.c | grep -v win32test) \
    -lz -lbz2 -lpthread -o "$DIR/dmg"
fi

if ! command -v mkfs.hfsplus >/dev/null && [[ ! -x "$DIR/mkfs.hfsplus" ]]; then
  echo "mkfs.hfsplus is missing: install the hfsprogs package, or put mkfs.hfsplus in $DIR" >&2
  exit 3
fi
ls -la "$DIR"
