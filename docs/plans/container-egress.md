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

## Agent connector identity integration

Agent-initiated `Isolated` calls now carry workspace identity without receiving
workspace files or environment. Owned agent contexts publish their canonical
owner; a foreign caller is rejected. Existing runtime keys distinguish agents,
while non-agent setup/system operations keep explicit management instances.

Manager reconciles actual existing connector containers against current accepted
connector/workspace identities. It does not eagerly start their Cartesian product.
Recovery and persistent creation use the same container-name lease, including
idle-timer registration. Orphan cleanup rechecks current desired state after
inventory and retires the exact observed container ID, not a replacement using
its name. Gateway remains the binding authority; unbinding can leave an idle
cached instance until normal cleanup. No new binding protocol is introduced.

This checkpoint changes identity and reconciliation, not network enforcement:
the application still uses its existing network until the Manager egress mode
and package endpoint are integrated. Scoped network creation, service
containerisation, container update/recovery and UI remain outstanding.

Validation for this identity checkpoint: 122 focused integration tests pass,
including owner publication/rejection, separate agent names, no isolated file or
environment access, existing-instance recovery, current-state orphan decisions,
cooldown/active-use ordering and immutable-ID retirement of a replaced one-shot.
The actual filtered full-ID/name Podman inventory command also passes in Fedora
44 WSL as part of the existing topology proof; the local report is
`artifacts/container-egress/product-inventory-12f668db.json`, with empty cleanup
errors. The architect reviewed and approved the final identity changes.


## Prepared-container lifecycle checkpoint

Manager's runtime-container layer now contains a small preparation primitive:
create without implicit pulls, initialize, inspect Podman's assigned IPv4
address and namespace, configure and read back the client default route through
`ns:<SandboxKey>`, and return the immutable container ID. It does not start the
application or publish policy. The networking caller must confirm the source's
Envoy policy is active before starting that ID. The connector create builder
preserves existing resource, capability and optional read-only file boundaries;
one-shot stdin is enabled at creation. Existing launch paths remain unchanged
until the egress lifecycle and package endpoint are integrated.

Each preparation and its transient helpers share a unique native operation
label. Failed or interrupted preparation recovers exact IDs by label, confirms
ownership, retires helpers first and checks removal independently of caller
cancellation. Auto-removal races are treated as success only when exact-ID
absence is confirmed. Genuine cleanup errors are retained and reported.

The existing Fedora runner now reproduces native staged startup. Its successful
local report, `artifacts/container-egress/prepared-proof-ff86effd.json`, records
no application execution before explicit start, native address `.1`, unchanged
namespace/address/default route, attached stdin/stdout/stderr and exit status 7,
with empty cleanup errors. Using `container:<ID>` for a helper was rejected:
Podman starts the initialized target as a dependency. This is platform evidence
for the sequence, not an installed application or policy-activation test.

Validation: 43 focused integration tests pass, covering preparation and identity,
route stability, cancellation/timeouts during create and both helper operations,
helper auto-removal races, ambiguous cleanup, and existing connector boundaries.
The Astra architect approved this checkpoint after reviewing the final cleanup
changes and the native report. Network enforcement is not enabled by this change.


## Stock Envoy configuration checkpoint

The runtime-container layer now compiles exact source-address/TCP rules into
stock CDS/LDS file resources. A native matcher selects source first, then
original destination port, visible SNI and a source-specific literal-IP fallback.
Each path selects a named upstream chain or the named denying chain. A hostname
rule always uses the configured DNS cluster for that hostname. Legacy
`FilterChainMatch` pruning is unsuitable for mixed rules: destination-IP matching
can discard another source's hostname chain before source matching occurs.

Configuration versions are deterministic hashes of the generated configuration.
Admission observes the accepted CDS/LDS response version, actual active resource
markers and absence of warming replacements. It does not trust a written file or
only `active_state.version_info`: that per-listener field remained stale during
a real Envoy in-place update. Clusters must be active before a referencing
listener is submitted. Listener/proxy log phases and selected upstream cluster
are emitted for later collection and classification.

Stock TLS inspection continues after a two-second timeout. Without readable SNI,
only that source's explicit literal-IP permission can proceed; everything else
reaches deny. Server-first protocols therefore have an inspection delay, rather
than immediate forwarding. Hostname rules still require visible SNI.

The existing topology runner consumes the actual C# test-exported files. The
final local report, `artifacts/container-egress/final-compiled-7586c484.json`,
passes allowed/denied TLS, overlapping IP/hostname rules from different sources,
server-first TCP and its denied-source control, unknown-source denial, staged
startup, attach/detach, cross-scope and DNS isolation, and Envoy-stopped failure.
It also observes accepted updates, rejected-update preservation and independent
cluster readiness. Cleanup errors are empty. The C# observer accepts the actual
accepted and preserved dumps and distinguishes the mixed-policy generation.
All 59 focused tests pass; the Astra architect reviewed this bounded checkpoint.

The compiler and observer are not yet wired to application launch or approval
publication. Manager topology lifecycle, package endpoint integration, service
containerisation, container release/update recovery and UI remain required work.


## Manager connector integration checkpoint

`ContainerEgressRuntime` now owns native topology and serialized policy publication
inside Manager's existing lifecycle. It prepares the egress pod from verified,
locally cached inventory entries, keeps its sole outside default route, installs
native drop/redirection rules before private attachment, and disables ordinary
IPv4 forwarding. No application receives an outside network or runtime socket.
Private network names are deterministic hashes of user, scope kind and agent;
management probes have a distinct scope kind.

The connector service uses create/init, verifies the assigned native tuple,
confirms the complete Envoy policy, then starts that immutable container ID.
A newly observed address allocation replaces its previous owner before startup.
Automatic restart is disabled. One-shot calls hold the existing keyed operation
lease through invocation and cleanup; persistent calls use exec against the
admitted ID. Isolated identity does not imply a workspace mount.

Retirement removes source permission before allocation release. If policy
confirmation fails, Manager invalidates readiness and confirms Envoy stopped
before stopping the source. Recovery reconstructs native ownership, prepares
policy files before reviving a stopped proxy, and never starts the whole pod
with stale files. Healthy adoption checks image, pod, command, capabilities,
readonly configuration mount and disabled restart. DNS configuration drift
rejects explicitly until controlled DNS replacement is integrated.

The proxy cannot initiate connections to managed private CIDRs or its own
loopback services. Reply-direction traffic remains possible. Loopback DNS is
allowed at port 53; only a separate numeric setup-helper identity can reach the
loopback admin port. This protects against approved names resolving to internal
addresses. The future designated package endpoint requires an explicit narrow
exception and forwarder, not an ordinary external rule.

Native Envoy observations require two publication details. An empty cluster set
has no response version in the admin dump, so its confirmation requires an actual
cluster dump with zero active and warming clusters, alongside the independently
confirmed listener. Nonempty sets retain the exact version checks. Envoy also
ignores resource-identical version-only CDS updates; staging/final publication
therefore gives actual cluster resources a separate publication metadata marker.
The stable destination marker and authorization configuration are preserved.

This remains an opt-in integration checkpoint. It accepts preloaded,
self-contained connector images. Bundle/language materialization, AgentSandbox,
package/telemetry endpoints, host artifact acquisition, full service deployment,
container update recovery, policy persistence and audit/approval UI still need
integration. Unsupported execution paths reject explicitly; there is no legacy
network fallback. The measured private-network cap is currently core plus one
active scope, with empty scopes retired to recover capacity.


The tracked Fedora WSL run with the actual Linux Native AOT host passed in
`artifacts/container-egress/runtime-proof-715a031e.json`, with no cleanup errors.
It completed real tool discovery and invocation, overlapping one-shot calls,
healthy adoption without an Envoy PID change, forced same-address replacement
without permission inheritance, proxy-stop-before-source-stop recovery, different
agent grants and empty-scope retirement. The proxy-UID loopback/admin and private
resolver probes were denied. Runtime, execution-service and shared-fixture source
hashes match the Linux AOT snapshot. This uses fixture DNS and offline images;
it does not demonstrate registry acquisition or an installed application.

Enabled supervisor shutdown skips the legacy global parallel managed-container
removal. The connector service remains responsible for policy-aware retirement;
egress topology survives for adoption. The host dependency graph constructs the
runtime before its dependent connector service, so normal reverse-order disposal
keeps the runtime available through connector cleanup. Both enabled preservation
and unchanged legacy shutdown are covered by focused tests.


## Package service and sandbox shell integration

The existing sidecar owner starts the released package-service image on core,
using its verified local inventory entry (`git-pkgs-proxy`). It retains the cache
mount and CLI diagnostics, publishes no host ports, and applies the existing
`/health` probe as an explicit Podman health check. No wrapper image build or
implicit pull is needed for this path. Adoption checks image, mounts, environment,
numeric user, readonly root, capabilities, NNP, command, restart policy and the
installed health-check configuration.

A topology-owned package mapping reuses the transparent listener: an eligible
source connecting to its scope's egress address at port 20101 selects the fixed
core backend. This branch precedes ordinary SNI/literal rules. Manager supplies a
hosts entry for `kautuka-pkgs-proxy`; the existing package URLs remain unchanged.
The mapping is not a user destination grant and never connects back to itself.

The package cluster binds upstream connections to the core egress address.
Ordinary external clusters bind to the inspected outside address. The native
private OUTPUT exception requires the Envoy UID, core source address and exact
package-backend address/port. An external hostname resolving to that backend
therefore cannot reuse the exception. These use [stock Envoy per-cluster source bindings](https://www.envoyproxy.io/docs/envoy/latest/api-v3/config/cluster/v3/cluster.proto.html)
and native nftables matches, with no additional listener or forwarding service. The package service's initial separate upstream list is exact HTTPS
access to `registry.npmjs.org`, `pypi.org` and `files.pythonhosted.org`.

Agent shell startup now uses prepared containers on the accepted user-agent
network, with exact-ID exec and policy-aware retirement. The shipped sandbox
runs as UID/GID 1000 with Podman's `keep-id` mapping to the runtime host owner.
This preserves workspace permissions and ownership without recursive chown or
world-write permissions. Adoption rejects the prior user/mapping/restart setup.
Agent and sidecar orphan cleanup uses the runtime's policy ordering and reads
current desired names before retiring each immutable ID.

The existing readiness path detects absent/stopped Envoy or DNS containers,
invalidates cached readiness, confirms the proxy stopped, then reconstructs and
recovers the native state. This is a running-state check, not a health proof for
a running but unresponsive service. Source restoration reapplies the complete
boundary after inventory loading, independently of source order. Ordinary source
grants change Envoy configuration; nftables is rewritten for topology or package
backend changes only.

The native report `artifacts/container-egress/runtime-proof-c15f5c3e.json` passed
with the actual pinned package and sandbox images and no cleanup errors. It
records a real npm metadata fetch from a sandbox, direct-core denial, an ordinary
external grant's DNS-rebinding denial with an observed kernel reject counter,
unchanged package/proxy adoption, stopped-proxy recovery and the earlier connector
lifecycle checks. The host observed the workspace file owned by the runtime
account. Images were cached separately for the disposable test; this remains no
proof of application-managed image acquisition. The final run covers the boundary-update optimization. All changed runtime
sources and the shared fixture match the Linux AOT snapshot by normalized-text
SHA-256. Two focused regression groups pass: 34 passed/one Linux-only skip, and
58 passed.

Bundle/language image materialization and MCP connector installation inside an
AgentSandbox remain explicitly blocked in enabled mode while their install
containers are migrated. Direct sandbox shell execution and the package route
are implemented; this does not claim every sandbox provisioning path is finished.
Core application containers, host artifact transport, update transactions,
policy persistence, logs/approval UI and broader platform evidence remain in the
overall goal.
