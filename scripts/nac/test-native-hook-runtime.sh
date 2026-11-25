#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 ]]; then
  echo 'Usage: bash scripts/nac/test-native-hook-runtime.sh <published-native-hook> <local-test-image-with-sleep>' >&2
  exit 2
fi

hook=$(realpath -- "$1")
image=$2
[[ -x "$hook" && $(id -u) != 0 ]]
command -v jq >/dev/null
test_root=$(mktemp -d /var/tmp/kautuka-native-hook.XXXXXXXX)
test_name="native-hook-${test_root##*.}"
mkdir -p "$test_root/hooks" "$test_root/state/kautuka/nac" "$test_root/tmp"
runtime=(podman --root "$test_root/storage" --runroot "$test_root/run" --tmpdir "$test_root/tmp" --storage-driver overlay)
cleanup() {
  "${runtime[@]}" rm --force --ignore "$test_name" >/dev/null 2>&1 || true
  "${runtime[@]}" network rm "$test_name" >/dev/null 2>&1 || true
  if [[ $(realpath -- "$test_root") == "$test_root" && "$test_root" == /var/tmp/kautuka-native-hook.* ]]; then
    podman unshare rm -rf -- "$test_root"
  fi
}
trap cleanup EXIT

podman save "$image" | "${runtime[@]}" load >/dev/null
"${runtime[@]}" network create --internal --disable-dns --subnet 10.247.0.0/16 "$test_name" >/dev/null
install -m 0700 "$hook" "$test_root/hooks/kautuka-oci-hook"
jq -n --arg path "$test_root/hooks/kautuka-oci-hook" --arg xdg "XDG_STATE_HOME=$test_root/state" '{
  version: "1.0.0",
  hook: {path: $path, env: [$xdg], timeout: 45},
  when: {always: true},
  stages: ["createRuntime"]
}' > "$test_root/hooks/kautuka.json"

launch=("${runtime[@]}" --hooks-dir "$test_root/hooks" run --detach --pull=never --name "$test_name"
  --network "$test_name:ip=10.247.23.17" --annotation io.kautuka.role=workload
  --cap-drop ALL --security-opt no-new-privileges --read-only "$image" sleep 60)
log="$test_root/state/kautuka/nac/hook.log"
if "${launch[@]}" > "$test_root/refused.stdout" 2> "$test_root/refused.stderr"; then
  echo 'A workload started without its infrastructure allowlist.' >&2
  exit 1
fi
if ! jq -e 'select(.action == "refuse" and .reason == "missing_allowlist")' "$log" >/dev/null; then
  cat "$test_root/refused.stderr" >&2
  exit 1
fi
"${runtime[@]}" rm --force --ignore "$test_name" >/dev/null

(umask 077; printf '{"entries":[]}\n' > "$test_root/state/kautuka/nac/infra-allowlist.json")
cid=$("${launch[@]}")
pid=$("${runtime[@]}" inspect --format '{{.State.Pid}}' "$cid")
"${runtime[@]}" unshare nsenter --target "$pid" --net nft -j list table inet kautuka_workload > "$test_root/applied.json"
jq -e '.nftables[] | select(.chain.name == "output" and .chain.policy == "drop")' "$test_root/applied.json" >/dev/null
jq -e --arg cid "$cid" '
  select(.id == $cid and .action == "allow" and .reason == "workload_rules" and
    (.rules_sha256 | test("^[0-9a-f]{64}$")))
' "$log" >/dev/null
jq -e --arg cid "$cid" --argjson pid "$pid" '.[] | select(.id == $cid and .pid == $pid)' \
  "$test_root/state/kautuka/nac/netns-owners.json" >/dev/null
"${runtime[@]}" rm --force "$test_name" >/dev/null

image_id=$("${runtime[@]}" image inspect --format '{{.Id}}' "$image")
proxy=$("${runtime[@]}" --hooks-dir "$test_root/hooks" create --pull=never --name "$test_name" \
  --network "$test_name:ip=10.247.0.3" --annotation io.kautuka.role=package-proxy \
  --annotation "io.kautuka.image-id=$image_id" --cap-drop ALL --read-only "$image" sleep 60)
(umask 077; jq -n --arg id "$proxy" --arg digest "$image_id" \
  '{entries: [{id: $id, role: "pkgs-proxy", expected_ipv4: ["10.247.0.3"], image_digest: $digest}]}' \
  > "$test_root/state/kautuka/nac/infra-allowlist.json")
"${runtime[@]}" --hooks-dir "$test_root/hooks" start "$proxy" >/dev/null
jq -e --arg cid "$proxy" '
  select(.id == $cid and .action == "allow" and .reason == "package_proxy_rules" and
    (.rules_sha256 | test("^[0-9a-f]{64}$")))
' "$log" >/dev/null
printf 'Native hook verified rootless Podman workload and package-proxy rules (%s, %s)\n' "$cid" "$proxy"
