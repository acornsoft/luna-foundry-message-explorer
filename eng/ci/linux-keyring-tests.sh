#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# Runs the secure-store integration tests against a real Secret Service (GNOME Keyring) in a private
# D-Bus session with a throwaway keyring. CI only; the keyring password is a fixed throwaway value.
set -euo pipefail
PROJECT="${1:-tests/ME.Credentials.Tests}"
CONFIG="${2:-Release}"
export HOME="$(mktemp -d)"
export XDG_RUNTIME_DIR="$HOME/run"; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
dbus-run-session -- bash -c "
  printf 'throwaway-ci-keyring' | gnome-keyring-daemon --unlock --components=secrets >/dev/null
  LFME_SECURE_STORE_TESTS=1 dotnet test '$PROJECT' --configuration '$CONFIG' --filter 'FullyQualifiedName~SecureStoreIntegration' --verbosity normal
"
