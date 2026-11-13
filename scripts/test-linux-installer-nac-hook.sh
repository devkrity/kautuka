#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_root="$(mktemp -d)"
trap 'rm -rf "$temp_root"' EXIT

KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 source "$repo_root/scripts/kautuka-install-linux.sh.in"

mkdir -p "$temp_root/payload/scripts" "$temp_root/hooks"
printf '%s\n' '#!/usr/bin/env python3' 'print("hook")' > "$temp_root/payload/scripts/kautuka_hook.py"
install_nac_hook_payload \
  "$temp_root/payload/scripts/kautuka_hook.py" \
  "$temp_root/hooks/kautuka_hook.py"
cmp -s "$temp_root/payload/scripts/kautuka_hook.py" "$temp_root/hooks/kautuka_hook.py"
[[ -x "$temp_root/hooks/kautuka_hook.py" ]]

missing_output="$temp_root/missing-hook.stderr"
if ( install_nac_hook_payload \
  "$temp_root/missing/scripts/kautuka_hook.py" \
  "$temp_root/hooks/missing-hook.py" ) 2>"$missing_output"; then
  echo 'installer accepted a payload with no NAC hook' >&2
  exit 1
fi
grep -Fq 'required network access hook payload is missing' "$missing_output"

echo 'Linux installer NAC hook payload checks passed.'
