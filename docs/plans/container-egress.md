# Container deployment and outbound access control

**Status:** Active — revision 6 with architect-review corrections.
**Owner:** Kautuka runtime and UpdateService maintainers.
**Activation condition:** User-authorised replacement design; first demonstrate
the small rootless egress topology before integrating application deployment.
Date: 7 November 2026.
Branch: `enhancement/network-access-control-v6`, from `main` at `5ca94f0`.

This replaces the earlier network-access-control proposal and package-policy
addendum. `enhancement/network-access-control` remains a parked research spike;
its NFQUEUE implementation is not a dependency. This document describes intended
behavior, not an assertion that main already implements it.

## Outcome and boundaries

Containerise every application service except host Manager and UpdateService.
All external TCP connections from managed core and workload containers pass
through one Envoy intermediary. Unknown destinations are denied. Attribute
proxy connection events to their container and expose logs and approvals through
the existing web/phone flow. DNS uses the designated resolver; unsupported
external protocols and private-network IPv6 are denied in v1.

Preserve the existing application execution boundary:

```text
Kautuka Gateway -> Manager -> Podman run/exec -> connector or sandbox process
                <- Manager <- stdio/captured execution results
```

Workloads receive no direct Gateway or Manager execution endpoint. Container
network changes do not replace the existing execution transport.

## Native deployment layout

Use one dedicated rootless Podman runtime owner. Core, agent and egress
containers share that owner's host trust boundary. Their network/filesystem
separation does not protect against compromise of that host account. The former
mandatory separate-runtime-owner design is superseded; no rootful fallback.

| Network | Members | External attachment |
| --- | --- | --- |
| `kautuka-core` | Core service containers and egress pod | None |
| `kautuka-<user>-<agent>` | That scope's sandboxes, skills, MCP and connector instances, plus egress pod | None |
| `kautuka-outside` | Trusted egress pod only | Normal Podman outbound network |

Each private bridge has a distinct subnet. Containers within the same scope may
communicate directly. Different scopes, including agents of the same user, are
isolated. Ordinary forwarding through the egress pod is denied, including between
private networks. Core communication with workload processes remains through
Manager. WebHost is the published browser/phone entry point.

Use separate containers for separate outbound identities. Pods are only for
members intentionally sharing one network identity. The trusted egress pod
contains Envoy and a stock DNS resolver, with no application code, credentials or
workspace mounts. CredentialsHost can remain networkless with its private Unix
socket shared only with the required trusted service.

## Routing and DNS

Private networks use Podman's internal bridge mode with network DNS disabled.
Configure the stock egress resolver directly. Where internal names are needed,
use Manager-managed hosts entries; Podman aliases do not resolve without DNS.
Return real DNS answers, not synthetic proxy addresses.

Install external default routes for clients only, through the egress pod's fixed
address on their network. The egress pod retains its sole external default route
through `kautuka-outside`. Podman network-wide `--route` applies to every attached
container, including egress: do not treat a default route to the egress address as
a ready-made deployment recipe. Inspect/reconcile routes after each attachment.

Native netfilter redirection in the egress namespace delivers private-network
TCP connections to Envoy with original destination information. No ordinary
forwarding or masquerading path bypasses Envoy. DNS is accepted only by the
designated resolver. A short-lived setup helper may configure each topology
change; Envoy and DNS retain no `NET_ADMIN`. Workloads have neither `NET_ADMIN`
nor `NET_RAW`, host networking, arbitrary network attachment or control sockets.

Stock CoreDNS 1.14.6 requires the narrow `NET_BIND_SERVICE` capability in the
tested runtime because its executable carries that file capability. Permit only
that capability for DNS, with a non-root user and no-new-privileges; Envoy retains
no capabilities. Observe actual process capability state in the deployment proof.
Do not grant DNS the setup helper's network-administration capability.

Use IPv4-only private networking initially and explicitly deny unsupported IPv6
traffic. The initial demonstration must verify routing and redirection on the
actual rootless Podman/Netavark/kernel versions, including nftables original
destination recovery. Documentation alone is not that proof.

Manager allocates scopes/subnets, attaches egress and retires unused networks.
Set an explicit supported active-network limit based on measurement; defer or
reject allocations at capacity. No automatic per-user fallback or shared pool.
Detach and clean up only after the scope's workloads have stopped. Reapply the
egress boundary before newly attached workloads are made ready.

## Connector identity and policy

Agent-used connector identity is `(user, agent, connector, execution mode)`.
Make standalone `Isolated` execution agent-scoped without granting agent-file
mounts. Keep `IsolatedWithAgentFiles` mounts explicit. `AgentSandbox` continues to
execute inside the corresponding sandbox. Instances reuse images/layers.
Non-agent validation/setup uses an explicit user-management scope; system
connectors use an explicit system scope, not an arbitrary agent's network.

Manager owns fixed address assignments and the address-to-container map. Reject
unknown sources. Remove stale permission before address reuse. Logs retain
identity at event time. This assumes the declared container boundary, not an
uncompromised application process or an authenticated TLS service identity.

Default deny. Initial rules are exact visible TLS SNI + original TCP port, or an
explicit destination IP + TCP port. A hostname rule selects a configured DNS
upstream for that approved name, not an arbitrary client-selected destination.
Use stock Envoy listener matching, TLS inspector, TCP proxy and DNS clusters;
avoid the alpha SNI dynamic-forward-proxy extension. SNI is connection metadata,
not TLS authentication. No TLS termination or interception.

Missing/unreadable SNI requires an explicit IP rule or is denied. Immediate TLS
(HTTPS/IMAPS) is supported; STARTTLS/FTP are not automatically supported by a
hostname rule. UDP/QUIC is denied except designated DNS. Wildcards and DNS
exfiltration policy are deferred.

## Configuration, events and approvals

Manager stores policy and generates file-based Envoy dynamic configuration.
Validate before replacement and confirm the intended version is active before
reporting an approval effective. Filesystem xDS has no RPC ACK/NACK; use the
local administrative observation, counters and errors. Add upstream clusters
before listeners reference them. Invalid updates keep the last accepted policy;
the UI distinguishes desired, pending, active and failed changes.

Log time, stable user/agent/container identity, host/IP, port, allow/deny and
available bytes/duration. Include prompt deny events for unmatched connections,
not just TCP-proxy completion logs. Group approvals by stable scope/container,
destination and port. Allow updates policy; clients retry. Do not hold sockets
or automatically replay tool calls. Linux-rejected attempts need only aggregate
counters, not individual UI records.

Initial approvals are Allow and Keep blocked. Temporary approvals are deferred
until their expiry contract is explicit; file updates alone do not guarantee
expiry while Manager is down. Do not add a separate expiry/control-plane service.
Rule removal affects new connections; immediate existing-flow termination is
outside v1. Manager outage leaves the last accepted policy in place.

Keep Envoy admin local and management-only. The explicit host HTTP/CONNECT proxy
endpoint is restricted to the trusted host services, not merely assumed safe
because it binds loopback. Manager/UpdateService and Podman acquisition use that
same intermediary with artifact-only destinations. Do not claim that proxy
configuration alone constrains arbitrary compromised host processes.

The package service keeps its existing cache role and separate upstream allow
list. Expose only its named package endpoint to scopes through Envoy; no general
core route. Package-name admission policy is deferred.

## Container releases and UpdateService

UpdateService remains responsible for discovery, release verification, staging,
activation, health assessment and recovery. Manager remains the single container
lifecycle executor, including images, networks and service containers. Updates
must coordinate with Manager reconciliation rather than run competing Podman
controllers. Preserve existing maintenance, active-execution and recovery rules.

Signed release metadata pins service images by digest and identifies the host
Manager/UpdateService artifacts. Verify/stage before switching services; prohibit
implicit pulls during activation. Startup order is egress/DNS, required trusted
dependencies, Gateway, then remaining services; honor actual health dependencies.
Retain the previous verified release until new-release health is accepted.

Keep data outside disposable containers. Preserve existing schema/migration,
backup and recovery safeguards. An older image is not a safe data rollback after
an incompatible migration. Migrate supported host-service installations without
discarding state, certificates, configuration or update-channel policy.

Initial installation supplies cached egress images, avoiding a download-the-proxy
through-itself dependency. Updating/recovering egress must preserve the same
bootstrap/repair path. Containerising services also requires their actual socket
mounts, permissions, persistent volumes, telemetry and frontend assets to work.
Do not equate an existing Dockerfile with a working deployment.

## Work order and acceptance

1. One small rootless proof: two scope networks, egress/stock DNS, allowed TLS,
   denied name/IP/external DNS/IPv6, observed source identity and no bypass.
   Attach the second scope while egress runs; check both sets of routes/flows,
   detach it, and verify isolation plus fail-closed behavior when Envoy stops.
2. Integrate Manager network lifecycle, agent connector scope and file-based
   policy. Verify normal Gateway -> Manager execution still works.
3. Containerise core services and adapt UpdateService and release packaging.
   Verify a real staged update, failed-health recovery and persistent data.
4. Connect logs/approvals to the existing UI; verify live Allow and failure
   feedback. Exercise native Linux, WSL and Mac appliance support before claiming
   each platform is supported.

Reuse existing Podman, updater and UI owners. No NFQUEUE, durable first-packet
protocol, custom conntrack grants, old stop gates or new update framework.

## References and review

- [Podman networks and static route scope](https://docs.podman.io/en/v5.8.0/markdown/podman-network-create.1.html)
- [Podman network attachment and DNS aliases](https://docs.podman.io/en/latest/markdown/podman-network-connect.1.html)
- [Envoy original destination](https://www.envoyproxy.io/docs/envoy/latest/configuration/listeners/listener_filters/original_dst_filter)
- [Envoy filesystem xDS and rejection behavior](https://www.envoyproxy.io/docs/envoy/latest/api-docs/xds_protocol.html)

The GPT-6 Astra medium architect reviewed revision 6 and the supplied feedback.
Consensus: adopt this bounded design with the explicit ownership, DNS, route,
IPv4, no-fallback and configuration-activation corrections above. Temporary
approvals are deferred rather than presenting scheduled expiry as guaranteed.
At adoption, implementation and environment evidence were outstanding; the
foundation evidence below records subsequent progress.

## Fedora 44 WSL foundation evidence

The [single topology proof](../../scripts/container-egress/README.md) now exercises
the native design with real rootless containers. The initial successful promoted
run is retained locally as `artifacts/container-egress/promoted-proof-4632c7a4.json`.
The final runner, including serialized allocation, mapping read-back, explicit
ambient-capability assertions and restricted configuration mounts, also passes
as `artifacts/container-egress/final-proof-5f20e6ca.json`.
Environment: Fedora 44 WSL, Podman 5.8.9, Netavark 1.17.2, aardvark 1.17.1,
nftables 1.1.6 and WSL kernel 6.18.58.1. Envoy distroless 1.40.2 and CoreDNS
1.14.6 are supplied as cached OCI archives. No installed application is changed.

Observed results include allowed TLS from both scopes after live attachment;
unchanged external default routes after attachment/detachment; continued first
scope traffic after retirement of the second; correctly combined source,
original-destination and SNI log fields; and no new controlled-upstream SYNs for
denied name, literal IP, unknown source or an Envoy-stopped request. A reachable
same-scope target provides the cross-scope denial control. Alternate DNS answers
from egress but receives no private-network query. Client IPv6 addresses/routes
are absent rather than assumed filtered. Cleanup errors are empty.

Actual process observations show NNP enabled and no ambient capabilities for both
stock services: Envoy effective/permitted/bounding capabilities are zero; CoreDNS
has only `NET_BIND_SERVICE`. Its executable's file capability requires that
narrow exception on this runtime even with NNP. The fixture's key/certificate
belong to the simulated TLS endpoints, not to the Envoy configuration.

The architect reviewed the proof and the explicit limits. This is WSL static
topology evidence, not a completed product, recursive DNS proof, file-xDS update
proof, host-process containment test or native Linux/Mac validation. Continue
with Manager integration rather than creating additional research subsystems.
