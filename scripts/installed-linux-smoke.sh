#!/usr/bin/env bash
# Assert an installed Linux or imported WSL appliance is actually up:
# systemd (optional) → WebHost → system-health → first-admin → execution-readiness.
# Does not send chat or HITL; those need Ollama and are a self-hosted pre-tag gate.
set -euo pipefail

base_url="${KAUTUKA_SMOKE_BASE_URL:-http://127.0.0.1:20229}"
skip_systemd="${KAUTUKA_SMOKE_SKIP_SYSTEMD:-0}"
timeout_seconds="${KAUTUKA_SMOKE_TIMEOUT_SECONDS:-180}"
service_user="${KAUTUKA_SMOKE_SERVICE_USER:-kautuka}"
setup_username="${KAUTUKA_SMOKE_USERNAME:-smoke-admin}"
setup_password="${KAUTUKA_SMOKE_PASSWORD:-}"

usage() {
  cat <<'EOF'
Usage: scripts/installed-linux-smoke.sh [options]

Options:
  --base-url <url>     WebHost origin. Default: http://127.0.0.1:20229
  --skip-systemd       Skip systemctl and drop-in checks (HTTP-only).
  --timeout-seconds N  HTTP wait budget. Default: 180
  --service-user NAME  Appliance user. Default: kautuka
  -h, --help           Show this help.

Environment:
  KAUTUKA_SMOKE_USERNAME / KAUTUKA_SMOKE_PASSWORD
      First-admin credentials to create, or existing login when setup is done.
      A password is generated when unset.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url)
      base_url="$2"
      shift 2
      ;;
    --skip-systemd)
      skip_systemd=1
      shift
      ;;
    --timeout-seconds)
      timeout_seconds="$2"
      shift 2
      ;;
    --service-user)
      service_user="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

base_url="${base_url%/}"

if [[ -z "$setup_password" ]]; then
  setup_password="$(python3 -c 'import secrets; print(secrets.token_urlsafe(24))')"
fi

fail() {
  echo "error: $*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

require_cmd curl
require_cmd python3

json_get() {
  python3 -c 'import json,sys; value=json.load(sys.stdin)
for key in sys.argv[1].split("."):
    if isinstance(value, dict):
        value=value[key]
    else:
        raise SystemExit(f"missing {sys.argv[1]}")
print(value if not isinstance(value, bool) else ("true" if value else "false"))' "$1"
}

assert_json() {
  local body="$1"
  local path="$2"
  local expected="$3"
  local actual
  actual="$(printf '%s' "$body" | json_get "$path")"
  [[ "$actual" == "$expected" ]] || fail "$path expected ${expected@Q}, got ${actual@Q} in: $body"
}

wait_http() {
  local url="$1"
  local elapsed=0
  echo "Waiting for ${url} ..."
  while (( elapsed < timeout_seconds )); do
    if curl --silent --show-error --fail --max-time 2 --head "$url" >/dev/null 2>&1 \
      || curl --silent --show-error --fail --max-time 2 "$url" >/dev/null 2>&1; then
      echo "HTTP ready: ${url}"
      return 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done
  fail "${url} was not reachable within ${timeout_seconds}s"
}

if [[ "$skip_systemd" != "1" ]]; then
  require_cmd systemctl
  require_cmd getent

  state="$(systemctl is-system-running || true)"
  [[ "$state" == "running" ]] || fail "systemctl is-system-running reported ${state@Q}, expected running"

  failed_system="$(systemctl --failed --no-legend --plain || true)"
  [[ -z "$failed_system" ]] || fail "system-scope failed units: ${failed_system}"

  service_uid="$(getent passwd "$service_user" | cut -d: -f3)"
  [[ -n "$service_uid" ]] || fail "service user ${service_user} was not found"

  dropin="/etc/systemd/system/user@${service_uid}.service.d/20-kautuka-restart.conf"
  [[ -f "$dropin" ]] || fail "missing ${dropin}"

  export XDG_RUNTIME_DIR="/run/user/${service_uid}"
  failed_user="$(runuser -u "$service_user" -- systemctl --user --failed --no-legend --plain || true)"
  [[ -z "$failed_user" ]] || fail "user-scope failed units: ${failed_user}"

  assert_unit_active() {
    local scope="$1"
    local unit="$2"
    local state
    if [[ "$scope" == "user" ]]; then
      state="$(runuser -u "$service_user" -- systemctl --user show -p ActiveState --value "$unit" 2>/dev/null || true)"
    else
      state="$(systemctl show -p ActiveState --value "$unit" 2>/dev/null || true)"
    fi
    [[ "$state" == "active" ]] || fail "${unit} ActiveState=${state@Q}, expected active (activating auto-restart is a crash loop)"
  }

  assert_unit_active system kautuka-gateway.service
  assert_unit_active system kautuka-channelhost.service
  assert_unit_active system kautuka-credentialshost.service
  assert_unit_active user kautuka-manager.service
  assert_unit_active user kautuka-webhost.service
  assert_unit_active user kautuka-updateservice.service
  echo "systemd checks passed (user@${service_uid} drop-in present; core units active)."
fi

wait_http "$base_url/"

health="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/runtime/system-health")"
assert_json "$health" "workspace.status" "healthy"
assert_json "$health" "lifecycle.coreReady" "true"
echo "system-health: workspace healthy, lifecycle.coreReady=true"

setup_status="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/auth/setup/status")"
needs_setup="$(printf '%s' "$setup_status" | json_get needsSetup)"

if [[ "$needs_setup" == "true" ]]; then
  setup_code="$(curl --silent --show-error --output /tmp/kautuka-smoke-setup.json --write-out '%{http_code}' --max-time 10 \
    -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"password":sys.argv[2]}))' "$setup_username" "$setup_password")" \
    "${base_url}/api/auth/setup")"
  [[ "$setup_code" == "200" ]] || fail "POST /api/auth/setup returned ${setup_code}: $(cat /tmp/kautuka-smoke-setup.json)"
  echo "created first admin ${setup_username}"
elif [[ "$needs_setup" == "false" ]]; then
  echo "setup already completed; logging in as ${setup_username}"
else
  fail "unexpected setup status: ${setup_status}"
fi

login_body="$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"password":sys.argv[2]}))' "$setup_username" "$setup_password")"
login_code="$(curl --silent --show-error --output /tmp/kautuka-smoke-login.json --write-out '%{http_code}' --max-time 10 \
  -H 'Content-Type: application/json' \
  -d "$login_body" \
  "${base_url}/api/auth/login")"
[[ "$login_code" == "200" ]] || fail "POST /api/auth/login returned ${login_code}: $(cat /tmp/kautuka-smoke-login.json)"

access_token="$(json_get accessToken < /tmp/kautuka-smoke-login.json)"
[[ -n "$access_token" ]] || fail "login response missing accessToken"

readiness="$(curl --silent --show-error --fail --max-time 15 \
  -H "Authorization: Bearer ${access_token}" \
  "${base_url}/api/runtime/execution-readiness")"
assert_json "$readiness" "ready" "true"
assert_json "$readiness" "code" "READY"

python3 - "$readiness" <<'PY'
import json, sys
body = json.loads(sys.argv[1])
deps = {item["dependency"]: item for item in body.get("dependencies") or []}
for name in ("manager", "credentials_host"):
    item = deps.get(name)
    if item is None:
        raise SystemExit(f"execution-readiness missing dependency {name}: {body}")
    if not item.get("ready"):
        raise SystemExit(f"dependency {name} not ready: {item}")
print("execution-readiness: READY (manager, credentials_host)")
PY

rm -f /tmp/kautuka-smoke-setup.json /tmp/kautuka-smoke-login.json
echo "installed-linux-smoke passed against ${base_url}"
