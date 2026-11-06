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


### Positive signed-plan/TLS acquisition checkpoint (6 November 2026)

`scripts/network-gateway/probe-artifact-acquisition.py --report <new-file> --admission-binary <published-AOT-path>` builds a tiny OCI image from the offline bounded workload, generates ephemeral P-256 signing/TLS material, verifies the signed acquisition plan with the published AOT command, and exports only the verified plan and fixture CA for the disposable runtime owners. Private keys remain in root-private directories. The fixture CA is passed using Podman's per-command certificate directory with TLS verification explicitly enabled; global trust is untouched. Proxy environment variables are explicitly excluded from the managed service.

Each owner starts with fresh private image storage and pulls the exact signed digest through the dedicated artifact admission profile. It compares the resulting manifest and configuration IDs before running the approved image with `--pull=never`, `--network=none`, nonroot execution and the bounded resource flags. Activation must return the expected nonce. The simulated uplink's tiny TLS OCI registry checks the translated peer tuple and artifact subject against the durable ledger **before wrapping the accepted socket for TLS**, then records connections and HTTP requests separately. Manifest, configuration and layer GET requests must all appear; requests are not equated with distinct admissions.

Both owners pass in Fedora/WSL, with no cleanup errors. A first run rejected Podman's bare hexadecimal configuration ID; the comparison now normalizes only the optional `sha256:` prefix and preserves exact digest equality. The failed report is retained separately. The successful run predates the subsequent diagnostic-only raw-ID field and failure-path registry-log capture additions. The architect reviewed the source and positive evidence.

This remains a **positive-only, local unauthenticated IPv4 TLS registry** proof. The process inventory occurs during a post-pull held container; actual active-pull helper/socket placement is not yet established. Corrupted content, cold acquisition with the consumer absent, registry authentication/token endpoints/redirects/CDNs, controlled DNS, real registry interoperability, trust rotation, anti-rollback, release transactions and native Linux PAM/SELinux parity remain outstanding. The fixture schema and root-supplied test key are not a production updater or trust-store design. Gate 1 remains **in progress**.


### Negative artifact-acquisition checkpoint (6 November 2026)

The acquisition probe adds optional `--failure-check`. Each negative attempt uses a separate new graph/runroot/tmp directory; the effective graph root and empty image list are checked before pulling. After rejection, both image and container lists must remain empty. This proves no completed image installation or container creation, not the absence of partially cached blobs during the failed attempt. All temporary stores are removed with the owning fixture at cleanup.

For corrupted-content evidence, the root-controlled registry serves valid but altered configuration JSON under the approved descriptor digest. Acceptance requires Podman's specific configuration-digest mismatch diagnostic, whose expected digest equals the signed configuration and whose calculated digest exactly matches the registry's independently recorded served-byte digest. A first run rejected an unrecognized diagnostic wording; the observed precise format is now parsed, and that rejected report is retained.

For consumer-absence evidence, root restores the correct content, stops/reaps the consumer, clears only the fixture gateway's conntrack state, verifies empty tables, and records the durable admission count, queue counter and registry-event boundary. A further cold-store pull must fail without any image/container installation. The admission count must remain unchanged, the new-flow queue counter must increase, and the registry must record no later connection or request. Source SYN retries are shortened only inside the disposable namespace to bound this test; this is not production retry policy or a production conntrack-reset procedure.

Both owners pass the positive baseline and both negative cases in Fedora/WSL. Corrupted-content and consumer-absent pulls return Podman exit 125. Each owner retains four admissions across consumer loss, its queued-TCP counter increases from four to seven, and its registry event count remains at twelve. Cleanup reports no errors. Eleven unprivileged Python tests pass on Linux; five pass and six Linux-only tests skip on Windows, including explicit compilation checks for embedded runtime programs.

The architect reviewed and approved this checkpoint against the recorded evidence. Active pull-process/socket placement, alternate-uplink escape, DNS, token/authentication/redirect/CDN handling, restart reconciliation and native Linux PAM/SELinux parity remain unproved. Gate 1 remains **in progress**.


### Active pull-socket placement checkpoint (6 November 2026)

Optional acquisition flag `--observe-pull` pauses the first admitted registry connection before TLS wrapping using a bounded, trusted fixture handshake. Root reads the IPv4 TCP table through the retained source-namespace descriptor and requires exactly one established socket with the registry's observed peer tuple and expected runtime UID. It then matches that socket inode to an actual process file descriptor, requiring a single Podman owner under the managed user-service cgroup and registered outer namespace. Captured owner/subordinate-UID participants must also inhabit that outer namespace. Only after successful observation does root acknowledge the registry to continue TLS.

Both owners pass the combined `--observe-pull --failure-check` experiment. Their observed socket inode maps to FD 7 in the expected Podman process; each process belongs to its user-manager application service and controlled namespace. The successful signed TLS pull/activation, corrupted-content rejection and consumer-absent cold-pull rejection all continue to pass. Cleanup reports no errors. Fifteen unprivileged Python tests pass on Linux; nine pass and six Linux-only checks skip on Windows. Added parser checks reject wrong UID, absent/ambiguous matches and truncated records.

The architect reviewed and approved this checkpoint. It establishes the placement of the **observed active IPv4 pull socket and captured participating processes**. The parser uses the tested Linux/amd64 little-endian `/proc/net/tcp` representation. It is not a portable production socket observer or exhaustive proof for every short-lived helper. DNS, alternate-uplink/stale-path escape, implicit-pull negative cases, OUTPUT admission and restart reconciliation remain outstanding, along with native Linux security/session validation. Gate 1 remains **in progress**.


### Typed local-output identity groundwork (6 November 2026)

The admission model now distinguishes forwarded flows from locally originated OUTPUT flows. Forwarded identity remains bound to its registered source/interface. A local actor requires an explicit nonzero socket UID, source address, egress interface and namespace generation. The queue codec reads the kernel hook, output interface and optional socket-credential attribute, requests conntrack plus UID/GID metadata, and keeps fail-open disabled. Missing OUTPUT UID and unsupported hooks cannot become admissions. Kernel socket-file creator credentials are not asserted to be live process identity.

The prototype ledger advances to version 2 and records hook/path, egress, nullable socket UID and namespace generation before publication. Old prototype version 1 and unrelated stores are refused before WAL/schema changes; no application database or installed ledger is migrated. Namespace generation currently defaults to fixture epoch `1`; actual namespace ownership/generation supervision is not implemented and this is not completed stale-namespace protection.

**Local OUTPUT is still disabled.** The current nft fixture denies it, and its kernel adapter explicitly rejects publication of local-output leases. This checkpoint adds typed evaluation/audit groundwork, not a resolver or enabled upstream route. Sixty focused C# tests pass on Windows and Linux, including wrong/missing/root UID, wrong interface, stale generation, separation from forwarded identity, durable expanded fields and persistent local-actor revocation. Linux Native AOT publication succeeds without new warnings; all twelve live kernel checks and both owners' combined observed/signed TLS acquisition positive/negative checks pass again, with no cleanup errors. The architect reviewed and approved this checkpoint.

The next local-upstream transport fixture uses a **trusted-infrastructure-only gateway namespace**, with a dedicated unprivileged actor. A conntrack grant does not identify the current UDP receiving socket: closing an authorized socket and rebinding its port under another UID can preserve an old reply tuple. OUTPUT ownership checks alone do not establish hostile-UID receive isolation. Keep untrusted workloads out of this namespace and include a close/rebind diagnostic before enabling broader INPUT/OUTPUT claims. DNS parsing, query policy, caching and response validation remain separate work. Gate 1 remains **in progress**.


### Trusted-infrastructure OUTPUT transport checkpoint (6 November 2026)

A separate, opt-in `probe-output <ktng-namespace> <audit.sqlite> <queue> <dedicated-uid>` profile now exercises local upstream transport. The existing forwarded/registry profile still rejects local-output leases. The new fixture denies FORWARD, uses a distinct output-grant set, evaluates a positive socket-UID match with unconditional denial fallback before fast-path acceptance in both OUTPUT hooks, and permits INPUT only for the reverse fixture tuple/interface/reply direction with a live output grant. Publication verifies the typed path, UID, interface and fixture generation. NDP exceptions are narrowly scoped trusted-link control traffic; this is not a production neighbor-security policy.

`scripts/network-gateway/probe-local-output.py --report <new-file> --admission-binary <published-AOT-path>` creates dedicated disposable resolver/other UIDs and an isolated simulated uplink. Its scope is **trusted infrastructure only** in the gateway namespace. It is not installed or used by Manager or the central resolver.

The live Fedora/WSL result passes six checks:

- Authorized TCP/UDP IPv4/IPv6 payloads arrive only after durable local-output admissions containing the resolver UID, hook, egress and namespace epoch.
- Root and the other UID fail all eight outbound attempts, with kernel actor-denial counter hits and no sink payloads.
- An extra UDP socket is closed and rebound under the other UID. It receives an old admitted reply, explicitly confirming **no hostile-UID receive isolation**. Its outbound send remains denied before the existing-flow fast path.
- After consumer death, four new authorized-UID flows fail through the queue path, while the four original admitted sockets continue through kernel grants. Explicit unused source ports avoid accidental tuple reuse. The admission count remains at its five-row baseline.
- Removing the output grant set stops both outbound and independently generated inbound payloads on the four original sockets.
- All created processes/accounts/mappings/namespaces are cleaned up without errors.

The first run exposed a harness assumption: a local OUTPUT drop can surface `EPERM` synchronously on UDP send. The bounded fixture client now reports send/receive socket errors; positive checks still require exact nonces and denial checks still require the relevant kernel counter plus sink silence. The failed run is preserved. Sixty focused C# tests pass on Windows and Linux, the Native AOT binary publishes/executes, and the twelve forwarding checks pass again after the client/namespace-helper changes. The architect reviewed and approved the checkpoint subject to these successful regressions.

This is **local upstream transport**, not DNS parsing/query policy, production OUTPUT supervision or receive isolation for mutually hostile local actors. Untrusted workloads must remain outside the infrastructure namespace. Actual generation ownership, DNS, alternate-uplink/stale-path denial and native Linux security/session validation remain outstanding. Gate 1 remains **in progress**.


### Bounded DNS wire groundwork (6 November 2026)

`DnsWire` adds a deliberately small fixture codec: one uncompressed ASCII LDH-hostname question, single A/AAAA address answers, exact ID/question/type/class matching, a 1,232-byte message bound, bounded TCP framing and a sixty-second TTL cap. It accepts only a direct pointer to the previously validated question or an uncompressed matching answer name; recursive compression pointers, extra records/trailing data, truncation and mapped IPv6 answers are rejected. Query type/class remain visible for later authorization, including unsupported types. Encoder and expected-question validation now use the same case/trailing-dot normalization.

Format references are [RFC 1035](https://www.rfc-editor.org/rfc/rfc1035.html), [AAAA records in RFC 3596](https://www.rfc-editor.org/rfc/rfc3596.html), and [DNS TCP framing in RFC 7766](https://www.rfc-editor.org/rfc/rfc7766.html). This fixture subset is intentionally narrower than general DNS interoperability: EDNS, CNAME chains, multiple answers, negative upstream responses, DNSSEC and connection-reuse semantics remain future work. Parsing an answer does not authorize its address or authenticate its origin.

Eighty-two focused tests pass on Windows and Linux. Coverage includes independent literal wire vectors, compressed/uncompressed answer names, every truncated prefix, mismatched IDs/questions, pointer cycles, header/count expansion, maximum labels/names, unsupported types, framing bounds, and bounded randomized input/response-mutation tests with failing-vector diagnostics. These are not exhaustive fuzzing. The architect reviewed the bounds and scope and approved the checkpoint after the Linux checks.

No DNS listener, query policy, resolver-configuration mount, upstream socket client, DNS-to-connect bindings or production behavior is enabled here. The DNS methods are not yet reached by the published AOT command; whole-prototype AOT publication alone would not establish execution of potentially trimmed methods. The eventual upstream client must bind replies to the expected remote endpoint and randomized outstanding transaction as well as using wire-level matching. Gate 1 remains **in progress**.


### Controlled malformed-input rejection correction (6 November 2026)

DNS transport negative tests exposed an exception-taxonomy gap: `InvalidDataException` is not covered by an `IOException` catch. The artifact verifier command and admission command now handle it explicitly, preserving fail-closed behavior through a controlled error result. Earlier artifact negative checks established nonzero exit/no verified plan, but did not distinguish a controlled exit from unhandled termination.

`scripts/network-gateway/probe-artifact-verifier.py --binary <published-AOT-path> --report <new-file>` makes that distinction executable and repeatable. The published Linux Native AOT command returns exactly zero for the valid plan and exactly one with no plan and the expected rejection diagnostic for modified bytes, an untrusted signer, malformed key, validly signed malformed JSON and oversized input. Ephemeral keys remain in a private temporary directory and are removed; the fixture disables core dumps for its Python process and descendants as an additional test-harness precaution, without changing the invoking shell's limits.

The architect reviewed the correction and the stronger verification requirement. This changes failure handling, not the authorization policy, trust provisioning, or deployment state. Gate 1 remains **in progress**.


### Coarse-owner DNS authorization and upstream core (6 November 2026)

The DNS core now evaluates an exact-name, fixed-answer fixture policy against a registered source/interface/namespace-generation identity, allows only IN A/AAAA, and records an `AuthorizedAttempt` synchronously before contacting upstream. Denials preserve the question name/type/class and observed client endpoint without inventing a requested external port. The client context is trusted listener metadata: source IP alone is insufficient, and the eventual kernel listener-ingress rules must bind it to the registered interface before this establishes owner identity.

A separate resolver-owned SQLite ledger uses an application marker/version, WAL/FULL commits, serialized writes and a configured retained-row limit. It refuses unrelated storage before journal/schema changes and remains capacity-limited across reopen. Request and outstanding-query limits also fail closed. The ledger is durable storage for a trusted resolver, not tamper-proof evidence against that resolver.

The upstream adapter opens a fresh connected literal-endpoint UDP/TCP socket per request. The coordinator chooses a cryptographic transaction ID, validates the outstanding question and fixed permitted answer address, records successful completion before returning an answer, and restores the client's original ID. TCP reads the complete bounded frame; UDP receives only from its connected endpoint. The coordinator directly awaits the cancellation-honoring adapter so a concurrency slot remains occupied through socket cleanup. The future listener must cancel and drain in-flight resolutions before disposing the coordinator or ledger.

Operational limits remain explicit: caller cancellation can leave only an authorized-attempt record; completion-audit failure returns SERVFAIL after upstream traffic already happened; synchronous SQLite locking/commits are not strictly interrupted by the asynchronous I/O deadline. No automatic retry, cache, production retention/rotation or DNS-to-connect permission is created. The fixed-answer policy is not general DNS interoperability.

One hundred ten focused tests pass on Windows and Fedora/WSL. Evidence includes independent-connection visibility before upstream I/O, a real SQLite writer lock preventing upstream contact, owner/interface/generation/name/type/class denials, pre-I/O and completion audit failure, forged/mismatched/unapproved answers, invalid-frame handling, cancellation, request/concurrency limits and persisted storage capacity. Real loopback tests exercise UDP/TCP over IPv4/IPv6, wrong-source-port UDP rejection, cancellation and oversized/incomplete TCP/UDP replies. The published AOT artifact verifier's exact rejection checks pass separately; the unused DNS core is not yet an executed AOT DNS path. The architect reviewed and approved the core and these limitations.

No DNS listener, kernel query-ingress/reply permission, runtime resolver-config mount or helper lookup path is enabled yet. Those remain the next integration proof, followed by direct-resolver bypass and service-failure checks. Gate 1 remains **in progress**.

### Bounded DNS socket listener checkpoint (6 November 2026)

`DnsResolverListener` connects the query coordinator to explicit-address UDP and TCP sockets. A fixed worker pool and one shared capacity limit bound queued plus active requests across both transports. Saturated UDP datagrams are dropped; saturated accepted TCP connections are closed. Oversized UDP datagrams are discarded before parsing. TCP handles one complete bounded frame and closes after the response; this does not claim general connection reuse or rejection of every trailing pipelined byte.

The I/O deadline includes time since socket acceptance/datagram receipt, prefix/body reads, resolution and response writes. Synchronous durable-audit work retains the earlier deadline limitation. Accepted sockets and capacity slots have explicit ownership until successfully transferred to a worker. Caller cancellation or an unexpected worker/producer failure cancels sibling loops and drains request resources before `RunAsync` returns. The owner must await that result before disposing the listener, coordinator and ledger.

One hundred twenty-four focused tests pass on Windows and Fedora/WSL. New real-socket cases exercise IPv4/IPv6 UDP/TCP resolution with client ID restoration and audited peer context, denied names without upstream contact, oversized UDP followed by a valid exchange, slow TCP prefix expiration, partial prefix/body disconnect recovery, shared UDP/TCP saturation, cancellation draining an active upstream request, invalid TCP frame lengths, and unexpected resolution failure closing an idle accepted connection and propagating the terminal error. The architect reviewed and approved the final checkpoint after socket-ownership and deadline corrections.

The configured interface and namespace generation remain **trusted configuration**, not metadata observed by these listener sockets. The future root-owned kernel boundary must restrict the explicit address to its registered source/interface and exclude alternate-interface and local-origin queries before that configuration can establish owner attribution. These loopback tests prove socket behavior, not that network boundary. The listener is not yet reachable from the AOT command, installed services, Manager or rootless helpers. Kernel query/reply permissions, resolver configuration, AOT DNS execution and bypass/failure integration remain next. Gate 1 remains **in progress**.

### Isolated DNS ingress and upstream integration (6 November 2026)

Two fixture-only commands now connect the published Native AOT executable to the DNS path. Root runs `probe-dns-admission <ktng-namespace> <audit.sqlite> <queue> <dedicated-uid>` to install the isolated kernel profile and consume admissions. The root fixture launches `probe-dns-resolver <ktng-namespace> <dns.sqlite> <core|work>` under its dedicated unprivileged resolver UID. The resolver refuses root execution. Root selects the coarse owner while attaching its registered source namespace; clients cannot choose an identity through DNS payloads. A root-owned fixture copy of the executable and native SQLite dependency permits unprivileged execution without exposing the private build tree.

The IPv4 profile redirects only the exact registered source/interface and designated gateway DNS address from port 53 to port 2053. INPUT repeats source/interface and original-destination checks before assigning a conntrack label. Client replies require the resolver UID, reverse tuple, reply direction and client label in both OUTPUT hooks. The label belongs to a separate conntrack field and never authorizes upstream traffic. Resolver upstream sockets use the existing typed OUTPUT admission path, exact simulated endpoint, durable admission ledger and timed kernel mark set. FORWARD and unrelated INPUT/OUTPUT remain denied. This is a trusted-infrastructure namespace, preserving the previously demonstrated UDP receive-isolation limitation.

`scripts/network-gateway/probe-dns.py` uses separate disposable core/workload owners sequentially, with their registered source namespaces, and a third resolver UID. The simulated upstream independently observes both a new durable query authorization and the matching durable network admission before responding. Its alternate DNS endpoint is exercised successfully before enforcement, so later denial is not inferred from an absent service. Each denied-name test requires zero upstream events; forged-answer tests require an actual upstream event plus a `ResponseRejected` decision, rather than accepting any SERVFAIL as proof.

The first live attempts exposed and corrected a terminal-payload alignment bug in the NFQUEUE codec. The [Linux v6.18 NFQUEUE builder](https://raw.githubusercontent.com/torvalds/linux/v6.18/net/netfilter/nfnetlink_queue.c) appends the final zero-copy payload without requiring tail alignment padding. The decoder now permits only a top-level terminal `NFQA_PAYLOAD` whose declared length exactly consumes the remaining region. Partial padding, declared truncation, duplicates, other unpadded attributes and nested conntrack alignment remain rejected. Three alignment-residue vectors and malformed negative cases exercise the correction. The original failure closed the consumer; failed fixture reports are retained.

Ingress diagnostics also prevented a misleading spoofing result: the initial `.3` test address was the broadcast address of the fixture's `/30`; the client reported that bind while packet capture showed the registered `.2` wire source. The corrected test uses a unicast `.9` address and requires its actual numeric ingress packet alongside the denial counter and unchanged query/upstream records. Reverse-path filtering is disabled only inside the disposable gateway to ensure the spoof and wrong-interface attempts reach the explicit nftables guards. A retained client tuple is replayed through the other interface to test guards after a label already exists.

The final published AOT run passes all seven check groups for both coarse owners. Each owner records twenty-two DNS decisions and seven upstream network admissions. Twelve kernel-denial cases include direct listener access, the previously reachable alternate resolver, local queries from root/the owner/the resolver UID, wrong-interface replay and a captured unicast spoof. Removing the consumer is preceded by an explicit live-process check and a fixture-only conntrack reset verified empty; TCP and UDP each increase the queue counter separately while admission/event counts remain unchanged. Graceful resolver shutdown exits zero, after which neither transport returns a response or generates upstream traffic. Cleanup reports no errors.

One hundred twenty-eight focused C# tests pass on Windows and Fedora/WSL. Eighteen Python checks pass on Linux; eleven pass and seven Linux-only checks skip on Windows. The twelve forwarding/kernel checks and six trusted local-output checks pass again, including the existing receive-isolation diagnostic. The same final published executable/native-library hashes remain unchanged through the final DNS fixture and both owners' observed, signed TLS acquisition positive/corruption/consumer-absence regression. The architect reviewed and approved this checkpoint after the corrections and successful live evidence.

This remains an isolated coarse-owner fixture. IPv4 transport carrying AAAA answers does not prove IPv6 DNS transport. Runtime resolver-configuration mounts and real helper lookups, general DNS interoperability, DNS-to-connect bindings, actual generation supervision, restart reconciliation, per-connector DNS identity, native Linux session/security parity and installed appliance integration remain outstanding. Gate 1 remains **in progress**; application enforcement is not enabled.

### Managed-owner and pasta resolver checkpoint (7 November 2026)

`scripts/network-gateway/probe-helper-dns.py` now exercises real libc `getaddrinfo()` under each managed owner and in fresh rootless pasta containers. The fixture attaches root-owned resolver, NSS and hosts files to each fresh `user@UID.service` using private read-only mounts. It resolves logical destination symlinks before creating the unit, then verifies the effective files and read-only mount entries inside the manager and workload namespaces with explicit namespace/root selection and target-identity checks. The WSL resolver symlink and its shared target are inspected and hashed; neither is replaced or edited. Original host resolver/NSS/hosts contents and symlink targets remain unchanged after both owners and teardown.

The private NSS policy uses `hosts: files dns`, the hosts files cannot satisfy the test names, and resolver environment overrides are removed. Known host resolver/system-bus socket paths are masked while the private user bus remains available. A root-owned Unix-socket canary is reachable under each owner before masking and unreachable from the managed view afterward. Actual connection attempts test the named endpoints; path existence alone is not treated as denial. This establishes the tested path masks, not exhaustive host IPC isolation, abstract-socket coverage, or safety against retained descriptors and other service proxies. Masking individual resolver socket files also avoids hiding a directory that could contain a native Linux resolver-file symlink target.

The offline C workload requests explicit IPv4/IPv6 answer families without `AI_ADDRCONFIG`, using absolute test names. Containers retain Podman's generated resolver file rather than receiving a manually substituted one. In the tested Podman 5.8.9 configuration, that file contains pasta's synthetic `169.254.1.1` plus central `10.231.251.1`, with one-second/one-attempt options. The real helper's observed arguments include `--dns-forward 169.254.1.1`; no explicit `--dns-host` argument was observed. Container NSS and hosts files are read from the live container. Root records the actual helper UID, network/mount namespaces, root and arguments while the workload remains alive. Its initial pre-sandbox file reads are not asserted to have been inspected; successful centrally observed traffic supplies the behavioral evidence.

Both owners pass seventeen phases with fourteen real workload invocations each. Each records twenty DNS decisions, four upstream events and four network admissions. Positive A/AAAA lookups precede negative cases in both execution environments. Own unapproved names and the other owner's name require central denial records and zero upstream activity. Direct alternate-DNS attempts require the kernel's forwarding-denial counter to increase. The admission consumer must be live before intentional removal; a fixture-only conntrack reset is verified empty, and fresh owner/container lookups increase the queue counter from four to five to seven while network admissions remain at four and no upstream event is added. Graceful resolver shutdown is followed by fresh failed lookups whose query-ingress counter increases without new DNS decisions or upstream traffic. Per-phase counters and audit records are retained in the reports.

Twenty-one unprivileged Python checks pass on Linux; twelve pass and nine Linux-only checks skip on Windows. The existing managed rootless resource-limit, four-protocol pasta consumer-loss, and both owners' observed signed TLS acquisition positive/corruption/consumer-absence regressions all pass. The published AOT executable/native-library hashes remain unchanged through the final DNS fixture and those three regressions. Every live fixture reports successful cleanup. The architect reviewed and approved the final checkpoint and evidence.

This proves the tested managed-owner and pasta-container lookup paths with **coarse-owner attribution over IPv4 DNS transport**, including AAAA answers. IPv6 DNS transport, broader DNS interoperability/bindings, complete helper and host-IPC coverage, namespace-generation supervision, restart reconciliation, performance/overload, per-connector identity and native Linux PAM/SELinux parity remain outstanding. No installer or application service is activated by this checkpoint. Gate 1 remains **in progress**.

### Dual-stack DNS transport checkpoint (7 November 2026)

The direct DNS fixture now serves native IPv4 and IPv6 client sockets and uses a matching-family literal upstream socket for each listener. This distinguishes actual IPv6 DNS transport from merely returning an AAAA answer over IPv4. Exact family-specific source/interface, original destination, transport-port, direction and resolver-UID checks remain in place. Client conntrack labels still authorize only the resolver query/reply path; upstream traffic requires a separate durable admission and live kernel handle. Neighbor-discovery exceptions are limited to the fixture links, expected peer/multicast destinations, hop limit 255 and neighbor solicitation/advertisement types. They do not constitute general ICMPv6 or PMTU support.

The two listeners use an explicit **eight-request partition per family, sixteen total**, including queued and active requests, with eight coordinator slots per family and one serialized DNS ledger. This is not a freely shared sixteen-slot pool. `DnsListenerGroup` supervises one service lifetime: a listener fault or unexpected normal exit cancels its sibling, and the group awaits all draining before caller-owned listeners, coordinators and ledger are disposed. Unrequested cancellation becomes a controlled service fault; requested shutdown drains normally. Four focused lifecycle tests cover fault propagation, delayed resource drain, unexpected completion and caller cancellation.

Both coarse owners pass the live Native AOT fixture. The positive matrix requires all eight combinations of client/upstream address family, TCP/UDP and A/AAAA, including a durable flow lookup for the actual upstream destination address before its response. Each owner records forty-four DNS decisions, fourteen upstream network admissions and twenty-four kernel-denial cases. Four pre-enforcement alternate-resolver baseline events and fourteen admitted upstream events produce eighteen recorded server events per owner. Denied names produce no upstream activity; forged responses must be observed and recorded as `ResponseRejected`.

Wrong-interface replays and unicast spoof attempts are checked separately for both families, with numeric packet captures for each spoof source. Ordinary IPv6 positives run before any static neighbor setup. Only the adversarial phase pins the known gateway neighbor entries inside the disposable source/sink namespaces, ensuring an unrelated neighbor-discovery failure cannot masquerade as nftables denial. IPv4 reverse-path filtering is disabled only inside that disposable gateway for the same reason. An initial run used the wrong sink-interface name and stopped cleanly before the adversarial tests; its failed report is retained, and the corrected fixture uses the topology's actual `u0` interface.

Each of the four protocol/family combinations is tested independently after consumer loss: its queue counter must grow, DNS decisions must show `AuthorizedAttempt` then `UpstreamFailed`, upstream events must remain unchanged, and network admissions must stay at fourteen. After graceful resolver shutdown, each combination must fail with an observed client-input counter increase and no further DNS decisions or upstream events. Both owners report successful teardown.

One hundred thirty-two focused C# tests pass on Windows and Fedora/WSL; twenty-one Python checks pass on Linux, while twelve pass and nine Linux-only cases skip on Windows. Native AOT publication and isolated nftables transaction validation succeed. Managed-helper DNS, twelve kernel primitives, six local-output checks, and both owners' observed signed-acquisition positive/corruption/consumer-absence regressions pass using an unchanged published executable/native-library pair. The architect reviewed the source, final dual-stack report and regression evidence and approved this checkpoint.

The direct fixture establishes the tested dual-stack paths; managed-helper resolver configuration retains its separately proven IPv4 transport. Broader DNS interoperability, PMTU/extension/fragment handling, generation supervision, restart reconciliation, accounting and overload remain outstanding. Gate 1 also still requires coverage for alternate socket mechanisms and descriptors, including AF_VSOCK, io_uring-created sockets, alternate ABIs and inherited/passed descriptors; any candidate restrictions must preserve rootless privilege-mapping behavior. These have not been established by ordinary IPv4/IPv6 routing or named-socket tests. Per-connector identity and native Linux security-context validation remain required. Gate 1 remains **in progress**, with no installed application enforcement enabled.

### Joint-protocol test-port allocation correction (7 November 2026)

The first full push validation of the dual-stack checkpoint failed fourteen listener tests at TCP binding, despite successful focused Windows/Linux runs and live Linux fixtures. The test fixture requested a UDP-assigned ephemeral port and then bound TCP to the same number. Windows has different TCP and UDP exclusion ranges: a local reproduction bound UDP successfully on port 61579 while TCP returned socket error 10013 for both address families. The failed push evidence is retained; host exclusions were not changed.

The test fixture now tries at most thirty-two distinct explicit unprivileged candidates using the actual listener constructor, retaining both bound sockets on success rather than probing, closing and rebinding. Only access-denied and address-in-use failures are retried. Exhaustion remains a test failure with address-family, candidate-port and error diagnostics. Fixture construction failure also releases its coordinator/cancellation resources. Fixed-port service startup is unchanged. Two additional IPv4/IPv6 cases deliberately occupy TCP on the first candidate, require the failed pair attempt to be recorded, and complete a real DNS exchange on the retained replacement pair. One hundred thirty-four focused tests pass on Windows and Fedora/WSL. The architect reviewed and approved this test-only correction; full push validation remains required.
