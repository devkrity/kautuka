#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$(mktemp)"
trap 'rm -f "$output"' EXIT

if ! KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"

  polls=0
  id() { printf "1001\\n"; }
  get_service_user_home() { printf "/home/kautuka\\n"; }
  run_as_service_user() { shift 3; "$@"; }
  systemctl() {
    if [[ " $* " == *" show "* ]]; then
      if [[ " $* " == *" kautuka-updateservice.service "* ]]; then
        if (( polls <= 1 )); then
          printf "activating\\n"
          return 0
        fi
      fi
      printf "active\\n"
      return 0
    fi
    return 1
  }
  curl() {
    local output_path=""
    while (($#)); do
      if [[ "$1" == "--output" ]]; then
        output_path="$2"
        shift 2
      else
        shift
      fi
    done
    polls=$((polls + 1))
    if (( polls == 1 )); then
      printf "%s\\n" "{\"manager\":{\"status\":\"unhealthy\"},\"lifecycle\":{\"status\":\"core_not_ready\",\"coreReady\":false}}" > "$output_path"
    else
      printf "%s\\n" "{\"manager\":{\"status\":\"healthy\"},\"lifecycle\":{\"status\":\"agents_pending\",\"coreReady\":true}}" > "$output_path"
    fi
  }
  sleep() { :; }

  wait_for_web_ui kautuka http://127.0.0.1:20229 20
  [[ "$polls" -eq 3 ]] || { echo "expected to poll readiness until every service was active, got $polls polls" >&2; exit 1; }
' _ "$repo_root" > "$output" 2>&1; then
  cat "$output" >&2
  exit 1
fi

grep -Fq 'Waiting for the web UI and core runtime readiness' "$output"
grep -Fq 'Startup progress (0s/20s)' "$output"
grep -Fq 'lifecycle=core_not_ready; manager=unhealthy' "$output"
grep -Fq 'updates=activating' "$output"
grep -Fq 'Core runtime is ready (lifecycle=agents_pending; manager=healthy).' "$output"
grep -Fq 'Verified web UI at: http://127.0.0.1:20229' "$output"

echo 'Linux installer readiness wait passed (HTTP availability did not bypass core readiness).'
