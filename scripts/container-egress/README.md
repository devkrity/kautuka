# Rootless container egress proof

This is one isolated Fedora 44/Linux feasibility check for the
[container deployment plan](../../docs/plans/container-egress.md). It does not
change application startup, an installed Kautuka deployment, host routes or the
host firewall. It creates a fresh rootless runtime account, private container
storage and test networks, then removes them. Account allocation is serialized.
Cleanup errors fail the check and preserve the fixture directory for inspection.

The runner requires Linux root for temporary account creation and observation of
the test namespaces. Every Podman container runs under the disposable non-root
runtime account. Short-lived setup containers receive `NET_ADMIN` only within
the namespaces being configured. Workloads do not receive it. Envoy has no
capabilities; stock CoreDNS receives only `NET_BIND_SERVICE`, with NNP enabled.

## Prepare the fixture

Use a dedicated Fedora 44 WSL distro or disposable Linux test environment. The
host needs systemd, Podman, Python, shadow-utils, iproute, nftables, curl, OpenSSL,
tar support and `ldd`. No application settings or credentials are required.

As an ordinary rootless Podman user, prepare the two offline image archives:

```sh
mkdir -p "$HOME/kautuka-egress-fixture-images"
podman pull docker.io/envoyproxy/envoy:distroless-v1.40.2
podman pull docker.io/coredns/coredns:1.14.6
podman save --format oci-archive \
  --output "$HOME/kautuka-egress-fixture-images/envoy.oci" \
  docker.io/envoyproxy/envoy:distroless-v1.40.2
podman save --format oci-archive \
  --output "$HOME/kautuka-egress-fixture-images/dns.oci" \
  docker.io/coredns/coredns:1.14.6
```

These are fixture-bootstrap pulls, not a test of application-managed image
acquisition. The runner loads the archives into its fresh private store, records
their SHA-256 and loaded image IDs, and starts containers with `--pull=never`.
Archive conversion can change an image's ID; the test observes the loaded ID.
This fixture is not the product's signed-release verification mechanism.

Run from the repository in Linux. The report path must not already exist:

```sh
sudo python3 scripts/container-egress/prove-topology.py \
  --image-cache "$HOME/kautuka-egress-fixture-images" \
  --report /var/tmp/kautuka-egress-proof.json
```

The tooling image is imported offline from the host's `ip`, `nft`, `sleep`, curl
and OpenSSL executables and their shared libraries. It is test-only. The TLS and
alternate-DNS containers on the outside bridge simulate remote endpoints; an
installed deployment gives that bridge only to the egress pod.

## What it proves

- A real rootless egress pod joins an internal scope and an outside bridge.
- A one-shot helper sets the client's default route; the workload cannot change it.
- Another helper configures native nftables in the egress pod's actual namespace.
- Stock Envoy recovers source IP, original destination and visible TLS SNI.
- Native `create --interactive` and `init` allocate an address and namespace
  while the application's observable startup marker remains absent. A helper
  joins `ns:<SandboxKey>` to prepare the route without starting the application.
  Explicit `start --attach --interactive` preserves its address, namespace and
  route, with working stdin/stdout/stderr and the application's exit status.
- Approved TLS succeeds through the proxy; a denied name and literal IP fail
  without sending a new SYN to the controlled upstream.
- Attaching a second scope live preserves the outside default and allows both
  scopes to use their permitted connection.
- An unknown source cannot use an approved hostname. A same-scope target is
  reachable, while cross-scope access fails without reaching that target.
- An alternate DNS service answers a positive control, but a private-network
  query cannot reach it. Those UDP probes use ordinary sockets after namespace
  entry and a drop to the runtime UID; they are network-path observations.
- IPv6 addresses and routes are absent from the explicitly IPv4-only client.
- Stopping the second scope's workloads and detaching its network preserves the
  first scope. Stopping Envoy then prevents new upstream connections.
- Running Envoy/CoreDNS capabilities and NNP match the intended boundaries.

Self-signed fixture TLS uses curl's insecure mode. The proof concerns forwarding
and policy, not certificate validation. SNI is metadata, not authenticated
service identity. Disabled IPv6 is not an observed IPv6 firewall-hit test.

Static policy and a stock resolver's fixture zone are used initially. File-xDS
activation, recursive DNS, the host management proxy, application service
containers, update recovery, UI and native-Linux/Mac evidence remain separate
implementation work. This single runner is not a new enforcement service.
