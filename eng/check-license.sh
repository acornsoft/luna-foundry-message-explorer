#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

license="$root/LICENSE"
if [[ ! -f "$license" ]]; then
  echo "LICENSE is missing"
  exit 1
fi

python3 - "$license" << 'PY'
import pathlib, sys
text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
phrases = [
    "Mozilla Public License Version 2.0",
    "1. Definitions",
    "2. License Grants and Conditions",
    "3. Responsibilities",
    "4. Inability to Comply Due to Statute or Regulation",
    "5. Termination",
    "6. Disclaimer of Warranty",
    "7. Limitation of Liability",
    "8. Litigation",
    "9. Miscellaneous",
    "10. Versions of the License",
    "Exhibit A - Source Code Form License Notice",
    "Exhibit B - \"Incompatible With Secondary Licenses\" Notice",
    "https://mozilla.org/MPL/2.0/",
]
missing = [phrase for phrase in phrases if phrase not in text]
if missing:
    print("LICENSE is not the MPL-2.0 text. Missing:")
    for phrase in missing:
        print(" -", phrase)
    sys.exit(1)
if len(text) < 15000:
    print("LICENSE is shorter than the official MPL-2.0 text")
    sys.exit(1)
PY

missing_header=0
while IFS= read -r -d '' file; do
  if ! grep -q -F "SPDX-License-Identifier: MPL-2.0" "$file"; then
    echo "missing SPDX header: $file"
    missing_header=1
  fi
done < <(find src tests \( -path '*/bin/*' -o -path '*/obj/*' \) -prune -o -type f \( -name '*.cs' -o -name '*.axaml' -o -name '*.bicep' -o -name '*.yaml' \) -print0)

if [[ "$missing_header" -ne 0 ]]; then
  exit 1
fi

customer="$(printf '%s%s' 'E' 'SL')"
internal="$(printf '%s%s' 'Gno' 'mah')"

if grep -R -n -I -w -F "$customer" \
  --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=artifacts \
  .; then
  echo "banned customer token found"
  exit 1
fi

if grep -R -n -I -F "$internal" \
  --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=artifacts \
  .; then
  echo "banned internal name found"
  exit 1
fi

secret_patterns=(
  "$(printf '%s%s' 'Shared' 'AccessKey')"
  "$(printf '%s%s=' 'Account' 'Key')"
  "$(printf '%s%s' 'Default' 'EndpointsProtocol')"
  "$(printf '%s %s' 'BEGIN PRIVATE' 'KEY')"
  "$(printf '%s %s' 'BEGIN RSA PRIVATE' 'KEY')"
)

for pattern in "${secret_patterns[@]}"; do
  if grep -R -n -I -F "$pattern" \
    --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=artifacts \
    .; then
    echo "possible secret pattern found"
    exit 1
  fi
done

echo "license check passed"
