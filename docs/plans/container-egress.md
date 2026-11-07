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

Bundle/language image materialization now uses the controlled runtime. Trusted
filesystem setup builds run with `--pull=never --network=none`; only that offline
setup receives CHOWN and DAC_OVERRIDE to traverse the shipped private home and
prepare the install directory. Binary metadata/chmod uses Python isolated mode,
so a copied bundle cannot shadow its standard-library imports. Dependency
lifecycle scripts run separately in prepared UID-1000 containers with no
capabilities, on the owner's user-agent network and the designated package route.
Their package-index inputs must match the fixed package-service endpoints.

Waiting for an installer and committing its filesystem happen outside the short
network-publication gate. An exited installer remains owned by its live request
until commit and retirement; a fresh Manager excludes abandoned installers from
recovered policy before removing them. Image removal is non-forced and follows
container retirement. Cache identity includes the controlled recipe, bundle,
base and install root. No networked Buildah step or implicit pull is used.

Python bundle execution uses the installed virtual environment, including known
shipped absolute interpreter paths. AgentSandbox artifacts retain `/artifact`
and `/contract.json`; copy-only staging containers have no network, never start,
and are copied and retired by immutable ID. Shared-agent binding payloads are
filtered by connector owner before provisioning, and explicit foreign connector
requests are rejected before any build. Sandbox processes retain their sandbox's
shared network identity.
Core application containers, host artifact transport, update transactions,
policy persistence, logs/approval UI and broader platform evidence remain in the
overall goal.


The next service-container checkpoint uses the existing Unix-socket RPC and mTLS
contracts: CredentialsHost on `--network=none`, then Gateway on the admitted core
network. CredentialsHost retains its private vault; Gateway receives only the
intended client transport material and relevant socket directories. Read-only
mounts do not by themselves hide CA/server private keys from a shared mapped UID.
Certificate renewal is local and must retain replacement visibility. Disable
CredentialsHost's network telemetry export while it has no network. ChannelHost
and the actual WebHost binary/assets follow; WebHost alone publishes a browser
port. Existing AOT Dockerfiles for Gateway, CredentialsHost and ChannelHost are
build inputs, not evidence of installed container deployment. UpdateService's
host-unit checks and restarts must move to Manager-owned application activation
and health before the installed deployment changes; UpdateService remains the
release transaction owner, with no second Podman controller.


The final materialization report
`artifacts/container-egress/runtime-proof-7d53f7a6.json` passed on Fedora 44 WSL
with Podman 5.8.9 and the shipped package/base images, with no cleanup errors.
Node (`is-number`), Python and uv (`requests`) executed their installed dependencies
in both isolated and AgentSandbox modes. The binary fixture exercised executable
setup and launch with a hostile shadow module present; it is a script fixture,
not an ELF ABI compatibility test. The report also confirms stable sandbox IDs
across shell calls, exited-installer recovery before commit, fresh-Manager removal
of an abandoned installer, recovered empty scopes and prior egress regressions.
Changed runtime sources, the shared fixture and smoke entry point match the Linux
AOT snapshot by normalized-text SHA-256. The focused Windows group passed 48 tests
with one Linux-only skip; actual Linux behavior was exercised by the native run.
The native test wrapper now preserves subprocess timeout and environment inputs;
earlier wrapper/fingerprint failures are retained as diagnostic runs, not passing
enforcement evidence. These results do not establish application-managed image
acquisition or an installed application release/update transaction.


Gateway's CredentialsHost, Manager and UpdateService clients now load derived
`clients/gateway` directories. Each issuer publishes only its public CA and the
explicit Gateway client PFX. Manager's separate UpdateService identity remains
canonical and is not included in Gateway's publication. Both derived directory
levels receive group traversal/setgid explicitly, including under a restrictive
service umask. File permissions are set before atomic rename; unchanged contents
retain their timestamps, and the directory inode is retained for bind mounts.
CredentialsHost renewal publishes before activating its new accepted identity.
A publication failure keeps the old in-memory identity active and logs the failed
renewal. Canonical stores and recovery inventories are unchanged; publication is
regenerated during bootstrap after recovery. Gateway does not fall back to private
canonical material. Only the CredentialsHost Gateway client currently reloads a
renewed PFX live; Manager/UpdateService client loading keeps its existing restart
semantics. This publication seam alone does not launch application containers.


The publication checkpoint's native report
`artifacts/container-egress/runtime-proof-0fa89334.json` passed with clean teardown.
The actual AOT publication helper repaired a deliberately private intermediate
directory; a different container UID with the intended group traversed the
published hierarchy while the CA key remained unreadable. A running networkless,
capability-free container with only the published directory mounted read-only
saw the two intended files and observed a renewed PFX after atomic replacement,
without remounting. Prior egress and dependency-execution checks also passed.
The shared publication source, native fixture and smoke project match the Linux
AOT snapshot by normalized-text SHA-256. Focused Windows checks passed 29 tests,
including Gateway's live credential reload and failed publication retaining the
old accepted identity. Application-service container launch remains the next
integration step; this is not an installed container-release or recovery proof.


Core service startup is now attached to the existing Manager worker's
pre-desired-state phase. The initial opt-in `CoreServices` integration starts
CredentialsHost and Gateway from verified local inventory entries
`core-credentialshost` and `core-gateway`; it performs no pulls. CredentialsHost
has no network. Gateway uses an admitted core allocation and normal policy-aware
retirement. Both use UID/GID 1000 with keep-id, no capabilities, NNP, a readonly
application root and private temporary storage. Adoption checks actual image,
command, mounts, mapping, resources, environment and network state before reusing
an exact container ID. Boot readiness requires a running process and an actual
Unix-socket connection; it is separate from workspace execution readiness.

Gateway's application state lives in a dedicated `gateway-data` directory. It
receives a separate workspace-root RW mount at the same absolute host path,
preserving existing attachment, skill-staging and file-browser access. It does
not receive the entire shared state root. Private service state, Podman storage,
run directories and control/configuration paths cannot serve as that workspace
mount. The initializer rejects an unmigrated legacy root `kautuka.db`; installer
relocation/ownership changes remain required before enabling this on an existing
host installation. The same host writer-lock inode is mounted read-only at its
existing fixed path, and the maintenance directory is mounted read-only so
atomic marker changes remain visible. Client socket and published-identity
mounts are narrow directory mounts. OTLP endpoints are explicitly empty in this
initial pair; observability service integration is still pending.

The first actual-container attempt recorded a Gateway `GLIBC_2.43` failure in
`artifacts/container-egress/runtime-proof-0c3b34b9.json`, with clean teardown:
a binary compiled on Fedora 44 cannot be assumed to run on the older Azure Linux
runtime. The three existing application Dockerfiles now pair the Azure Linux 3
AOT SDK with the Azure Linux 3 distroless runtime, both digest-pinned, and use the
existing release artifact trimming properties. The AOT SDK supplies its own
native toolchain, replacing the Debian-specific compiler installation. This
follows [Microsoft's Linux Native AOT compatibility guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
and the [official SDK image variants](https://github.com/dotnet/dotnet-docker/blob/main/README.sdk.md).
The build context excludes local development settings, private key files and
databases. An isolated offline SDK development build is not a rootful
application-runtime fallback; acceptance still uses the rootless service
fixture and the actual published native dependencies.


The core namespace diagnostic in
`artifacts/container-egress/runtime-proof-5c5d9247.json` established Podman 5.8.9's
networkless representation: `NetworkMode=none`, one unassigned `none` metadata
entry, and only `lo` in the live namespace. Adoption now accepts only that empty
placeholder (or no attachments), rejecting assigned endpoint/address/prefix data
and additional networks. The fixture selects service identities by their labels
and compares service-to-ID mappings, without depending on multi-inspect order.

The core-pair checkpoint's native report
`artifacts/container-egress/runtime-proof-c48aec52.json` passed with no cleanup
errors. Both actual Azure Linux-built Native AOT services started under rootless
Podman. CredentialsHost had only its loopback interface and answered an
mTLS-authenticated health RPC over its Unix socket. Both service containers
retained their exact IDs during reconciliation. The host exclusive-writer probe
was blocked while the services ran and succeeded after Manager stopped them.
The same run passed previous egress, package-service, certificate publication,
source reuse, recovery and Node/Python/uv/binary dependency execution checks.
All 64 runtime, shared and fixture source inputs match the native snapshot by
normalized-text SHA-256. The focused Windows group passed 33 tests with one
Linux-only skip. The architect found no remaining source blocker in this pair.
This does not establish a complete installed deployment: ChannelHost, WebHost,
observability, installer/data relocation, host artifact transport and container
release/update integration remain unfinished.

The next core integration extends the same supervisor to CredentialsHost,
ChannelHost, Gateway and WebHost, with reverse-order retirement. ChannelHost
receives its own state and the existing disjoint production channel inventories;
Gateway receives the ChannelHost socket and its own channel client identities.
The offline channel authority is not mounted into either service.

WebHost receives no Gateway database, workspace, client PFX, writer lock or
writable service-state mount. Its existing Unix-socket Gateway hop is preserved.
Manager publishes exactly one IPv4 TCP port to container port 20229. HTTP is
permitted only for the explicit managed-loopback deployment with host publication
127.0.0.1; the container itself listens on the private core interface. Other host
bindings require an edge.pfx in the configured certificate directory, mounted
read-only. The optional certificate password remains in webhost.json. Readiness
requires the actual /health response, using an exact installed-certificate pin
and validity check for HTTPS. It is a local readiness check, not browser PKI or
revocation validation. Certificate content participates in the service fingerprint;
replacement recreates WebHost on reconciliation rather than reloading it live.
The image recipe requires the same-release web-assets image for its frontend.
Native service-fixture payloads alone do not establish the packaged frontend.

Update integration remains pending: the existing updater checks host service
units, and its Manager client currently has status-only authorization. The
container release path must add narrowly authorized UpdateService lifecycle
operations, replace those unit checks with deployment health, and preserve the
existing maintenance journal and exclusive database-writer fence. A maintenance
marker suppresses new reconciliation; it does not itself stop running writers.

`artifacts/container-egress/runtime-proof-88bca744.json` passed with no cleanup
errors on the preceding four-service build. It verified networkless CredentialsHost
mTLS health, shared writer-lock fencing, unchanged-ID adoption, published WebHost
HTTP and HTTPS, Gateway setup-status forwarding over the Unix socket, and
replacement of only WebHost when enabling TLS. All prior workload, package,
source-reuse and dependency-execution regressions also passed. That run preceded
the local pinned-health correction removing the online revocation request; the
corrected build is being verified separately with the explicitly scoped
`--core-only` test mode. The focused Windows group passed 39 tests.

The corrected core-only native report
`artifacts/container-egress/runtime-proof-75cdf94a.json` passed with clean teardown.
It explicitly reports `core-services-only`: the four real application services,
HTTP/TLS publication, Gateway forwarding, same-ID adoption, WebHost-only TLS
replacement, networkless credentials and writer-lock checks all passed. It does
not repeat the workload regressions covered by the preceding full report.
All 65 runtime/shared/fixture inputs match the final native build snapshot by
normalized-text SHA-256. The architect's final source review found no remaining
blocker. Container-release acquisition, installer activation, full frontend image
packaging, updater lifecycle operations and installed-system recovery remain open.

Host artifact transport is now being integrated through a second stock Envoy
listener on a pathname Unix socket. Its exact hostname:443 CONNECT routes use
fixed outside-bound clusters; it does not implement dynamic forwarding or TLS
interception. Both listener markers participate in the existing CDS/LDS
publication transaction. The socket directory is owned by the rootless runtime
mapping, mode 2770; only Envoy mounts it read-write and receives mapped group 0.
The socket is mode 0660. Application/workload containers do not receive this mount.
This follows the [Envoy 1.40.2 CONNECT termination example](https://raw.githubusercontent.com/envoyproxy/envoy/v1.40.2/configs/terminate_http1_connect.yaml).

The host .NET handler uses an explicit fixed proxy and a Unix-only connection
callback, with no loopback bypass, environment proxy selection or TCP fallback.
The updater selects it when ContainerEgress:Enabled is true or an explicit
ArtifactSocketPath is configured. Unconfigured legacy host-service deployment
retains its current transport during this staged migration. The final appliance
installer must select managed egress for both host processes and provision the
initial authority policy and cached images before downloads depend on Envoy.
An explicitly selected but unavailable socket fails closed. Signed manifests,
digests and release verification remain responsible for artifact authenticity.
This shared host socket authorizes the trusted runtime owner; it does not identify
individual host processes. Installed UpdateService group/traversal access and
application-managed OCI acquisition are still pending integration and proof.

`artifacts/container-egress/runtime-proof-fa640b24.json` passed with no cleanup
errors. This is explicitly a host-artifact-only native proof. The actual typed
client completed end-to-end TLS to the controlled fixture server, followed an
allowed redirect, and received explicit proxy 403 denials for the forbidden
redirect authority and port; plain HTTP was also denied. It verified socket
mode 0660 and mapped UID/GID 65532:0, failed closed during an Envoy outage, then
succeeded with the same client after socket recreation. The earlier diagnostic
run stopped in the fixture's certificate-only PEM loader and is not passing TLS
evidence. All 67 runtime/shared/fixture source inputs match the AOT build snapshot
by normalized-text SHA-256. Focused Windows checks passed 23 transport/compiler
tests and 70 updater/source-policy tests. The architect cleared the corrections.
A full native regression with the host listener enabled is running separately;
this focused result does not claim core-service or workload regression coverage.

The combined native rerun
`artifacts/container-egress/runtime-proof-a2cff49a.json` passed with no cleanup
errors. It exercised the host-artifact listener alongside all four core services,
source policy publication/recovery, the package service, sandbox execution and
Node/Python/uv/binary dependency execution. All 67 runtime/shared/fixture inputs
match this diagnostic AOT snapshot by normalized-text SHA-256. The preceding
combined run `runtime-proof-9daa819a.json` timed out during sandbox preparation;
that failure cleaned up and did not reproduce in the rerun. Its exact cause is
not established. The 30-second preparation budget is unchanged. Preparation
errors now identify the native operation, and the fixture records slow/failed
operation timing without printing argument lists or environment values.

Release archive resolution now retains the signed repository reference for
authorization and uses the inspected immutable config ID for bootstrap helpers
and core-service creation/adoption. A bound entry requires archive SHA-256,
Linux platform and config ID together. Missing cached images are loaded only
after archive verification; mismatched identities/platforms fail closed without
a pull. The legacy archive-loading entry point delegates to this same verifier
when binding metadata is present. Podman's native bare-hex config ID is
normalized to `sha256:` form without relaxing signed input validation.

The release producer binds staged Docker/OCI archives without extracting them,
then includes these fields in the detached signed manifest. The WSL version root
and signer receive the same generated inventory. Both current release targets
are linux/amd64; another platform requires its own inventory. Association with
the repository reference relies on the trusted producer's pinned pull/save
sequence, not on a claim that a converted archive proves registry provenance.
This does not yet complete Linux archive distribution, bootstrap/core image
publishing, application-managed registry acquisition or installer activation.

Focused native report `artifacts/container-egress/runtime-proof-dc1efc0b.json`
passed with clean teardown. It covers a genuinely absent tiny OCI image loaded
from a synthetic trusted inventory, config-ID resolution without repository
metadata, tamper rejection before a second load, host artifact transport and
the four core services. The other images in that fixture use cache-hit bindings;
this is not cold-install or real release-signature provenance evidence. The
final combined runtime regression is being rebuilt after the legacy-entry-point
correction. Producer checks pass for OCI/Docker wrappers, wrong platform,
changed config blobs, ambiguous multi-image archives, traversal, signed binding
preservation/tamper rejection and mocked native archive staging.

The final archive checkpoint's combined native report
`artifacts/container-egress/runtime-proof-bb4da811.json` passed with clean teardown:
cold archive identity/tamper checks, host transport, four core services, source
recovery, package service and Node/Python/uv/binary isolated/sandbox execution.
The preceding `runtime-proof-13185720.json` passed the archive, host and core
checks but timed out at the first sandbox's native `create` operation (30 seconds).
The passing rerun used the same deadline. Process observation showed the create
parent waiting for a child amid approximately 43% global WSL I/O pressure;
this does not establish the cause. The intermittent creation delay remains open.
Checkpoint `a4f7832` passed normal push checks: 2,486 backend tests, five skips,
540 frontend tests, zero build warnings/errors and 30 existing lint warnings.

Managed registry acquisition now has a bounded native implementation alongside
the existing runtime, using the same ephemeral materializer lifecycle in the
private core network. Its fixed system identity is `image-acquisition`; the
exact registry endpoint must have an explicit grant before a helper starts.
Skopeo itself must already be a verified cached release asset. Two fixed helper
commands retrieve the pinned raw manifest, then copy the selected platform with
`--preserve-digests`. Manager verifies the original index membership, preserved
manifest, config digest and platform before native loading and immutable-ID
inspection. Converted manifests are rejected. No engine socket, container-store
mount, implicit pull or TLS interception is involved. Source and archive files
remain in a private cache for offline restoration. Ambiguous helper retirement
preserves staging rather than deleting a potentially mounted directory.

This uses [stock Skopeo inspection](https://raw.githubusercontent.com/containers/skopeo/main/docs/skopeo-inspect.1.md)
and [preserved-digest copying](https://raw.githubusercontent.com/containers/skopeo/main/docs/skopeo-copy.1.md).
The native proof uses an explicitly pinned public Quay image; initial helper
acquisition into the development fixture cache was manual setup, not evidence of
application-managed bootstrap. Consumer wiring, release helper publication,
private-registry credentials, cache/stale-staging capacity management and installed
deployment remain unfinished. Cold network acquisition belongs in background
preparation; tool calls must use ready images within their existing deadline.

Focused native report `artifacts/container-egress/runtime-proof-c4ce72c9.json`
passed with clean teardown. It verifies real registry acquisition, source/config
binding, cache restoration without another helper, pre-helper denial of an
unapproved registry, and observed proxy events correlated with both helpers'
recorded immutable IDs and source addresses. All 70 runtime/shared/fixture inputs
match the Native AOT snapshot. The focused Windows group passed 39 tests.
This report does not repeat the core/workload coverage of `bb4da811`.

The stricter audit test exposed absent stdout access events in the managed
Envoy/journald path. `0df10e54` proved acquisition functionality only; `8aef936a`
and `a22c7143` correctly failed the added audit assertion. Logger counters reported
successful writes and zero buffered bytes, but that did not prove their delivery.
The tested configuration now uses stock `StderrAccessLog`, TCP connected events
and HTTP request-start events while retaining completion events and recording
`event_type`. The final syscall trace confirms JSON writes to a duplicate of the
stderr pipe; those records appear in `podman logs` and satisfy source attribution.
The missing-stdout root cause is not established. A host request-start record
means receipt, not admission; actual TLS success and explicit proxy-403 tests
establish outcomes. Persistent audit delivery/storage and UI are still pending.


Runtime image consumers now preserve the authorized source reference while using
a typed, inspected config ID for native creation and adoption. Background
reconciliation prepares released sandbox images, accepted raw connector images
and package services. Foreground execution and inspection are cache-only: they
never start acquisition or archive loading, and a ready image does not wait
behind an unrelated registry transfer. Desired-state failure/materialization
transitions compare the expected definition under the store lock.

Acquisition helpers put Skopeo temporary layers in their private output directory
and disable implicit image volumes/read-only tmpfs mounts. Normal operation
failures remove staging only after confirmed helper retirement; uncertain
retirement preserves potentially mounted output.

Combined Native AOT report artifacts/container-egress/runtime-proof-08c22369.json
passed with no cleanup errors. All 70 runtime/shared/fixture source inputs match
the compiled snapshot by normalized-text SHA-256. This run combines all four core
services, host artifact transport, real pinned registry acquisition, offline cache
restoration, failed-registry-lookup cleanup, source recovery, package service and
Node/Python/uv/binary isolated and sandbox execution. The focused Windows group
passed 106 tests, and the Manager build had zero warnings/errors.

The earlier disk-backed run runtime-proof-125b53b0.json failed on actual disk
exhaustion; its exact disposable owner/store were subsequently recovered and
removed. The passing combined run uses a bounded 6 GiB RAM-backed disposable
store, so it proves neither disk capacity/throughput nor persistence. The first
RAM-backed run runtime-proof-e55477c1.json exposed a fixture-only non-forced
image-removal issue when the acquired image was also the cached helper; the
fixture now removes only inspected tags before testing offline restoration.
Installed deployment, updater integration, release publication, persistent audit
and approval UI remain unfinished.

The existing update snapshot engine now writes format 3. Its explicit inventory
includes Gateway databases, machine/auth keys and validated per-agent databases
under both the legacy data root and `gateway-data`. It does not copy workspaces,
arbitrary databases, container storage or whole service directories. Absent fixed
records authorize removal of supported target-only files during recovery; user
file sentinels and unrelated files survive. Existing dirfd ancestor locking and
directory ownership/mode restoration also cover the new nested location.

Format-2 snapshots retain their original verification and deletion scope: they
cannot admit or delete uncaptured `gateway-data` state. The installer must take
a format-3 snapshot before beginning the host-to-container layout migration;
capture/restore themselves do not migrate files. The architect cleared this
checkpoint. Recovery tests passed 28/28 on Fedora 44 WSL as root and 21 on Windows
with seven Linux-specific skips. This is snapshot-engine evidence, not an
installed container-update transaction or interruption/recovery proof.

The existing disposable apply-release transaction regression also passed using
the format-3 engine: snapshot verification, maintenance fencing, injected
precommit interruption/recovery, unresolved-operation refusal, committed forward
recovery and terminal snapshot-replay refusal. The test's systemd/lifecycle and
health checks are mocked, so it does not establish container cohort management.
It ran in a private mount namespace that hid the test distro's existing helper
directory; the fixture-owned installation paths were absent before the test and
removed by its teardown. Local log:
`artifacts/container-egress/recovery-format3-transaction.log`.

The core supervisor now has a fixed update shutdown path that requires the
long-lived Manager to be stopped by the transaction owner. It stops the existing
owned Envoy container by its inspected immutable ID and confirms it is stopped
before retiring the four owned core containers in reverse order. It performs no
readiness/bootstrap, image acquisition or policy publication. Foreign ownership,
uncertain inspection/stop and failed retirement refuse shutdown completion.
The updater must still acquire the existing exclusive writer lease before
snapshot/restore; successful retirement alone is not that lease.

Five focused mock tests passed. Native AOT report
`artifacts/container-egress/runtime-proof-a13051b1.json` passed with no cleanup
errors and 70 matching normalized-text source hashes. It exercises the actual
four core containers, confirms update shutdown leaves Envoy stopped and releases
their host writer lock, then successfully reconstructs egress. Host artifact and
cold-archive checks also passed. This focused run omits workload regressions and
uses the bounded RAM-backed store; it does not prove persistence or the installed
updater CLI/root-helper integration, which remain to be implemented.

Manager now dispatches fixed local maintenance commands before its normal writer
lease, configuration providers, certificates, hosted workers and web server.
`--update-core-stop <operationId>` requires matching fixed journal/maintenance
records and the actual installed source/target executable. Capability discovery
is `--update-core-capabilities`; no generic commands, image references, mounts or
caller-selected configuration paths are accepted. The root helper owns record
ownership/ancestry checks, the clean runtime-owner environment and exclusion of
the long-lived Manager. The CLI neither writes the journal nor clears admission.

`--update-core-verify <operationId>` selects the source or target release from
the durable phase. It reads that release's JSON configuration and required image
inventory without environment/command-line providers, with the production data
root fixed. Verification first retires the old cohort, then starts fresh services
under the current marker and reports the four observed immutable container/image
IDs after full security/admission and health checks. This prevents adoption of
normal workers during precommit verification; a committed-phase restart launches
normal workers again. Ordinary reconciliation still refuses precommit startup.

The architect cleared the source review. Focused tests passed 27/27. Actual
Manager Native AOT publishing succeeded; the native executable returned the
capability and rejected malformed maintenance arguments before requiring the
host writer lock. Native report
`artifacts/container-egress/runtime-proof-82131b76.json` passed with clean teardown
and 70 matching runtime/shared/fixture input hashes: fresh-cohort maintenance
verification, ordinary fence refusal, actual core health, closed-proxy shutdown,
writer-lock release and subsequent egress recovery. This RAM-backed fixture does
not execute the installed CLI/root-helper transaction. That wiring remains
unfinished, and container update activation must not be enabled before it exists.
