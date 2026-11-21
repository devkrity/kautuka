#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" || $EUID -eq 0 ]]; then
    echo 'Usage: test-gateway-connect-listener.sh <built NetworkAccess.Acceptance DLL> (Linux rootless Podman)' >&2
    exit 2
fi
fixture_dll="$(realpath -- "$1")"
script_dir="$(cd "$(dirname "$0")" && pwd)"
readarray -t images < <(python3 - "$script_dir/../src/backend/Kautuka.Manager/runtime-images.json" "$(uname -m)" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    inventory = json.load(stream)
field = {"x86_64": "image", "aarch64": "arm64Image"}[sys.argv[2]]
for role in ("envoy", "coredns"):
    print(next(item[field] for item in inventory["images"] if item["id"] == role))
PY
)
[[ ${#images[@]} -eq 2 ]]
envoy_image="${images[0]}"
dns_image="${images[1]}"
podman image exists "$envoy_image"
podman image exists "$dns_image"

temp="$(mktemp -d)"
containers=()
auth_pid=''
origin_pid=''
cleanup() {
    if [[ -s "$temp/origin.pid" ]]; then kill "$(cat "$temp/origin.pid")" 2>/dev/null || true; fi
    if [[ -n "$origin_pid" ]]; then kill "$origin_pid" 2>/dev/null || true; wait "$origin_pid" 2>/dev/null || true; fi
    for (( i=${#containers[@]}-1; i>=0; i-- )); do
        podman rm -f --time 0 "${containers[i]}" >/dev/null 2>&1 || true
    done
    if [[ -n "$auth_pid" ]]; then kill "$auth_pid" 2>/dev/null || true; wait "$auth_pid" 2>/dev/null || true; fi
    rm -rf -- "$temp"
}
trap cleanup EXIT

mkdir -m 0700 "$temp/certs"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 1 \
    -subj '/CN=Gateway CONNECT fixture CA' -keyout "$temp/ca.key" -out "$temp/certs/gateway-ca.crt" 2>/dev/null
sign_cert() {
    local name="$1" san="$2" role="$3"
    openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
        -subj "/CN=$name" -keyout "$temp/certs/$name.key" -out "$temp/$name.csr" 2>/dev/null
    openssl x509 -req -in "$temp/$name.csr" -CA "$temp/certs/gateway-ca.crt" -CAkey "$temp/ca.key" \
        -CAcreateserial -days 1 -out "$temp/certs/$name.crt" \
        -extfile <(printf 'subjectAltName=%s\nextendedKeyUsage=%s\n' "$san" "$role") 2>/dev/null
}
sign_cert server 'DNS:kautuka-connect.internal' serverAuth
sign_cert gateway 'URI:spiffe://kautuka.internal/gateway' clientAuth
sign_cert wrong 'URI:spiffe://kautuka.internal/other-service' clientAuth

dotnet "$fixture_dll" --gateway-connect-fixture "$temp/authorization" >"$temp/auth.log" 2>&1 &
auth_pid=$!
for _ in {1..100}; do
    [[ -S "$temp/authorization/envoy.sock" ]] && break
    kill -0 "$auth_pid" 2>/dev/null || { cat "$temp/auth.log" >&2; exit 1; }
    sleep 0.1
done
[[ -S "$temp/authorization/envoy.sock" ]] || { cat "$temp/auth.log" >&2; exit 1; }

cat > "$temp/Corefile" <<'COREFILE'
.:10053 {
    bind 127.0.0.1
    hosts {
        93.184.216.34 fixture-public.test
        192.168.88.2 fixture-lan.test fixture-public-to-lan.test
        127.0.0.1 fixture-lan-to-loopback.test
    }
}
COREFILE
dns="$(podman run -d --pull=never --network none \
    -v "$temp/Corefile:/Corefile:ro,z" "$dns_image" -conf /Corefile)"
containers+=("$dns")
net_pid="$(podman inspect --format '{{.State.Pid}}' "$dns")"
[[ "$net_pid" =~ ^[1-9][0-9]*$ ]] || { podman logs "$dns" >&2; exit 1; }
in_fixture_net() {
    podman unshare nsenter --net="/proc/$net_pid/ns/net" "$@"
}
for address in 10.247.0.2 10.248.0.3 93.184.216.34 192.168.88.2; do
    in_fixture_net ip address add "$address/32" dev lo
done

cat > "$temp/origin.py" <<'PY'
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        address, _ = self.connection.getsockname()
        body = f"{address} via {self.client_address[0]}".encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


with HTTPServer(("0.0.0.0", 18080), Handler) as server:
    with open(sys.argv[1], "w", encoding="ascii") as pid_file:
        print(os.getpid(), file=pid_file)
    server.serve_forever()
PY
in_fixture_net python3 "$temp/origin.py" "$temp/origin.pid" >"$temp/origin.log" 2>&1 &
origin_pid=$!

mkdir "$temp/admin"
# The fixture authorization socket belongs to a temporary host process.
# The appliance has its own SELinux policy for Manager's authorization socket.
envoy_args=(--pull=never --network "container:$dns" --security-opt label=disable --user 0 --entrypoint /usr/local/bin/envoy
    -v "$temp/authorization:/run/kautuka-nac:ro,z"
    -v "$temp/certs:/etc/kautuka-connect:ro,z"
    -v "$temp/admin:/run/kautuka-admin:z"
    "$envoy_image" --config-path /run/kautuka-nac/envoy.yaml)
podman run --rm "${envoy_args[@]}" --mode validate --log-level error
envoy="$(podman run -d "${envoy_args[@]}" --log-level error)"
containers+=("$envoy")
for _ in {1..100}; do
    [[ -S "$temp/admin/admin.sock" ]] && break
    sleep 0.1
done
[[ -S "$temp/admin/admin.sock" ]] || {
    echo 'Envoy did not publish its fixture readiness socket' >&2
    ls -la "$temp/admin" >&2
    podman logs "$envoy" >&2
    exit 1
}

proxy=(--silent --show-error --max-time 10 --noproxy '' --proxytunnel
    --resolve kautuka-connect.internal:15004:10.247.0.2
    --proxy "https://kautuka-connect.internal:15004"
    --proxy-cacert "$temp/certs/gateway-ca.crt"
    --proxy-header 'x-kautuka-user: fixture-user'
    --proxy-header 'x-kautuka-agent: fixture-agent')
request() {
    local host="$1" certificate="$2"
    shift 2
    local auth=()
    if [[ -n "$certificate" ]]; then
        auth=(--proxy-cert "$temp/certs/$certificate.crt" --proxy-key "$temp/certs/$certificate.key")
    fi
    in_fixture_net curl "${proxy[@]}" "${auth[@]}" "$@" \
        --output "$temp/body" --write-out '%{http_connect} %{http_code}' "http://$host:18080/"
}
expect_tunnel() {
    local host="$1" expected_address="$2" expected_source="$3"
    shift 3
    local status
    if ! status="$(request "$host" gateway "$@")"; then
        echo "$host failed to open a tunnel ($status)" >&2
        cat "$temp/auth.log" >&2
        podman logs "$envoy" >&2
        return 1
    fi
    [[ "$status" == '200 200' && "$(cat "$temp/body")" == "$expected_address via $expected_source" ]] || {
        echo "$host returned $status with $(cat "$temp/body")" >&2
        podman logs "$envoy" >&2
        return 1
    }
}
expect_denied() {
    local host="$1" certificate="$2" expected="$3"
    shift 3
    local status
    if status="$(request "$host" "$certificate" "$@" 2>"$temp/curl-error")"; then
        echo "$host unexpectedly reached the origin ($status)" >&2
        return 1
    fi
    [[ "$status" == "$expected 000" ]] || {
        echo "$host returned $status instead of $expected 000" >&2
        cat "$temp/curl-error" >&2
        podman logs "$envoy" >&2
        return 1
    }
}

expect_tunnel fixture-public.test 93.184.216.34 93.184.216.34
expect_tunnel fixture-public.test 93.184.216.34 93.184.216.34 --proxy-header 'x-kautuka-public-only: true'
expect_tunnel fixture-lan.test 192.168.88.2 10.248.0.3
expect_denied fixture-lan.test gateway 503 --proxy-header 'x-kautuka-public-only: true'
expect_denied fixture-lan.test gateway 403 --proxy-header 'x-kautuka-public-only: false'
expect_denied fixture-lan.test '' 000
expect_denied fixture-lan.test wrong 000
expect_denied fixture-lan.test gateway 403 --proxy-header 'x-kautuka-agent: other-agent'
expect_denied fixture-public-to-lan.test gateway 503 --proxy-header 'x-kautuka-egress-class: lan'
expect_denied fixture-lan-to-loopback.test gateway 503

status="$(in_fixture_net curl --silent --show-error --max-time 10 --noproxy '' \
    --resolve kautuka-connect.internal:15004:10.247.0.2 \
    --cacert "$temp/certs/gateway-ca.crt" --cert "$temp/certs/gateway.crt" --key "$temp/certs/gateway.key" \
    --output /dev/null --write-out '%{http_code}' https://kautuka-connect.internal:15004/)"
[[ "$status" == 403 ]]

kill "$auth_pid"
wait "$auth_pid"
auth_pid=''
expect_denied fixture-public.test gateway 403
echo 'Pinned Envoy enforced mTLS, caller scope, CoreDNS resolution and public/LAN routing in an isolated namespace.'
