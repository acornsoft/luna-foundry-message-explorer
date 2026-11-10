#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Package the desktop app as "Luna Foundry Message Explorer.app" in one DMG per architecture.
#
#   eng/package-macos.sh [--rid osx-arm64|osx-x64|all] [--out DIR] [--version X.Y.Z] [--build-number N]
#
# Each DMG holds the app and an /Applications link. The app is self-contained (no .NET install needed)
# and ships the sample test-job collections.
#
# Signing:
#   - Default: ad-hoc. Apple Silicon runs it, but it is not notarized, so the first launch needs
#     right-click > Open (see docs/install-macos.md).
#   - Developer ID: set LFME_MAC_P12_PATH and LFME_MAC_P12_PASSWORD (a "Developer ID Application"
#     certificate). The app is signed with the hardened runtime and eng/macos/entitlements.plist, then
#     the DMG is signed. Also set LFME_ASC_KEY_PATH (.p8), LFME_ASC_KEY_ID and LFME_ASC_ISSUER_ID to
#     notarize the DMG and staple the ticket. Uses rcodesign on any OS. Secrets are never printed.
#
# On macOS it uses codesign (ad-hoc) and hdiutil. On Linux it needs these on the PATH (or in
# $LFME_MAC_TOOLS): rcodesign (apple-codesign), mkfs.hfsplus (hfsprogs), hfsplus and dmg (libdmg-hfsplus).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="Luna Foundry Message Explorer"
BUNDLE_ID="ai.acornsoft.lfme"
EXE="Acornsoft.MessageExplorer.Desktop"
DMG_BASE="LunaFoundryMessageExplorer"
RIDS=(osx-arm64 osx-x64)
OUT="$ROOT/artifacts/dist"
VERSION=""
BUILD_NUMBER=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) [[ "$2" == all ]] || RIDS=("$2"); shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --build-number) BUILD_NUMBER="$2"; shift 2 ;;
    -h|--help) sed -n '7,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n "${LFME_MAC_TOOLS:-}" ]] && export PATH="$LFME_MAC_TOOLS:$PATH"
OS="$(uname -s)"
need() { command -v "$1" >/dev/null 2>&1 || { echo "missing tool: $1 ($2)" >&2; exit 3; }; }
need dotnet ".NET 8 SDK"
need python3 "draws the icon"
if [[ "$OS" == Darwin ]]; then
  need codesign "Xcode command line tools"; need hdiutil "macOS"
else
  need rcodesign "cargo install apple-codesign"; need mkfs.hfsplus "hfsprogs"
  need hfsplus "libdmg-hfsplus"; need dmg "libdmg-hfsplus"
fi

# Signing mode. Secrets go to files in a private temp folder (never on a command line, never echoed).
SIGNING=adhoc
NOTARIZE=0
SECRETS="$(mktemp -d)"
chmod 700 "$SECRETS"
trap 'rm -rf "$SECRETS"' EXIT
ENTITLEMENTS="$ROOT/eng/macos/entitlements.plist"
if [[ -n "${LFME_MAC_P12_PATH:-}" ]]; then
  [[ -f "$LFME_MAC_P12_PATH" ]] || { echo "LFME_MAC_P12_PATH does not point at a file" >&2; exit 3; }
  [[ -n "${LFME_MAC_P12_PASSWORD:-}" ]] || { echo "LFME_MAC_P12_PASSWORD is not set" >&2; exit 3; }
  need rcodesign "cargo install apple-codesign"
  SIGNING=developer-id
  ( umask 077; printf '%s' "$LFME_MAC_P12_PASSWORD" > "$SECRETS/p12-password" )
  if [[ -n "${LFME_ASC_KEY_PATH:-}" && -n "${LFME_ASC_KEY_ID:-}" && -n "${LFME_ASC_ISSUER_ID:-}" ]]; then
    [[ -f "$LFME_ASC_KEY_PATH" ]] || { echo "LFME_ASC_KEY_PATH does not point at a file" >&2; exit 3; }
    ( umask 077; rcodesign encode-app-store-connect-api-key --output-path "$SECRETS/asc-key.json" \
        "$LFME_ASC_ISSUER_ID" "$LFME_ASC_KEY_ID" "$LFME_ASC_KEY_PATH" >/dev/null )
    NOTARIZE=1
  else
    echo "Developer ID signing without notarization (LFME_ASC_KEY_PATH, LFME_ASC_KEY_ID, LFME_ASC_ISSUER_ID not all set)."
  fi
fi
echo "Signing: $SIGNING; notarize: $([[ $NOTARIZE == 1 ]] && echo yes || echo no)"
DEVID_ARGS=(--p12-file "${LFME_MAC_P12_PATH:-}" --p12-password-file "$SECRETS/p12-password")

if [[ -z "$VERSION" ]]; then
  VERSION="$(dotnet msbuild "$ROOT/src/ME.App.Desktop/ME.App.Desktop.csproj" -getProperty:Version -nologo | tr -d '[:space:]')"
fi
VERSION="${VERSION%%-*}"
VERSION="${VERSION%%+*}"
if [[ -z "$BUILD_NUMBER" ]]; then
  BUILD_NUMBER="${GITHUB_RUN_NUMBER:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 0)}"
fi
echo "Packaging $APP_NAME $VERSION (build $BUILD_NUMBER) for ${RIDS[*]}"

mkdir -p "$OUT"
WORK="$OUT/work"
rm -rf "$WORK"
mkdir -p "$WORK"

# Sample plugins build to artifacts/plugins; the bundled sample jobs point there by relative path.
for plugin in OrderGenerator ErpStandin HopRelay; do
  dotnet build "$ROOT/samples/plugins/$plugin/$plugin.csproj" -c Release -nologo -v quiet
done
python3 "$ROOT/eng/macos/make-icon.py" "$WORK/AppIcon.icns"

info_plist() {
  cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleExecutable</key><string>$EXE</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleSignature</key><string>????</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION.$BUILD_NUMBER</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSMicrophoneUsageDescription</key><string>Message Explorer listens only while you hold Hold to talk in the Voice tab, and sends that audio to Grok Voice to answer your question.</string>
  <key>NSHumanReadableCopyright</key><string>MPL-2.0. Gotham Savings and Loan (GSL) sample data is synthetic.</string>
</dict>
</plist>
PLIST
}

build_app() {
  local rid="$1" app="$2"
  local contents="$app/Contents"
  mkdir -p "$contents/MacOS" "$contents/Resources"
  dotnet publish "$ROOT/src/ME.App.Desktop/ME.App.Desktop.csproj" -c Release -r "$rid" --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:DebugType=None -p:DebugSymbols=false -nologo -v quiet -o "$contents/MacOS"
  # Single-file: Contents/MacOS holds only Mach-O code (the app and its native libraries), which is what
  # code signing expects. Anything else would have to live in Contents/Resources.
  find "$contents/MacOS" -name '*.pdb' -delete
  chmod 755 "$contents/MacOS"/*
  local f
  for f in "$contents/MacOS"/*; do
    is_macho "$f" || { echo "not Mach-O, does not belong in Contents/MacOS: $f" >&2; exit 4; }
  done

  info_plist > "$contents/Info.plist"
  printf 'APPL????' > "$contents/PkgInfo"
  cp "$WORK/AppIcon.icns" "$contents/Resources/AppIcon.icns"

  # Same relative layout as the repo, so the sample job files work unchanged:
  #   Resources/samples/jobs/<collection>  ->  ../../plugins/python  and  ../../../artifacts/plugins
  local res="$contents/Resources"
  mkdir -p "$res/samples/plugins/python" "$res/artifacts/plugins"
  cp -R "$ROOT/samples/jobs" "$res/samples/jobs"
  cp "$ROOT"/samples/plugins/python/*.py "$res/samples/plugins/python/"
  for plugin in OrderGenerator ErpStandin HopRelay; do
    mkdir -p "$res/artifacts/plugins/$plugin"
    cp "$ROOT/artifacts/plugins/$plugin/$plugin.dll" "$ROOT/artifacts/plugins/$plugin/$plugin.deps.json" \
       "$ROOT/artifacts/plugins/$plugin/$plugin.runtimeconfig.json" "$res/artifacts/plugins/$plugin/"
  done
  cp "$ROOT/LICENSE" "$res/LICENSE.txt" 2>/dev/null || true
  cp "$ROOT/THIRD-PARTY-NOTICES.md" "$res/THIRD-PARTY-NOTICES.md" 2>/dev/null || true
}

is_macho() {
  local magic
  magic="$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \n')"
  [[ "$magic" == cffaedfe || "$magic" == cefaedfe || "$magic" == cafebabe ]]
}

sign_app() {
  local app="$1"
  if [[ "$SIGNING" == developer-id ]]; then
    # Hardened runtime on every Mach-O, secure timestamp, entitlements on the main executable only.
    rcodesign sign "${DEVID_ARGS[@]}" --for-notarization --code-signature-flags runtime \
      --entitlements-xml-file "Contents/MacOS/$EXE:$ENTITLEMENTS" "$app" >/dev/null
    rcodesign print-signature-info "$app/Contents/MacOS/$EXE" | grep -q 'code_directory' \
      || { echo "no code directory on $EXE" >&2; exit 5; }
  elif [[ "$OS" == Darwin ]]; then
    # Ad-hoc ("-") signature: enough for Apple Silicon to run it. Libraries first, then the sealed bundle.
    find "$app/Contents/MacOS" -type f -name '*.dylib' -print0 | while IFS= read -r -d '' f; do
      codesign --force --sign - --timestamp=none "$f"
    done
    codesign --force --sign - --timestamp=none "$app"
    codesign --verify --deep --strict --verbose=2 "$app"
  else
    # rcodesign signs nested Mach-O files, then the main executable, and seals Contents/Resources.
    rcodesign sign "$app" >/dev/null
    rcodesign print-signature-info "$app/Contents/MacOS/$EXE" | grep -q 'code_directory' \
      || { echo "no code directory on $EXE" >&2; exit 5; }
  fi
}

make_dmg() {
  local stage="$1" dmg_path="$2"
  rm -f "$dmg_path"
  if [[ "$OS" == Darwin ]]; then
    ln -s /Applications "$stage/Applications"
    hdiutil create -quiet -volname "$APP_NAME" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg_path"
    rm "$stage/Applications"
  else
    local img="$WORK/volume.hfs" size_mb
    size_mb=$(( $(du -sm "$stage" | cut -f1) * 13 / 10 + 32 ))
    rm -f "$img"
    truncate -s "${size_mb}M" "$img"
    mkfs.hfsplus -v "$APP_NAME" "$img" >/dev/null
    hfsplus "$img" addall "$stage" >/dev/null
    hfsplus "$img" symlink /Applications /Applications >/dev/null
    # Executable bits for every Mach-O in Contents/MacOS.
    local app_rel="/$APP_NAME.app/Contents/MacOS"
    while IFS= read -r -d '' f; do
      if is_macho "$f"; then hfsplus "$img" chmod 755 "$app_rel/$(basename "$f")" >/dev/null; fi
    done < <(find "$stage/$APP_NAME.app/Contents/MacOS" -maxdepth 1 -type f -print0)
    dmg build "$img" "$dmg_path" >/dev/null
    rm -f "$img"
  fi
}

for rid in "${RIDS[@]}"; do
  arch="${rid#osx-}"
  stage="$WORK/$rid"
  app="$stage/$APP_NAME.app"
  mkdir -p "$stage"
  echo "== $rid: publish and assemble"
  build_app "$rid" "$app"
  echo "== $rid: sign ($SIGNING)"
  sign_app "$app"
  dmg_path="$OUT/$DMG_BASE-$VERSION-$arch.dmg"
  echo "== $rid: DMG"
  make_dmg "$stage" "$dmg_path"
  if [[ "$SIGNING" == developer-id ]]; then
    echo "== $rid: sign DMG"
    rcodesign sign "${DEVID_ARGS[@]}" "$dmg_path" >/dev/null
    if [[ "$NOTARIZE" == 1 ]]; then
      echo "== $rid: notarize and staple (this waits for Apple)"
      rcodesign notary-submit --api-key-file "$SECRETS/asc-key.json" --staple "$dmg_path"
    fi
  fi
  echo "Built $dmg_path ($(du -h "$dmg_path" | cut -f1)), signing: $SIGNING"
done

( cd "$OUT" && { command -v sha256sum >/dev/null && sha256sum ./*.dmg || shasum -a 256 ./*.dmg; } > SHA256SUMS.txt )
cat "$OUT/SHA256SUMS.txt"
