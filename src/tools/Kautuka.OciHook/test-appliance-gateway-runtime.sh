#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 2 || $(id -u) == 0 ]]; then
  echo 'Usage: test-appliance-gateway-runtime.sh <native-hook> <local-image-with-sleep>' >&2
  exit 2
fi
hook=$(realpath -- "$1")
image=$2
[[ -x "$hook" ]]
work=$(mktemp -d /var/tmp/kautuka-gateway-nft.XXXXXXXX)
network="gateway-test-${work##*.}"
gateway="$network-gateway"
boundary="$network-boundary"
mkdir -p "$work/hooks" "$work/state/kautuka/nac" "$work/tmp"
runtime=(podman --root "$work/store" --runroot "$work/run" --tmpdir "$work/tmp" --storage-driver overlay)
server_pid=''
cleanup() {
  local result=$?
  if (( result != 0 )); then cat "$work/state/kautuka/nac/hook.log" >&2 2>/dev/null || true; fi
  [[ -z "$server_pid" ]] || kill "$server_pid" >/dev/null 2>&1 || true
  "${runtime[@]}" rm --force --ignore "$gateway" "$boundary" >/dev/null 2>&1 || true
  "${runtime[@]}" network rm "$network" >/dev/null 2>&1 || true
  if [[ $(realpath -- "$work") == "$work" && "$work" == /var/tmp/kautuka-gateway-nft.* ]]; then
    podman unshare rm -rf -- "$work"
  fi
}
trap cleanup EXIT
podman save "$image" | "${runtime[@]}" load >/dev/null
"${runtime[@]}" network create --internal --disable-dns --subnet 10.247.0.0/16 "$network" >/dev/null
"${runtime[@]}" run --detach --pull=never --name "$boundary" --network "$network:ip=10.247.0.2" "$image" sleep 120 >/dev/null
boundary_pid=$("${runtime[@]}" inspect --format '{{.State.Pid}}' "$boundary")
install -m 0700 "$hook" "$work/hooks/kautuka-oci-hook"
jq -n --arg path "$work/hooks/kautuka-oci-hook" --arg xdg "XDG_STATE_HOME=$work/state" \
  '{version:"1.0.0",hook:{path:$path,env:[$xdg],timeout:45},when:{always:true},stages:["createRuntime"]}' > "$work/hooks/kautuka.json"
image_id=$("${runtime[@]}" image inspect --format '{{.Id}}' "$image")
[[ "$image_id" == sha256:* ]] || image_id="sha256:$image_id"
cid=$("${runtime[@]}" --hooks-dir "$work/hooks" create --pull=never --name "$gateway" \
  --network "$network:ip=10.247.0.4" --annotation io.kautuka.role=appliance-gateway \
  --annotation "io.kautuka.image-id=$image_id" --cap-drop ALL --read-only "$image" sleep 120)
(umask 077; jq -n --arg id "$cid" --arg digest "$image_id" \
  '{entries:[{id:$id,role:"appliance-gateway",expected_ipv4:["10.247.0.4"],image_digest:$digest}]}' > "$work/state/kautuka/nac/infra-allowlist.json")
"${runtime[@]}" --hooks-dir "$work/hooks" start "$cid" >/dev/null
pid=$("${runtime[@]}" inspect --format '{{.State.Pid}}' "$cid")
"${runtime[@]}" unshare nsenter -t "$pid" -n nft -j list table inet kautuka_workload > "$work/applied.json"
[[ -z "${KAUTUKA_GATEWAY_GOLDEN_OUTPUT:-}" ]] || cp -- "$work/applied.json" "$KAUTUKA_GATEWAY_GOLDEN_OUTPUT"
jq -e --arg cid "$cid" 'select(.id == $cid and .action == "allow" and .reason == "appliance_gateway_rules" and (.rules_sha256 | test("^[0-9a-f]{64}$")))' "$work/state/kautuka/nac/hook.log" >/dev/null
cat > "$work/server.py" <<'PY'
import select, socket, time
listeners=[]
for port in (53, 15001, 15004):
    server=socket.socket(); server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(('10.247.0.2', port)); server.listen(); listeners.append(server)
udp=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); udp.bind(('10.247.0.2', 53)); listeners.append(udp)
print('ready', flush=True)
end=time.monotonic()+30
while time.monotonic()<end:
    ready,_,_=select.select(listeners, [], [], 1)
    for server in ready:
        if server is udp:
            data,addr=udp.recvfrom(16); udp.sendto(data,addr)
        else:
            conn,_=server.accept(); conn.close()
PY
"${runtime[@]}" unshare nsenter -t "$boundary_pid" -n python3 "$work/server.py" > "$work/server.log" 2>&1 &
server_pid=$!
for _ in $(seq 1 40); do grep -q '^ready$' "$work/server.log" && break; sleep .1; done
grep -q '^ready$' "$work/server.log" || { cat "$work/server.log" >&2; exit 1; }
cat > "$work/client.py" <<'PY'
import socket
for port in (15004,53):
    with socket.create_connection(('10.247.0.2',port),timeout=2): pass
with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sock:
    sock.settimeout(2); sock.sendto(b'ok',('10.247.0.2',53)); assert sock.recv(8)==b'ok'
try:
    socket.create_connection(('10.247.0.2',15001),timeout=1)
except (TimeoutError,OSError): pass
else: raise SystemExit('unexpected direct 15001 connection')
try:
    socket.create_connection(('::1',15004),timeout=1)
except (TimeoutError,OSError): pass
else: raise SystemExit('unexpected IPv6 connection')
print('CONNECT and DNS allowed; direct 15001 and IPv6 blocked')
PY
"${runtime[@]}" unshare nsenter -t "$pid" -n python3 "$work/client.py"
"${runtime[@]}" unshare nsenter -t "$pid" -n nft -j list table inet kautuka_workload > "$work/after-client.json"
ipv6_drops=$(jq '[.nftables[] | select(.rule.comment == "ipv6_drop") | .rule.expr[] | select(has("counter")) | .counter.packets] | add // 0' "$work/after-client.json")
printf 'Observed IPv6 drop counter: %s\n' "$ipv6_drops"
printf 'Gateway hook applied and verified nft hash: '
jq -r --arg cid "$cid" 'select(.id == $cid and .reason == "appliance_gateway_rules") | .rules_sha256' "$work/state/kautuka/nac/hook.log"
