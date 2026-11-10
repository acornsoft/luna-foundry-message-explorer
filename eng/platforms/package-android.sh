#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Builds the Android head (APK, arm64 phones and x64 emulators) with the .NET 10 "android" workload,
# then checks the APK holds the app, the Mono runtime and the Skia libraries for both ABIs.
# The APK is signed with the build machine's debug key only: a CI build check, not a store release.
#
#   ANDROID_SDK_ROOT=... JAVA_HOME=... bash eng/platforms/package-android.sh [out-dir]

set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-$root/artifacts/platforms}"
project="$root/src/ME.App.Android"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/android-sdk}}"
args=(-c Release -p:AndroidSdkDirectory="$sdk" --nologo -v:q)
[[ -n "${JAVA_HOME:-}" ]] && args+=(-p:JavaSdkDirectory="$JAVA_HOME")

# src/ME.App.Android/global.json selects the .NET 10 SDK; the rest of the repo stays on .NET 8.
(cd "$project" && dotnet build "${args[@]}")

apk="$(find "$project/bin/Release" -name '*-Signed.apk' | head -1)"
[[ -f "$apk" ]] || { echo "no APK was built" >&2; exit 1; }

listing="$(unzip -l "$apk" | awk '{print $4}')"
missing=0
for abi in arm64-v8a x86_64; do
  for lib in libmonosgen-2.0.so libSkiaSharp.so libHarfBuzzSharp.so "libassemblies.$abi.blob.so"; do
    grep -qx "lib/$abi/$lib" <<< "$listing" || { echo "APK is missing lib/$abi/$lib" >&2; missing=1; }
  done
done
grep -qx "classes.dex" <<< "$listing" || { echo "APK is missing classes.dex" >&2; missing=1; }
[[ $missing -eq 0 ]] || exit 1

mkdir -p "$out"
cp "$apk" "$out/LunaFoundryMessageExplorer-$version-android-debugsigned.apk"
ls -l "$out" | grep -F android
