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
setup_token="${KAUTUKA_SMOKE_SETUP_TOKEN:-}"
unset KAUTUKA_SMOKE_PASSWORD KAUTUKA_SMOKE_SETUP_TOKEN
appliance_version=""
setup_token_file=/var/lib/kautuka/setup-token

usage() {
  cat <<'EOF'
Usage: scripts/installed-linux-smoke.sh [options]

Options:
  --base-url <url>     WebHost origin. Default: http://127.0.0.1:20229
  --appliance-version VERSION
                       Require this signed container appliance release and its localhost WebHost.
  --skip-systemd       Skip systemctl and drop-in checks (HTTP-only).
  --timeout-seconds N  HTTP wait budget. Default: 180
  --service-user NAME  Appliance user. Default: kautuka
  -h, --help           Show this help.

Environment:
  KAUTUKA_SMOKE_USERNAME / KAUTUKA_SMOKE_PASSWORD / KAUTUKA_SMOKE_SETUP_TOKEN
      First-admin credentials to create, or existing login when setup is done.
      A password is generated when unset. The setup token is read from the
      owner-only local file when this script runs as root and no value is set.
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
    --appliance-version)
      if [[ -z "${2:-}" || "$2" == -* ]]; then
        echo "error: --appliance-version requires a version" >&2
        exit 1
      fi
      appliance_version="$2"
      shift 2
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

if [[ -n "$appliance_version" && "$skip_systemd" == "1" ]]; then
  fail "--appliance-version requires the local Manager and Podman checks"
fi

response_dir="$(mktemp -d)"
trap 'rm -f -- "$response_dir/setup.json" "$response_dir/login.json"; rmdir -- "$response_dir"' EXIT

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

post_auth() {
  local operation="$1"
  shift
  printf '%s\0' "$setup_username" "$setup_password" "$@" \
    | python3 -c 'import json, sys
values = sys.stdin.read().split("\0")[:-1]
print(json.dumps(dict(zip(("username", "password", "setupToken"), values))))' \
    | curl --silent --show-error --output "$response_dir/$operation.json" --write-out '%{http_code}' --max-time 10 \
        -H 'Content-Type: application/json' --data-binary @- "${base_url}/api/auth/$operation"
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

  if [[ -z "$appliance_version" ]]; then
    state="$(systemctl is-system-running || true)"
    [[ "$state" == "running" ]] || fail "systemctl is-system-running reported ${state@Q}, expected running"

    failed_system="$(systemctl --failed --no-legend --plain || true)"
    [[ -z "$failed_system" ]] || fail "system-scope failed units: ${failed_system}"
  fi

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

  assert_unit_active user kautuka-manager.service
  if [[ -n "$appliance_version" ]]; then
    require_cmd podman
    assert_unit_active system kautuka-appliance-update.path
    runuser -u "$service_user" -- /opt/kautuka/manager/current/kautuka-manager \
      --check-appliance-ready --candidate-version "$appliance_version" \
      || fail "Manager did not confirm readiness for appliance release $appliance_version"
    publication="$(runuser -u "$service_user" -- podman port kautuka-appliance-webhost 20229/tcp)" \
      || fail "cannot read the appliance WebHost port"
    [[ "$publication" =~ ^127\.0\.0\.1:([0-9]+)$ ]] || fail "unexpected appliance WebHost publication: $publication"
    web_port="${BASH_REMATCH[1]}"
    [[ "$base_url" == "http://127.0.0.1:$web_port" || "$base_url" == "http://localhost:$web_port" ]] \
      || fail "browser origin does not match this appliance's published port $web_port"
    setup_token_file=/var/lib/kautuka/gateway/setup-token
    echo "systemd checks passed (Manager ready for $appliance_version and the activation watcher is active)."
  else
    assert_unit_active system kautuka-gateway.service
    assert_unit_active system kautuka-channelhost.service
    assert_unit_active system kautuka-credentialshost.service
    assert_unit_active user kautuka-webhost.service
    assert_unit_active user kautuka-updateservice.service
    echo "systemd checks passed (user@${service_uid} drop-in present; core units active)."
  fi
fi

wait_http "$base_url/"

health="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/runtime/system-health")"
assert_json "$health" "workspace.status" "healthy"
assert_json "$health" "lifecycle.coreReady" "true"
echo "system-health: workspace healthy, lifecycle.coreReady=true"

setup_status="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/auth/setup/status")"
needs_setup="$(printf '%s' "$setup_status" | json_get needsSetup)"

if [[ "$needs_setup" == "true" ]]; then
  if [[ -z "$setup_token" && -r "$setup_token_file" ]]; then
    setup_token="$(cat "$setup_token_file")"
  fi
  [[ -n "$setup_token" ]] || fail "first-admin setup requires the one-time token; set KAUTUKA_SMOKE_SETUP_TOKEN or run this smoke test as root on the appliance"

  setup_code="$(post_auth setup "$setup_token")"
  [[ "$setup_code" == "200" ]] || fail "POST /api/auth/setup returned ${setup_code}"
  echo "created first admin ${setup_username}"
elif [[ "$needs_setup" == "false" ]]; then
  echo "setup already completed; logging in as ${setup_username}"
else
  fail "unexpected setup status: ${setup_status}"
fi
unset setup_token

login_code="$(post_auth login)"
[[ "$login_code" == "200" ]] || fail "POST /api/auth/login returned ${login_code}"

access_token="$(json_get accessToken < "$response_dir/login.json")"
[[ -n "$access_token" ]] || fail "login response missing accessToken"

readiness="$(printf 'Authorization: Bearer %s\n' "$access_token" \
  | curl --silent --show-error --fail --max-time 15 -H @- "${base_url}/api/runtime/execution-readiness")"
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

echo "installed-linux-smoke passed against ${base_url}"
