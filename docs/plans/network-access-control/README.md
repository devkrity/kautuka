# Network access control implementation

**Status:** Active
**Owner:** Kautuka maintainers
**Activation condition:** Work on `enhancement/network-access-control`, based on `main` at `5ca94f0`. Commits are authorised; merging is not.

## Goal and authority

Implement the [revision 4.1 proposal](spec-rev4-1.txt) and Part A of the [package-service addendum](package-service-addendum.txt), with the amendments below. The imported documents are design inputs, not evidence that the feature exists. This page resolves contradictions in those snapshots. Part B (package-name/version policy) is deferred.

All Kautuka-initiated external traffic must cross one mandatory network gateway for policy and durable admission. Preserve rootless Podman, separate core/workload owners, container-first application services, local independent network infrastructure, and offline recovery. Connector enforcement is required; additional per-call attribution is supplementary. No unrestricted fallback or implicit switch to rootful Podman is permitted.

The original input content is preserved as text snapshots, subject to Git's line-ending normalization. Their references to older proposal revisions and external review files are historical context, not additional implementation dependencies.

## Governing amendments

1. **Revocation acknowledgement.** A revocation is acknowledged as enforced only after kernel permission removal. Operational revocations stop forwarding within two seconds of activation. When the policy service cannot enforce a request, the request remains pending and is not reported as successful. Already admitted flows expire within the surviving kernel lease (at most 120 seconds); new admissions fail closed. A two-second outage-time revocation guarantee requires an independently tested enforcement path and is not currently claimed.
2. **Namespace validation.** Validate process roles, service/cgroup membership, mapped identities and the registered namespace/interface generation graph. Do not require every process of an owner UID to inhabit one namespace. Legitimate rootless inner namespaces are allowed only as registered topology. Quarantine retired outer paths before cleanup. Host-namespace UID rules are only a backstop for host-originated sockets, not rules governing every stale namespace.
3. **Candidate A DNS.** Gate 2 must prove authenticated per-pod DNS identity, query policy, bindings and approval ownership. Coarse runtime identity is not a fallback for connector DNS. Candidate B must prove DNS source preservation as well as TCP/UDP identity.
4. **Admission handles.** Socket/packet marks are bounded internal handles, bound to the complete flow, trusted creator, runtime identity and generation. They are not unchecked database IDs. Define packet-to-conntrack mark propagation, source-port reservation before transmission, synchronization with revocation, and safe handle reuse. Persist admission before releasing the first external packet.
5. **Denials and trust.** Denials use `NF_DROP`; a packet already dropped cannot subsequently reach a reject chain. Prompt reset is optional and separately tested. Cached/imported image activation is checked by Manager against recorded verified image identity, not by an assumed Podman run-time signature check. Silent network drop does not promise immediate client failure.
6. **Package interface.** Enforce a workload-facing route/method allowlist for the minimum supported npm/PyPI protocol, including artifact paths; deny every other ecosystem, generic route, management API, UI, metrics and cache-listing surface before handler/cache access. The backend is unreachable directly. Gateway host rules alone do not enforce the HTTP interface.
7. **Package permission.** Package-service access is an explicit capability for approved materialisation or package-enabled workload contexts. Ordinary mail execution receives no standing package access. No direct registry access is granted to workloads. Package rules cannot be bypassed through a shared service.
8. **Package evidence.** The pinned upstream source has many ecosystem routes, but generic routes require named configured upstreams and no Git handler was found in v0.8.1. Verify the shipped image digest against its source before claiming provenance. Record durable admissions per external flow, not per HTTP fetch; request logs and cache hits are separate. Preserve the `dns_bound` numeric-address limitation. Observed CDN IPs do not authorise hostname wildcards.
9. **Package tests.** Exercise cold and warm client/proxy caches, unauthorized callers, backend bypass, route/method rejection, redirects, direct registry attempts, and revoked local service access. Observe HTTPS redirects at the proxy or a controlled upstream, not by assuming packet capture exposes encrypted headers.
10. **Deferred package policy.** Part B requires authenticated caller identity at the package-service endpoint; it is not inherited automatically from either gateway identity candidate. URL bearer tokens cannot independently choose broader package policy. Describe artifacts as served, not confirmed installed. Do not add Part B to the stop gates.

## Sequential stop gates

| Gate | Required proof | Current state |
|---|---|---|
| 1 | Both runtime owners; gated signed acquisition; helper/implicit-pull/DNS/stale-path denial; durable first-packet admission and failure/restart tests; published AOT binaries; WSL primitive smoke check | In progress; no enforcement claim |
| 2 | Distinct connector and DNS policy identities; lateral isolation; constrained package interface and explicit package permission | Not started |
| 3 | Minimal core-service socket/health chain; staged signed release transaction; offline recovery; schema and write-fence safety | Not started |
| 4 | Full shipped WSL NAT integration; desktop and phone WebHost ingress | Not started |

Stop at a failed gate and report the evidence. Do not proceed by silently weakening the invariant. Record Linux and WSL evidence separately. Windows unit tests alone do not establish Linux kernel behaviour; a WSL smoke check does not establish the full Linux and WSL support matrix.

## Review and commits

Use the requested GPT-6 Astra architect at medium reasoning effort for code reviews at meaningful checkpoints. Resolve security findings before committing the affected implementation. Run applicable local checks with repository hooks enabled and DCO sign-off. Keep checkpoints focused, leave the working tree reviewable, and do not merge or publish a release.

## Evidence and limitations

- Baseline inspected: clean local `main` at `5ca94f0`.
- Local .NET SDK: 10.0.403.
- Available smoke environment: `KautukaTestFedora44`, Podman 5.8.9, WSL kernel 6.18.58.1. This is a local test distro, not proof of the final shipped configuration.
- Kernel configuration exposes network namespaces/veth and modular NFQUEUE/conntrack features; runtime probes remain required.
- No existing installation, Podman storage, or application data may be reset merely to run the prototype. Temporary test topology must have unique names and clean up only resources it created.

### Primitive checkpoint (6 November 2026)

`scripts/network-gateway/probe-primitives.py` creates isolated source, gateway and simulated-uplink namespaces. It never attaches them to the real uplink. Run on Linux as root with `python3 scripts/network-gateway/probe-primitives.py --report /tmp/<new-evidence-name>.json`. The report must be a new file; existing evidence is never overwritten. Unprivileged cleanup fault tests run with `python -m unittest discover -s scripts/network-gateway -p 'test_*.py'`.

Local Fedora/WSL result: six primitive checks passed: namespace/veth routing, allowed UDP, no-consumer NFQUEUE drop in FORWARD and OUTPUT, default-drop forwarding, and cutoff from a namespace retained by an open descriptor after its name was removed. Denial checks observe the sink's receipt log, not only client timeouts. All created namespaces and the echo child were cleaned up.

The first probe invocation exposed a diagnostic-format assumption (`queue num` versus nft's normalized output); the probe now checks structured nft JSON. The architect requested retained-namespace coverage, sink-side denial evidence, and cleanup failure isolation; all were addressed before this checkpoint.

This is **not a Gate 1 pass**. A real admission consumer, durable audit, conntrack/handle/revocation proof, IPv6, both runtime owners, rootless-helper containment, signed acquisition, published AOT execution, and native Linux coverage remain outstanding.

### Admission checkpoint (6 November 2026)

`src/backend/Kautuka.NetworkGateway` now contains the isolated admission prototype: immutable exact-endpoint policy with ingress-interface binding, SQLite WAL/FULL admissions and persistent revocations, bounded lease coordination, conservative IP parsing, raw NFQUEUE UAPI encoding/decoding, and source-generated Linux native interop. A terminal coordinator/transport failure closes the consumer instead of continuing under uncertain state.

The `probe-admission` command is intentionally fixture-only: it verifies an explicitly named `ktng-*` namespace before applying its rules, has one fixed UDP endpoint, and is not installed, enabled, or used by Manager. It clears incoming packet marks, queues new flows, validates the returned permission in a later hook, and copies it to conntrack state for the kernel fast path. Both hooks enforce fixture ingress/address/direction guards before trusting a conntrack permission. The OUTPUT path currently denies all traffic.

Local evidence:

- 38 focused C# tests pass on Windows (.NET SDK 10.0.403) and the Fedora/WSL test distro (self-contained .NET 10.0.14).
- The Linux Native AOT binary publishes and executes on Fedora/WSL using SDK 10.0.113 with `-r linux-x64 -p:RuntimeFrameworkVersion=10.0.14`. The older compiler/runtime package selected by that distro SDK was rejected by NuGet's security audit; the patched runtime selection passes without suppressing the audit.
- Nine live fixture checks pass when the primitive probe receives `--admission-binary <published-path>/Kautuka.NetworkGateway`. They include a durable admission observed by the simulated uplink at first receipt, consumer death blocking new flows, and replay of an admitted tuple from the wrong interface being dropped by the explicit boundary guard. Cleanup reported no errors.
- The three Python cleanup/ownership fault tests pass. The complete Windows solution builds with zero warnings/errors.
- The complete backend pre-push test selection passes: 2,437 succeeded, 4 skipped, 0 failed (API-flow tests excluded by the repository's filter). The integration suite completed in approximately ten minutes. The skips cover Linux-only filesystem/socket behaviour and unavailable local broker/sandbox fixtures; they are not evidence for those paths.
- The architect identified post-publication lease expiry, persistent revocation/startup state, callback-failure handling, and fast-path ingress binding; these were addressed with tests before checkpoint review.

Still **not a Gate 1 pass**: no real-uplink route is installed and no application behaviour changes. Required work remains: TCP/IPv6, permitted OUTPUT admission, conntrack retirement/accounting/reconciliation, real kernel revocation and pending races, throughput/overload, topology supervision, both runtime owners/helper coverage, controlled DNS, signed registry acquisition, and the native Linux support matrix. The retained fixture namespace descriptor is not a production namespace supervisor.

### Protocol and kernel-lifecycle checkpoint (6 November 2026)

The fixture now admits basic TCP and UDP over both IPv4 and IPv6. It retains four application sockets, verifies unique bytes at the simulated uplink, kills the queue consumer, verifies that new connections in all four cases are denied, and then verifies that the existing connections continue through kernel permission state. Removing the actual kernel admission handles blocks both source-to-sink payloads and independently generated sink-to-source payloads. This is proof of the **kernel grant-removal mechanism**, not the complete policy-service revocation/acknowledgement lifecycle.

The updated published-AOT run passes twelve live checks with no cleanup errors. Forty focused C# tests pass on Windows and Linux; four unprivileged Python harness tests pass. SQLite startup now checks a dedicated application marker and schema version before changing journal mode or creating tables. A real second-connection writer lock denies admission with zero kernel publication/allow verdicts. Unmarked early prototype ledgers and unrelated databases are refused rather than silently adopted; this does not affect any application store.

The only gateway OUTPUT exception added is IPv6 neighbor solicitation/advertisement on the two fixture interfaces with hop limit 255. General OUTPUT admission remains unimplemented. IPv6 extension-header/fragment admission and PMTU/control-plane coverage remain unsupported/unvalidated; a decoder check on new admission is not a claim that every established-flow packet is decoded again. Conntrack retirement, accounting, overload/performance, topology supervision, runtime owners/helpers, controlled DNS, signed acquisition and the native Linux matrix remain outstanding. Gate 1 remains **in progress**.

### Runtime-owner feasibility checkpoint (6 November 2026)

`scripts/network-gateway/probe-runtime-owners.py` provisions two disposable accounts in a dedicated test environment, disjoint subordinate allocations, private homes/runtime/storage, and network namespaces containing only loopback. It runs Podman metadata/unshare operations as systemd-managed `User=` units with namespace attachment and delegation. It verifies effective UID/GID maps, outer namespace/cgroup, isolated graph roots, and denial of access to the other owner's private home. It does not acquire images or test external networking.

Run as Linux root with an explicit `--cgroup-manager systemd` or `--cgroup-manager cgroupfs` and a new `--report` file. Do not run alongside unrelated account provisioning: the root-owned fixture lock serializes this fixture only. Cleanup stops only the recorded units, checks for owner/subordinate-UID survivors, removes only recorded accounts/mappings/namespaces, and deletes only its validated temporary root. Incomplete cleanup preserves the resource manifest and reports failure.

Observed in the Fedora/WSL test environment:

- Requested **systemd** was rejected because Podman reported **cgroupfs**. The probe did not silently accept the fallback; cleanup completed without errors.
- A separate, explicitly requested **cgroupfs** experiment passed both owners' identity/mapping/namespace/storage/directory-access checks. Podman's warning about the unavailable user-session bus for pause-process sandboxing is retained in the report.
- Eight unprivileged Python tests pass on Linux. On Windows, four pass and the four Linux allocation/mapping tests are explicitly skipped.

The architect approved this limited feasibility checkpoint. **No production cgroup-manager choice has been made.** Before selecting cgroupfs, actual containers must prove memory, process-count and CPU limits from kernel cgroup state and exercised workloads, with bounded parent-unit ceilings. The real pasta/slirp/pull-helper paths, alternate-route denial, startup/restart supervision and signed acquisition remain unproved. Gate 1 is still **in progress**.
