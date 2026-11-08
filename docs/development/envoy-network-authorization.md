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
allocations. A stale or unknown generation denies without generating an approval.
A check's snapshot read is its decision point. Publication does not revoke an
already established connection or promise to interrupt a check already executing
against the previous accepted snapshot.

Connection logs remain bounded outcome history, including authorization rejection
flags. They are not a reliable source of approval requests. Unavailable Manager,
invalid attributes, stale generation, timeout and capacity rejection also deny;
these failures do not imply that the user should approve a destination. Known log
collection gaps remain visible, and Linux firewall drops are not individually
recorded in Envoy's connection history.

Normal Manager startup hosts the authorization endpoint before the core-service
supervisor. The standalone update verification command also hosts it for its
verification lifetime while normal Manager is stopped. The stop-only maintenance
command does not start the endpoint or bootstrap networking.

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
