#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$(mktemp)"
trap 'rm -f "$output"' EXIT

if ! KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"

  polls_file="$(mktemp)"
  printf "0\\n" > "$polls_file"
  trap "rm -f \\\"$polls_file\\\"" EXIT
  id() { printf "1001\\n"; }
  get_service_user_home() { printf "/home/kautuka\\n"; }
  run_as_service_user() { shift 3; "$@"; }
  confirm_first_admin_setup() { echo "FIRST-RUN SETUP acknowledgement requested for $1"; }
  journalctl() {
    printf "%s\\n" "Built-in MCP metadata refresh start. connectorId=kautuka-google-mail bundleChanged=False metadataMissing=True"
  }
  systemctl() {
    if [[ " $* " == *" show "* ]]; then
      if [[ " $* " == *" kautuka-updateservice.service "* ]]; then
        polls="$(cat "$polls_file")"
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
    local target=""
    while (($#)); do
      if [[ "$1" == "--output" ]]; then
        output_path="$2"
        shift 2
      elif [[ "$1" == "--write-out" ]]; then
        shift 2
      else
        target="$1"
        shift
      fi
    done
    if [[ "$target" == */api/auth/setup/status ]]; then
      printf "%s\\n" "{\"needsSetup\":true}" > "$output_path"
      printf "200"
      return 0
    fi
    polls="$(cat "$polls_file")"
    polls=$((polls + 1))
    printf "%s\\n" "$polls" > "$polls_file"
    if (( polls == 1 )); then
      printf "%s\\n" "{\"workspace\":{\"status\":\"unhealthy\"},\"lifecycle\":{\"status\":\"core_not_ready\",\"coreReady\":false}}" > "$output_path"
    else
      printf "%s\\n" "{\"workspace\":{\"status\":\"healthy\"},\"lifecycle\":{\"status\":\"agents_pending\",\"coreReady\":true}}" > "$output_path"
    fi
    printf "200"
  }
  sleep() { :; }

  wait_for_web_ui kautuka http://127.0.0.1:20229 20
  polls="$(cat "$polls_file")"
  [[ "$polls" -eq 3 ]] || { echo "expected to poll readiness until every service was active, got $polls polls" >&2; exit 1; }
' _ "$repo_root" > "$output" 2>&1; then
  cat "$output" >&2
  exit 1
fi

grep -Fq 'Waiting for the web UI and core runtime readiness' "$output"
grep -Fq 'Startup progress (0s/20s)' "$output"
grep -Fq 'lifecycle=core_not_ready; manager=unhealthy' "$output"
grep -Fq 'updates=activating' "$output"
grep -Fq 'Preparing first-use connector runtime (an image may need to download): kautuka-google-mail' "$output"
grep -Fq 'Core runtime is ready (lifecycle=agents_pending; manager=healthy).' "$output"
grep -Fq 'Verified web UI at: http://127.0.0.1:20229' "$output"
grep -Fq 'FIRST-RUN SETUP acknowledgement requested for http://127.0.0.1:20229' "$output"

echo 'Linux installer readiness wait passed (HTTP availability did not bypass core readiness).'
