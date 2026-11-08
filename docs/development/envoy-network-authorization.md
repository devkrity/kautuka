# Envoy network authorization

Manager serves stock Envoy's `envoy.service.auth.v3.Authorization/Check` on a
dedicated HTTP/2 Unix socket. This is an endpoint in Manager, not another service.
Only Envoy mounts its directory. The directory is mode 0750 and the socket 0660;
the ordinary Manager RPC listener and its authentication remain separate.

For transparent external TCP connections, Envoy supplies source and original
destination addresses and ports, plus visible TLS SNI. Manager checks the
accepted policy and allocation snapshot. A missing permission is persisted in
the existing policy store before Deny returns. Gateway reads and decides those
requests through its existing network-access API. Approval requires a new client
attempt; no connection is held for approval and no action is resumed.

Gateway resolves agent workspace identities back to visible catalog agents using
the same canonical scope ID builder as workspace creation. The network-access
page shows the current agent name alongside the connector and owner names;
runtime IDs remain unchanged under **Technical identity**. Missing, deleted or
inaccessible catalog entries retain the technical-ID fallback.

Every protected TCP proxy explicitly uses `ON_DOWNSTREAM_DATA` with an 8192-byte
early-data limit. Authorization errors and the two-second authorization timeout
fail closed. The listener also closes on its two-second inspection timeout.
Silent/server-first protocols are unsupported. TLS is inspected for SNI without
decrypting application traffic.

Existing routing remains authoritative: hostname grants select their configured
DNS clusters, literal-address grants select fixed endpoints, and private grants
use the separate private upstream source. Unknown routes use an empty cluster,
so even an erroneous Allow cannot make an unknown destination routable. The
Linux firewall still blocks metadata and limits LAN access to explicitly approved
private routes. A public hostname resolving privately can pass the hostname
authorization check and still fail at that firewall; it does not automatically
gain LAN permission.

Listener publications carry an authorization generation tied to policy and source
allocations, including the exact container ID behind each source IP. Before
publication, Manager atomically exposes the accepted and candidate snapshots.
During activation a check from either generation may proceed only when both
snapshots agree on its allocation, identity and selected SNI-or-IP rule, including
the private-network source binding. New grants wait for acceptance; removals,
ownership changes and privilege changes fail closed during the transition.

After acceptance, old generations describe routes rather than independent
permissions. Each is checked against the current identity, allocation and exact
selected rule. Manager retains at most eight old descriptors for ten seconds,
using a monotonic clock; expired, evicted and incompatible generations require a
retry without creating an approval. This provides bounded continuity across
ordinary updates, not unlimited continuity under arbitrarily rapid churn.
Confirmed rollback restores the last accepted policy and checks any briefly
loaded candidate route against that restored authority. Failed rollback clears
authorization and stops Envoy. Established connections retain the existing
new-connection policy contract.

The first missing-permission request is durable before Deny returns. Retries of
an existing pending request update attempt and last-seen counters only in memory;
the next real request or policy write flushes them. Restart may lose those counter
increments, but not the pending request. The direct path no longer constructs
random observation IDs, batches log observations or maintains a dedupe history.
Pending-request storage is bounded; exhaustion is an operational failure rather
than a silently omitted request. Distinct first requests still require durable
writes, so repeated-request efficiency is not a claim of unlimited flood isolation.

Connection logs remain bounded outcome history, including authorization rejection
details from stock `%RESPONSE_CODE_DETAILS%`. `ext_authz_denied` is a deliberate
denial; `ext_authz_error` is an authorization-service fault. `UAEX` alone is
ambiguous and is not presented as a policy block. Activity labels the latter
**Authorization unavailable**, and generic close records do not erase it.
The pinned Envoy 1.40.2 logging fixture observed both details on matched and
unknown chains for denial, gRPC Unavailable, timeout and authorizer absence.
That fixture tests logging on loopback; the separate rootless Manager fixture
establishes the routing and direct-approval boundary.
Logs are not a reliable source of approval requests. Unavailable Manager,
invalid attributes, unavailable generations, timeout and capacity rejection also
fail closed. Capacity, storage and routing-transition faults use actual gRPC
transport errors; deliberate denials use the authorization response. These faults
do not imply that the user should approve a destination. Known log
collection gaps remain visible, and Linux firewall drops are not individually
recorded in Envoy's connection history.

Normal Manager startup hosts the authorization endpoint before the core-service
supervisor. The standalone update verification command also hosts it for its
verification lifetime while normal Manager is stopped. The stop-only maintenance
command does not start the endpoint or bootstrap networking.

Container image acquisition checks the effective persisted policy, including UI
decisions and explicit Blocks, before starting its restricted helper. A missing
registry grant creates a durable core-scoped request labelled **Container image
downloads**. Approving a registry does not implicitly approve a redirected blob
host. Those destinations receive their own direct authorization requests. Helper
failure diagnostics show at most five pending endpoints observed for acquisition
during that attempt, without reading Envoy logs or claiming every helper failure
is a network-policy failure. The acquisition gate serializes downloads, but a
late check from a previous helper can overlap this diagnostic time window.
No registry or redirect failure permits a direct host pull.

## Validation boundaries

The integration tests cover generated filter ordering and delayed TCP connection
configuration, generation/source validation, durable direct approval creation,
owner filtering through Manager RPC, and the existing Gateway HTTP authorization
boundary. The Linux-only endpoint test checks HTTP/2 Unix-socket access, modes and
the absence of an ambient Kestrel TCP listener.

Runtime proof uses a disposable Fedora WSL distro and a dedicated rootless Podman
account. Its native executable references the production Manager, policy store,
runtime and configuration compiler. Observed runtime results and limitations are
recorded separately from the unit and HTTP test results. The developer appliance
and installed application data are outside that fixture.

## Observed rootless runtime checkpoint, 8 November 2026

The Native AOT fixture exercised the production authorization host and runtime
under a dedicated rootless Podman account on Fedora. A previously unknown IP/port
created a pending request through the actual Manager policy RPC without calling
audit collection. An explicit administrator LAN decision activated the route and
a client retry received the test server's nonce. A second owner remained denied;
stopping the authorization host also denied a new connection on the approved
route. Packet capture showed an outbound SYN only for the approved attempt, with
the reserved private source translated to the pod's normal outside address.

The test server was a separate container on the outside network. WSL's own host
address refused connections from an ordinary outside-network container as well
as from the private-bound proxy; that first fixture attempt is not counted as a
successful connection test. The successful run demonstrates the rootless bridge,
private-source translation and authorization path. It does not establish physical
LAN reachability through the outer rootless helper. The earlier isolated
transparent-network experiment covers SNI, wrong-IP binding, timeout and metadata
cases; those cases are not represented as fresh rootless end-to-end tests here.


## Remote HTTP connector decision boundary

The internal `CheckRemoteConnectorNetwork` Manager RPC accepts the trusted
Gateway caller and an exact `remote-connector` subject: user, canonical agent
workspace (or explicit management scope), and user-scoped connector ID. Neither
an administrator actor nor a shared Gateway grant can substitute for that
subject's permission. Only the Gateway mTLS identity may call the RPC.

Logical permissions live in the existing policy and approval store. A missing
permission is durably recorded before returning `missing_permission`; an explicit
Block returns `blocked`. Routing prerequisites and policy transitions return
separate decisions without reopening an already-approved logical request.
Accepted logical policy data is included in routing generation binding. A
candidate representing the desired revision permits unchanged logical grants,
selected outer routes and allocations to continue; incompatible changes require
a retry.

Public-only logical grants contribute to the existing shared Gateway public
route union. Explicit core Gateway decisions override the union. Private logical
approvals never create a shared Gateway LAN entitlement. The logical and selected
outer route must agree on private-source privilege. Metadata remains forbidden,
and the Linux firewall still enforces actual upstream destination ranges.

Plain HTTP hostname calls retain a logical hostname permission but require a
transport-pinned IPv4 address and an explicitly approved compatible core Gateway
IP route. HTTP has no TLS SNI, so a hostname rule alone cannot select that route.
HTTPS checks conservatively require a core hostname route; public grants derive
it, while private grants require a separate explicit core hostname approval.
This application check does not reproduce the native listener's literal-IP
fallback for HTTPS hostnames.

An allowed response carries an opaque binding to the exact subject, destination,
Gateway source/container allocation, selected route and private privilege.
Gateway integration must bind its transport to the first successful value and
reject a later mismatch before resolving credentials or sending a pooled
request. An unrelated policy generation does not change this binding.

The Manager boundary has focused regression coverage. Gateway request-handler
integration, including initialization, discovery, invocation, connection tests,
immutable HTTP transport pins and pooled downgrade rejection, remains a separate
checkpoint. This is trusted Gateway application authorization, not a separate
packet identity for each remote connector. Existing active streaming responses
are outside the per-send revocation boundary.


## Collector credentials and exporter transport

The pinned managed OpenObserve catalog explicitly sets `ZO_TELEMETRY=false`.
Manager generates a strong per-install root password and stores it under the
private `telemetry/manager` directory. Publication uses private temporary files,
atomic moves and file/directory flushes. Collector data is preserved if its root
credential does not match; no shared development password is tried as fallback.

Manager supplies the root password to the collector through a temporary private
environment file. It creates or reuses the named ingestion-only token through
the existing restricted host Unix-socket route. Only the exact ingestion-token
GET/POST API is added to that private route. Core applications mount only
`telemetry/exporter` read-only, never the root-credential directory. Their stable
configuration names the authorization file rather than putting its value in
container arguments or runtime fingerprints. Atomic replacement is observed by
the next request without restarting core applications.

Host exporters use the fixed Unix socket; core exporters use only the fixed
private collector origin and signal path. Both asynchronous and synchronous
HTTP sends apply the same credential and endpoint checks. Synchronous exporter
calls deliberately use the checked asynchronous transport internally, because
the Unix-socket connection callback uses that transport. Missing or invalid
credentials prevent a request; redirects and ambient proxies are disabled.
CredentialsHost remains networkless with export disabled.

The Fedora 44 rootless Native AOT cross-component fixture used the pinned
collector and stock Envoy with SDK 10.0.403. It observed accepted OTLP log payloads
through host and core routes, successful SDK metric and trace flushes through
both routes, rejected invalid credentials, and ingestion-token rejection on
read/admin APIs. It checked private credential modes, root absence from core
mounts/arguments, optional collector outage without blocking readiness or changing
core identity, and credential reuse after collector restart. This establishes
transport and credential behavior; it does not claim a query of every persisted
metric/span or a new full installed-application acceptance run.
