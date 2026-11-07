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
  require_cmd runuser

  # A staged WSL appliance has no installed pointers until firstboot finishes.
  firstboot_deadline=$((SECONDS + timeout_seconds))
  while [[ "$(systemctl show -p LoadState --value kautuka-wsl-firstboot.service)" == loaded ]]; do
    firstboot_state="$(systemctl show -p ActiveState --value kautuka-wsl-firstboot.service)"
    [[ "$firstboot_state" != failed ]] || fail 'WSL firstboot failed'
    if [[ "$firstboot_state" == active && -f /etc/kautuka/wsl-firstboot-complete ]]; then
      break
    fi
    # On subsequent boots ConditionPathExists skips the completed oneshot.
    if [[ "$firstboot_state" == inactive && -f /etc/kautuka/wsl-firstboot-complete ]]; then
      break
    fi
    (( SECONDS < firstboot_deadline )) || fail 'WSL firstboot did not complete within the smoke budget'
    sleep 2
  done

  python3 - "$service_user" <<'PY_SCOPE'
import json, sys
with open('/etc/kautuka/service-scope.json', encoding='utf-8-sig') as stream:
    contract = json.load(stream)
if (contract.get('schemaVersion') != 2 or contract.get('applicationServices') != 'containers'
        or contract.get('username') != sys.argv[1] or contract.get('target') != 'kautuka.target'
        or contract.get('userUnits') != ['kautuka-manager.service', 'kautuka-updateservice.service']
        or contract.get('systemUnits') != []):
    raise SystemExit('installed smoke requires the current container service-scope contract')
PY_SCOPE

  state="$(systemctl is-system-running || true)"
  [[ "$state" == "running" ]] || fail "systemctl is-system-running reported ${state@Q}, expected running"

  failed_system="$(systemctl --failed --no-legend --plain || true)"
  [[ -z "$failed_system" ]] || fail "system-scope failed units: ${failed_system}"

  service_uid="$(getent passwd "$service_user" | cut -d: -f3)"
  [[ -n "$service_uid" ]] || fail "service user ${service_user} was not found"

  dropin="/etc/systemd/system/user@${service_uid}.service.d/20-kautuka-restart.conf"
  [[ -f "$dropin" ]] || fail "missing ${dropin}"

  export XDG_RUNTIME_DIR="/run/user/${service_uid}"
  export DBUS_SESSION_BUS_ADDRESS="unix:path=${XDG_RUNTIME_DIR}/bus"
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
  assert_unit_active user kautuka-updateservice.service
  core_deadline=$((SECONDS + timeout_seconds))
  while true; do
    observed="$(runuser -u "$service_user" -- podman inspect kautuka-credentialshost kautuka-channelhost kautuka-gateway kautuka-webhost 2>/dev/null)" || observed='[]'
    if printf '%s' "$observed" | python3 -c '
import json, re, sys
roles = {"credentialshost", "channelhost", "gateway", "webhost"}
with open("/opt/kautuka/manager/current/runtime-images.json", encoding="utf-8-sig") as stream:
    entries = [entry for entry in json.load(stream)["images"] if entry["id"].startswith("core-")]
images = {entry["id"].removeprefix("core-"): entry["localImageId"].removeprefix("sha256:") for entry in entries}
if len(entries) != 4 or set(images) != roles or any(not re.fullmatch("[0-9a-f]{64}", identity) for identity in images.values()):
    raise SystemExit("invalid installed core image inventory")
actual = json.load(sys.stdin)
if len(actual) != 4: raise SystemExit("expected four core containers")
seen, ids = set(), set()
for container in actual:
    labels = container.get("Config", {}).get("Labels", {})
    service = labels.get("kautuka.core-service")
    identity = container.get("Id", "")
    if (service not in images or service in seen or identity in ids or not re.fullmatch("[0-9a-f]{64}", identity)
            or labels.get("kautuka.managed") != "manager" or container.get("State", {}).get("Status") != "running"
            or container.get("Image", "").removeprefix("sha256:") != images[service]):
        raise SystemExit("core container ownership, state or image mismatch")
    seen.add(service)
    ids.add(identity)
' 2>/dev/null; then
      break
    fi
    (( SECONDS < core_deadline )) || fail 'four owned core containers did not match the installed image inventory'
    sleep 2
  done
  echo "Local layout checks passed (two host units and four Manager-owned, image-bound core containers)."
fi

wait_http "$base_url/"

health="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/runtime/system-health")"
assert_json "$health" "workspace.status" "healthy"
assert_json "$health" "lifecycle.coreReady" "true"
echo "system-health: workspace healthy, lifecycle.coreReady=true"

setup_status="$(curl --silent --show-error --fail --max-time 10 "${base_url}/api/auth/setup/status")"
needs_setup="$(printf '%s' "$setup_status" | json_get needsSetup)"

if [[ "$needs_setup" == "true" ]]; then
  if [[ "$skip_systemd" != "1" && -z "$setup_token" && -r /var/lib/kautuka/gateway-data/setup-token ]]; then
    setup_token="$(cat /var/lib/kautuka/gateway-data/setup-token)"
  fi
  [[ -n "$setup_token" ]] || fail "first-admin setup requires the one-time token; set KAUTUKA_SMOKE_SETUP_TOKEN or run this smoke test as root on the appliance"

  setup_code="$(curl --silent --show-error --output /tmp/kautuka-smoke-setup.json --write-out '%{http_code}' --max-time 10 \
    -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"password":sys.argv[2],"setupToken":sys.argv[3]}))' "$setup_username" "$setup_password" "$setup_token")" \
    "${base_url}/api/auth/setup")"
  [[ "$setup_code" == "200" ]] || fail "POST /api/auth/setup returned ${setup_code}: $(cat /tmp/kautuka-smoke-setup.json)"
  echo "created first admin ${setup_username}"
elif [[ "$needs_setup" == "false" ]]; then
  echo "setup already completed; logging in as ${setup_username}"
else
  fail "unexpected setup status: ${setup_status}"
fi
unset setup_token

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
