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


### Container resource-limit rejection checkpoint (6 November 2026)

The runtime-owner fixture accepts optional `--workload-source <path>/resource-workload.c`. It compiles a bounded C workload locally, packages only that executable and its dynamic loader/libc, and imports the archive into the disposable owner's private storage. Containers use the recorded image ID with `--pull=never`, `--network=none`, a nonroot UID, no capabilities, no new privileges, and a read-only root filesystem. This offline fixture import is **not signed registry acquisition evidence**.

Requested container limits are 32 MiB memory with no swap, 16 processes and 25% of one CPU. Parent units independently cap memory at 256 MiB, tasks at 128, CPU at one core and runtime at 75 seconds, so missing container enforcement cannot trigger unbounded workloads. The workload checks the exercised container's own kernel limit files. A passing process test requires exactly fifteen children and a local `pids.events` limit hit; a passing CPU test requires local `cpu.stat` throttling deltas as well as timing. Memory success requires exit 137 and Podman's confirmed OOM-killed state. The architect identified the host-contention and unrelated-process-limit false positives before privileged execution; both were corrected.

**The explicit cgroupfs candidate failed.** In the Fedora/WSL experiment, the first owner's actual container observed the parent limits (`memory.max=268435456`, `pids.max=128`, `cpu.max=100000 100000`) rather than its requested limits. This rejects the candidate's resource-enforcement proof; it does not yet distinguish missing child limits from an incorrect cgroup mount/view. Resolving that distinction requires the running workload's host PID/cgroup path and descendant kernel files. The exercised workloads therefore refused to report success. The second owner was not exercised after this rejection. All recorded resources were cleaned up without errors. Nine unprivileged Python tests pass on Linux; on Windows four pass and five Linux-only checks skip. The C fixture compiles with `-O2 -Wall -Wextra -Werror`.

Do not select this cgroupfs layout for production or drop container resource limits. The next candidate is a managed systemd user manager/session bus with the entire user-manager tree attached to the controlled outer network namespace. Namespace attachment and resource enforcement must both be measured again; a user manager that spawns helpers or scopes outside containment is unacceptable. Gate 1 remains **in progress**.


### Managed non-login user-manager checkpoint (6 November 2026)

A held container resolved the preceding cgroupfs ambiguity: its host PID was directly in the parent fixture service cgroup, with the parent limits and no separate limited child. That is a rejection of this tested layout, not a statement about all possible Podman/cgroupfs configurations.

The next explicit candidate, `--cgroup-manager systemd --managed-user-session --workload-source <path>/resource-workload.c`, provisions an instance-specific runtime drop-in for each disposable `user@UID.service`, before starting it. The entire user manager is attached to its controlled network namespace with bounded ancestor resources. Its private `/run/user/UID` ownership/mode and runtime-directory dependencies are verified. The launching client enters that namespace before dropping privileges, uses a clean environment and explicit bus, and starts the probe through the user manager. Cleanup stops the owned user manager and runtime-directory service before deleting only its instance drop-in and reloading systemd; no global user-manager template or lingering setting is changed.

The first managed-user candidate exposed `(sd-pam)` outside the controlled namespace and was rejected. The explicit non-login candidate clears `PAMName=` and supplies HOME/runtime/bus settings. A second process snapshot caught the fixture's own user-bus client outside the namespace; the client now enters the namespace before dropping its UID. Neither process was exempted from the acceptance check.

Both owners pass the corrected Fedora/WSL candidate:

- Host PID/cgroup correlation and container-visible kernel files show separate child limits of 32 MiB, 16 tasks and `cpu.max=25000 100000`.
- Each process workload creates fifteen children, then receives the expected failure with a local `pids.events` limit hit.
- CPU workloads use approximately 0.51/0.52 CPU seconds over two wall-clock seconds, with twenty local throttling periods each.
- Both memory workloads exit 137 with the container's OOM-killed flag confirmed.
- Root-side snapshots while each held workload exists place the user manager, D-Bus broker, user-bus client, pause process and container monitor in the registered outer namespace. Only the exact `--network=none` workload PID may occupy its registered inner namespace. Snapshots do not establish coverage of short-lived networking/pull helpers.
- Cleanup reports no errors. Nine unprivileged Python tests pass on Linux; four pass and five skip on Windows. The bounded C workload compiles with warnings treated as errors.

The architect reviewed the failures, candidate changes and corrected evidence. This remains **WSL feasibility**, not a production runtime selection. The Fedora PAM stack includes SELinux session context, login UID, keyring, namespace, umask and system-auth hooks; a non-login production design must explicitly account for required security/session settings and validate native Linux with SELinux enabled. Actual pasta/slirp networking, signed registry acquisition, alternate-route denial and supervision still require proof. Gate 1 remains **in progress**.


### Rootless pasta admitted-traffic checkpoint (6 November 2026)

`scripts/network-gateway/probe-runtime-network.py --report <new-file> --admission-binary <published-AOT-path> --workload-source <path>/resource-workload.c` exercises real rootless pasta sockets against the isolated simulated uplink. It uses the two disposable runtime owners and the managed non-login systemd candidate above. Each owner's outer namespace is attached sequentially to a fresh gateway/sink topology; there is no real host-uplink interface or route. Containers use the locally built/imported fixture image ID, `--pull=never`, explicit pasta networking, nonroot execution and the resource limits. No proxy variables or TLS interception are involved.

The borrowed source namespace is validated against a retained descriptor and must contain only loopback with no main-table routes. Source operations and interface cleanup use that descriptor. The network fixture owns only its new veth/gateway/sink resources; the runtime-owner fixture retains ownership of its source namespace/account/storage. A failure-injection test verifies that failed link setup does not claim or delete the borrowed namespace. Topology names are retained in the resource manifest for cleanup recovery.

Observed on Fedora/WSL:

- Both core and work owners pass TCP/UDP IPv4/IPv6 container traffic: eight successful nonce exchanges total.
- The simulated uplink checks the durable admission using the translated source tuple it receives, before echoing the payload. The runner also requires the exact nonce sequence and records the admission rows. Attribution here is to the isolated runtime-owner boundary; it does not prove per-connector attribution.
- Root-side held-workload snapshots contain a real pasta executable, the user manager, bus, pause process and container monitor in the registered outer namespace, with the exact workload PID in its registered inner namespace. The first run rejected the distro's `pasta.avx2` command name; the check now accepts the explicit pasta/pasta.avx2/pasta.avx512 variants. That diagnostic rejection is retained separately.
- Ten unprivileged Python tests pass on Linux; five pass and five Linux-only checks skip on Windows. The C workload compiles with warnings treated as errors.
- All twelve existing published-AOT kernel checks pass again after the topology refactor. Both live fixtures report no cleanup errors.

The architect reviewed the code and evidence. These are **positive admitted-traffic checks**. The pasta-specific consumer-loss/new-tuple tests, actual alternate-uplink/retained-descriptor escape attempts, short-lived pull-helper coverage, signed acquisition, controlled DNS and native Linux PAM/SELinux validation remain outstanding. Namespace snapshots alone cannot prove where every socket or retained namespace descriptor is used. Gate 1 remains **in progress**, and application enforcement is not enabled.


### Pasta consumer-absence checkpoint (6 November 2026)

The runtime-network probe adds optional `--failure-check`. After each owner's four positive protocol checks, a trusted fixture-only control handshake stops and reaps the admission consumer. Root clears only the newly created gateway namespace's IPv4/IPv6 conntrack tables and verifies they are empty before acknowledging. This setup prevents a reused pasta source tuple from inheriting an earlier established-flow permission. It is not a production revocation/restart procedure, and the root callback is not an interface for untrusted workload instructions.

The published-AOT fixture now exposes four observational counters immediately before the mandatory new-flow queue path. Each denied attempt must exit through the workload's connection-failure result, produce no sink nonce, leave the durable admission count unchanged from the separately checked baseline, and increase its protocol/family queue counter. The counters do not add an acceptance path.

Both owners pass all four new-flow denial checks after consumer loss: eight denied pasta-mediated attempts total, no extra durable admissions, positive-only nonce sequences at the sink, and increases in all four queue counters. Boundary-denial counters remain zero, so the attempts reached the intended new-flow path. Both owners' positive baselines also pass, and cleanup reports no errors.

A first rerun exposed CRLF line endings in a Windows-edited C# raw rule string. `nft` rejected those rules and the consumer stopped closed. The nft input adapter now normalizes line endings to LF; the republished Native AOT binary passes the experiment. Forty focused Windows C# tests pass, and ten Python tests pass on Linux (five pass/five Linux-only skips on Windows). Formatting, documentation validation and commit hooks pass.

This establishes **new-flow denial with the consumer absent after a fixture-only conntrack reset** in the tested WSL configuration. It does not establish production conntrack reconciliation, restart recovery, or existing-flow revocation behind pasta. Controlled DNS, signed acquisition, alternate-uplink escape attempts and native Linux support remain outstanding. Gate 1 is still **in progress**.


### Signed fixture acquisition-plan checkpoint (6 November 2026)

The prototype adds `probe-verify-artifact <manifest> <signature> <trusted-public-key>`. It reuses the existing `Kautuka.Telemetry.Security.ReleaseManifestSignatureVerifier` used by UpdateService, rather than inventing another signature algorithm. Bounded reads authenticate the exact manifest bytes before strict parsing; duplicate or expanded fields are rejected. The fixture plan binds the exact registry/repository, Linux/amd64 platform, lowercase SHA-256 manifest/configuration digests, release identity and compatibility marker. Only a successful check emits the source-generated immutable plan. This is **not the production release/update format**.

Trust provisioning belongs to the root-owned fixture caller: it supplies a fixed P-256 public key outside runtime-owner-writable paths and consumes the verifier's output directly. The command's arbitrary key-file parameter is not a production trust store or permission for a runtime owner to choose a signing root. Existing shared verifier behaviour is unchanged. Signature verification has not been added to the installed network gateway, Manager lifecycle or updater.

A separate `probe-registry` admission profile authorizes only the fixture's IPv4 TCP registry endpoint on port 20443 under the artifact subject. It does not inherit the echo protocol policy. This endpoint profile has not yet been exercised with a registry.

Evidence: 51 focused tests pass on both Windows and Linux, including modified bytes, an untrusted signer, signed scope expansion, duplicate/unknown fields and malformed/oversized inputs. Actual Linux Native AOT publication succeeds without new trimming/AOT warnings. The executable grows from 3,903,504 to 4,600,080 bytes after referencing the existing Telemetry verifier (696,576 bytes); no assumption is made that every unrelated dependency vanishes. The published command accepts a valid detached P-256 signature and rejects changed bytes, an untrusted signer and malformed key input with no verified-plan output. Ephemeral private keys stayed in a root-private temporary directory and were removed after the experiment.

The architect reviewed the code and evidence. The local TLS OCI registry, real Podman digest pull, post-pull image/configuration check and activation-negative cases remain next. This checkpoint does not prove transport, real registry interoperability, production trust rotation or anti-rollback. Gate 1 remains **in progress**.
