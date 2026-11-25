#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 2 || $(id -u) == 0 ]]; then echo 'Usage: test-appliance-webhost-runtime.sh <native-hook> <local-image>' >&2; exit 2; fi
hook=$(realpath -- "$1")
image=$2
work=$(mktemp -d /var/tmp/kautuka-webhost-nft.XXXXXXXX)
network="webhost-test-${work##*.}"
web="$network-web"
boundary="$network-boundary"
mkdir -p "$work/hooks" "$work/state/kautuka/nac" "$work/tmp" "$work/netconfig"
runtime=(podman --root "$work/store" --runroot "$work/run" --tmpdir "$work/tmp" --storage-driver overlay --network-config-dir "$work/netconfig")
server_pid=''
boundary_server_pid=''
cleanup() {
  local result=$?
  if (( result != 0 )); then cat "$work/state/kautuka/nac/hook.log" >&2 2>/dev/null || true; fi
  [[ -z "$server_pid" ]] || kill "$server_pid" >/dev/null 2>&1 || true
  [[ -z "$boundary_server_pid" ]] || kill "$boundary_server_pid" >/dev/null 2>&1 || true
  "${runtime[@]}" rm --force --ignore "$web" "$boundary" >/dev/null 2>&1 || true
  "${runtime[@]}" network rm "$network" >/dev/null 2>&1 || true
  if [[ $(realpath -- "$work") == "$work" && "$work" == /var/tmp/kautuka-webhost-nft.* ]]; then podman unshare rm -rf -- "$work"; fi
}
trap cleanup EXIT
podman save "$image" | "${runtime[@]}" load >/dev/null
"${runtime[@]}" network create --internal --disable-dns --subnet 10.247.0.0/16 "$network" >/dev/null
"${runtime[@]}" run --detach --pull=never --name "$boundary" --network "$network:ip=10.247.0.2" "$image" sleep 120 >/dev/null
install -m 0700 "$hook" "$work/hooks/kautuka-oci-hook"
jq -n --arg path "$work/hooks/kautuka-oci-hook" --arg xdg "XDG_STATE_HOME=$work/state" \
  '{version:"1.0.0",hook:{path:$path,env:[$xdg],timeout:45},when:{always:true},stages:["createRuntime"]}' > "$work/hooks/kautuka.json"
image_id=$("${runtime[@]}" image inspect --format '{{.Id}}' "$image")
[[ "$image_id" == sha256:* ]] || image_id="sha256:$image_id"
cid=$("${runtime[@]}" --hooks-dir "$work/hooks" create --pull=never --name "$web" \
  --network "$network:ip=10.247.0.5" --publish 127.0.0.1::20229 --annotation io.kautuka.role=appliance-webhost \
  --annotation "io.kautuka.image-id=$image_id" --userns=keep-id:uid=65532,gid=65532 --user=65532:65532 --cap-drop ALL --read-only "$image" sleep 120)
(umask 077; jq -n --arg id "$cid" --arg digest "$image_id" \
  '{entries:[{id:$id,role:"appliance-webhost",expected_ipv4:["10.247.0.5"],image_digest:$digest}]}' > "$work/state/kautuka/nac/infra-allowlist.json")
"${runtime[@]}" --hooks-dir "$work/hooks" start "$cid" >/dev/null
pid=$("${runtime[@]}" inspect --format '{{.State.Pid}}' "$cid")
actual_inode=$(stat -Lc '%i' "/proc/$pid/ns/net")
audit_inode=$(jq -r --arg cid "$cid" 'select(.id == $cid and .reason == "appliance_webhost_rules") | .netns_inode' "$work/state/kautuka/nac/hook.log")
[[ "$actual_inode" == "$audit_inode" ]]
jq -e --arg cid "$cid" 'select(.id == $cid and .reason == "appliance_webhost_rules" and .ips == [] and (.rules_sha256 | test("^[0-9a-f]{64}$")))' "$work/state/kautuka/nac/hook.log" >/dev/null
"${runtime[@]}" unshare nsenter -t "$pid" -n nft -j list table inet kautuka_workload > "$work/applied.json"
[[ -z "${KAUTUKA_WEBHOST_GOLDEN_OUTPUT:-}" ]] || cp -- "$work/applied.json" "$KAUTUKA_WEBHOST_GOLDEN_OUTPUT"
cat > "$work/server.py" <<'PY'
import socket
s=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);s.bind(('0.0.0.0',20229));s.listen()
print('ready',flush=True)
c,_=s.accept();c.sendall(b'webhost-ok');c.close()
PY
"${runtime[@]}" unshare nsenter -t "$pid" -n python3 "$work/server.py" > "$work/server.log" 2>&1 &
server_pid=$!
for _ in $(seq 1 40); do grep -q '^ready$' "$work/server.log" && break; sleep .1; done
grep -q '^ready$' "$work/server.log" || { cat "$work/server.log" >&2; exit 1; }
port=$("${runtime[@]}" port "$cid" 20229/tcp)
python3 - "$port" <<'PY'
import socket,sys
host,port=sys.argv[1].rsplit(':',1)
assert host=='127.0.0.1'
with socket.create_connection((host,int(port)),timeout=4) as s: assert s.recv(32)==b'webhost-ok'
print('loopback-published ingress and established reply succeeded')
PY
cat > "$work/boundary.py" <<'PY'
import socket,time
s=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);s.bind(('10.247.0.2',15004));s.listen()
print('ready',flush=True)
time.sleep(30)
PY
boundary_pid=$("${runtime[@]}" inspect --format '{{.State.Pid}}' "$boundary")
"${runtime[@]}" unshare nsenter -t "$boundary_pid" -n python3 "$work/boundary.py" > "$work/boundary.log" 2>&1 &
boundary_server_pid=$!
for _ in $(seq 1 40); do grep -q '^ready$' "$work/boundary.log" && break; sleep .1; done
grep -q '^ready$' "$work/boundary.log" || { cat "$work/boundary.log" >&2; exit 1; }
cat > "$work/client.py" <<'PY'
import socket
try: socket.create_connection(('10.247.0.2',15004),timeout=1)
except (TimeoutError,OSError): pass
else: raise SystemExit('unexpected new outbound connection')
try: socket.create_connection(('::1',20229),timeout=1)
except (TimeoutError,OSError): pass
else: raise SystemExit('unexpected IPv6 connection')
print('new outbound and IPv6 connections blocked')
PY
"${runtime[@]}" unshare nsenter -t "$pid" -n python3 "$work/client.py"
"${runtime[@]}" unshare nsenter -t "$pid" -n nft -j list table inet kautuka_workload > "$work/after.json"
final_drops=$(jq '[.nftables[] | select(.rule.comment == "final_drop") | .rule.expr[] | select(has("counter")) | .counter.packets] | add // 0' "$work/after.json")
[[ "$final_drops" -gt 0 ]]
printf 'Observed outbound drop counter: %s\n' "$final_drops"
printf 'IPv6 drop counter: '
jq '[.nftables[] | select(.rule.comment == "ipv6_drop") | .rule.expr[] | select(has("counter")) | .counter.packets] | add // 0' "$work/after.json"
printf 'WebHost verified nft hash: '
jq -r --arg cid "$cid" 'select(.id == $cid and .reason == "appliance_webhost_rules") | .rules_sha256' "$work/state/kautuka/nac/hook.log"
