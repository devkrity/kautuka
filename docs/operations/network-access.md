# Network Access Control (NAC) v7 Operations & User Guide

On SELinux-enforcing Linux development hosts, run `bash scripts/nac/install-dev-selinux-policy.sh` once before starting the development stack. The launcher selects the confined Envoy-specific domain so Envoy can reach Manager's authorization socket; Envoy uses the egress pod's SELinux level to access shared memory, and other containers keep their existing SELinux domain.

macOS NAC is experimental and unverified. A real Mac with Podman Machine is still required to verify its network authorization path.

CI run [43802273218](https://github.com/devkrity/kautuka/actions/runs/43802273218) passed the production rootless Podman/Envoy acceptance, C6, brake negative control, and Linux integration tests on source SHA `590fa249c4cc8bc30875d8fb2ba08b07bedd33c5`. Its acceptance report is available as that run's artifact. Policy-deny verification covers new connections; the isolated Fedora `--live` run remains a manual check.

> **Earlier verification:** CI run [43517837642](https://github.com/devkrity/kautuka/actions/runs/43517837642) on source SHA `06a4333` passed `--prod-run`, C6, and the full integration suite. Its unedited seven-scenario artifact is committed at [nac-v7-prod-report-06a4333.json](nac-v7-prod-report-06a4333.json); the report records Envoy 1.40.2, Podman 5.7.0, an Azure Linux kernel, and 7/7 scenarios including LAN grant traffic reaching a routed fixture and the C6 tamper control. It applies to that source SHA and runner environment; it does not establish exhaustive platform coverage or a physical-host LAN run. The earlier WSL2 report [nac-v7-production-rootless-run.json](../verification/nac-v7-production-rootless-run.json) remains a separate local run matching source SHA `97b66ca`; that report does not establish CI results or physical-host LAN behavior. Follow-up run [43518533011](https://github.com/devkrity/kautuka/actions/runs/43518533011) passed the v7-19 Linux-only test guard change.

This guide describes the architecture, approval workflows, operational procedures, and troubleshooting diagnostics for **Kautuka Network Access Control (NAC) v7**.

On Linux, existing Envoy, CoreDNS, and boundary containers are reused only when their recorded Podman create command matches the requested image and arguments and their resolved image ID matches the current image reference. This also detects a locally rebuilt boundary image under the same tag. Drift stops the egress pod, confirms every present member is inactive and the pod infra member is valid, then removes only the stale named container before recreation. Missing peer containers do not block replacement in a partially created pod. If pod state, stop, or removal cannot be confirmed, startup fails closed. Focused simulated Podman coverage checks image ID, arguments, and partial-pod recovery; this is not live Podman verification.

---

## 1. Architecture Overview `[Partially implemented; not end-to-end verified]`

During reconciliation, transient egress pod health or boundary verification failures close launch admission while Manager recreates and verifies the pod. A failed recovery is retried; a proven ruleset hash mismatch latches the incident and stops workloads. An unrelated closed gate still permits registry reconciliation without starting recovery. Focused tests cover these paths with a simulated Podman runner.

Container reuse compares immutable image IDs with or without Podman's `sha256:` prefix as the same SHA-256 digest. A different digest still requires replacement.

The production acceptance workflow includes a user-brake scenario for stopping the selected user's existing workload, leaving another user's workload running, rejecting a new launch while paused, and allowing a launch after release. A separate CI run with the workload-stop call disabled provides its negative control. Live verification of this new scenario is pending a successful CI run.

The Envoy authorization host rejects wildcard TCP bind addresses; the wildcard listener used for detection tests exists only in the test fixture.

### Windows native development path

On Windows, Manager configures the development Podman Machine with the host decision endpoint and a dedicated Envoy client certificate bundle, then exposes the Envoy authorization listener over TCP with mTLS. The egress pod is recreated on Manager startup so its host mapping and certificate mounts stay current; its infra allowlist and boundary rules are copied into the Podman Machine before verification. Linux continues to use the rootless Unix socket path. Windows-specific setup is guarded by the Windows platform check, and launcher readiness follows the effective `Kautuka__Transport` setting (TCP when selected, otherwise the configured socket). This path has focused argument and mocked Podman Machine tests; live Windows Podman Machine end-to-end verification is still needed. Native boundary verification captures the generated ruleset from `cat "$1"` before piping it to `nft -f -`; Fedora's SELinux-labeled `container_file_t` file cannot be opened by `nft`, and a plain pipeline could hide a failing `cat` and hash a truncated ruleset. The same command form is used through Podman Machine SSH. The NAC `hooks_dir` drop-in uses containers/common array append mode to preserve earlier custom hook directories. Append mode also applies to later fragments, so later `hooks_dir` arrays append instead of replacing the accumulated list; placing the NAC drop-in late in sort order reduces but does not eliminate that interaction.

On Linux, the development launcher stages the repository OCI hook, its adjacent nft ruleset template, and a uniquely named hook configuration in a private per-run directory. It passes a `containers.conf` override to the Manager process only; the override appends the temporary hook directory to the existing Podman hook directories, leaving unrelated hooks configured there active. The launcher removes the staged files after Manager's process group has stopped. This development path has a fail-first canary check for a checkout with no installed NAC hook and rejects an inherited `CONTAINERS_CONF_OVERRIDE` rather than replacing it. It does not detect or reconcile another installed Kautuka NAC hook: if one is also discovered by Podman, both hooks may run, and coexistence across different hook versions or state locations has not been verified. Use the isolated development setup without an installed Kautuka hook for this validation path. Connector-supplied host environment override handling is a separate, unresolved issue, so this launcher setup does not establish that every connector launch is protected by the development hook override.

> **Status note:** This guide describes the target NAC v7 design. A rendered template or passing unit test does not prove a live Podman topology. The hook and policy components have local tests, and the launch path includes post-start checks for the ruleset hash, `no-new-privileges`, IPv6 disablement, Podman-resolved capability sets (including null empty sets), and mount sources restricted to desired workspace roots matching the workload's registry user and agent; verifier tests cover the same agent ID under a different user, a sibling agent, IPv6 disablement, and a captured rootless Podman inspect fragment. The verifier reads `net.ipv6.conf.all.disable_ipv6` from the workload's network namespace: on Linux through rootless `podman unshare nsenter`, and on Windows or macOS through `podman machine ssh` into the Podman VM. Podman 5.8.9 omits `HostConfig.Sysctls` from inspect, so the namespace value is authoritative. The production rootless Podman and Envoy report records seven scenarios: internal-parser deny-all, public-address rebind filtering, LAN approval and fixture access, sandbox approval and rule persistence, reconcile deregistration, verifier tamper detection, and DNS upstream enforcement. Each scenario's negative control produced its expected result. The report matches the production-acceptance artifact uploaded by CI run 43517837642; one successful run does not establish exhaustive platform coverage. The canary uses the persisted system brake state for its `system` identity and requires a successful hook record for its own container ID and workload IP with a canonical hash matching the expected rendered workload ruleset. Rendered boundary, Envoy, and CoreDNS configuration files are published by atomic replacement. Reconcile hashes all three rendered files together and latches the existing topology incident containment path if any file changes after startup. Boundary nft output uses LF line endings so nft can parse it when the manager runs on Windows. On Linux, startup applies the generated boundary ruleset with the host nft binary in a disposable network namespace created through rootless `podman unshare`, hashes the native nft JSON, and compares it with both live boundary tables before opening the startup gate; reconcile compares live tables with that startup hash and closes the gate if they change. Reconcile also closes the gate if the persisted `boundary.nft`, `envoy.yaml`, or CoreDNS `Corefile` no longer matches the combined rendered configuration hash captured at startup. Startup and reconcile also require a successful exit code from the boundary init container, not only an exited state. Existing egress pods are checked for their recorded workload/outside addresses and forwarding sysctls before startup trusts them. The Envoy and CoreDNS create arguments use their pinned images' non-root defaults; tests ensure no root override is supplied. Startup and reconcile now reject a degraded or incomplete egress pod and keep the NAC launch gate closed. These checks have local Linux tests; only the scenarios listed in the production report are confirmed against live Podman so far. The other verifier tests and overlapping ephemeral-call test passed in the CI integration suite; they remain test-level evidence rather than live Podman connector runs. New connector and sandbox launches consult the startup gate and persisted brakes. Connector reconciliation checks the owning user's brake before a temporary image build or stale-container replacement; `V7_20_ReconcileLaunchChecksBrakeBeforeRecreate_WithNegativeControl` passes locally, and `V7_20_ReconcileHonorsBrakeBeforeConnectorImageBuild` checks the guard placement. Both passed CI run 43517837642. Connector, sandbox, and package-builder specs now carry the current persisted system/user brake state instead of hardcoding `IsBraked=false`; the Fedora suite passed 2,239 tests with 5 skips locally and passed CI run 43517837642; user-brake activation and release are covered by CI run 43802273218. If an emergency brake cannot stop or kill a workload, the registry retains its identity so authorization can still fail closed; this path has a local test but still needs live Podman proof. Isolated connector launches keep user/agent identity labels while filtering workspace mounts; internal system connectors keep `deny-all` egress even when an agent invokes them with user context. Focused tests cover agent and probe argument construction and the missing-user failure. The boundary image lookup resolves the repository Dockerfile from the build output directory and fails with a direct diagnostic if the build fails. The SHA-matched local report at `docs/verification/nac-v7-production-rootless-run.json` records E-rebind, E-parser, LAN grants, and a live internal connector launch through `CallToolAsync`. The CI brake proof covers the exercised user-brake stop and release scenarios. Treat the sections below as design and operator intent until a capability is explicitly marked **Proven** by a real Podman/nft acceptance run.

Sandbox and agent-file connector mounts are checked against the configured workspace base root from Manager desired state. Mount paths outside that root and paths traversing a symbolic link or reparse point are rejected before Podman arguments are rendered. Linux integration tests cover an escaping symlink and both production builders rejecting a workspace path outside the configured root; this remains unit-level evidence, not a live Podman mount test.

Sandbox registry recovery is fail-closed: Manager rebuilds registry entries with deny-all egress, then the desired-workspace supervisor resolves the trusted runtime image and verifies an existing sandbox's canonical container ID, current spec, workload IP, and post-start boundary before promoting that exact registry identity. Podman operations normalize raw gRPC workspace contexts from current desired state and the trusted runtime-image allowlist; launch and execution fail before Podman work if the workspace has been removed. Persistent and ephemeral connector launches also start with deny-all identities and promote only after post-start verification; ephemeral MCP initialization waits for verification. Focused tests exercise these paths with a fake Podman runner; live restart recovery and ephemeral connector startup are not yet confirmed. A connector's immediate startup network request may be denied until verification completes.

When Manager's topology startup gate is closed, its container `ext_authz` controller rejects new container authorization decisions with reason `topology_closed`; a gate-read failure is treated as closed. A live boundary incident also latches the gate closed for the current Manager process, unions registered workload IDs with `kautuka-workload` network members, skips only exact infra IDs from the validated allowlist, attempts `stop` then `kill` for active members, and inspects any candidate whose stop state remains uncertain. Launch leases cover sandbox and connector preparation, container creation, and verification/registration. An incident closes admission and cancels active launch tokens before scanning; it does not wait for potentially stalled MCP startup. A scan with active leases is incomplete and reconcile rescans, so a late materialized workload is included in a later pass. Failed enumeration, failed inspection, unconfirmed candidates, or surviving workloads are logged as critical and keep the gate closed. Incident state is process-local and is not persisted across Manager restart. The gate does not revoke established connections or gate Gateway/ChannelHost `CheckOutbound` decisions; a compromised kernel may bypass Envoy. This is not complete containment against a compromised kernel.

Agent sandboxes keep the hardened default UID `65532`; Manager mounts `/home/node` as tmpfs so that UID can traverse the home directory and create connector install paths such as `/home/node/.kautuka/connectors`. A real local Podman check confirmed the prior nested `/home/node/.kautuka` tmpfs was inaccessible to UID `65532` and the home-level tmpfs worked.

The Linux rootless Envoy authorization socket uses Envoy's pinned non-root image user and Podman's `keep-groups` option. The Manager creates the admin socket directory with group write and execute permissions; no dedicated service group is provisioned. CoreDNS's internal forwarder uses the resolver configured by Podman inside the egress pod; the boundary permits DNS only to the configured resolver addresses. Podman assigns the workload network's automatic and explicit default routes metric 1000 and the outside network's default route metric 100, so the egress pod sends external traffic through the outside gateway while workloads still route through the egress pod. Startup checks both network metrics and rejects drift. Focused Linux tests cover these settings. The production rootless report records the live route table and passing Envoy approval/rule, E-rebind, E-parser, and LAN-grant scenarios.

`PodmanContainerTool` records and labels an agent identity only when its context scope is `agent`; a non-agent scope cannot inherit its scope ID as an agent ID. Sandboxes without both a user and agent identity use `deny-all` egress and no attribution. Registry rebuild preserves explicitly restricted userless sandboxes, while rejecting userless normal-egress workloads. The post-start mount verifier matches the desired workspace by both agent and user, including a missing user. Focused tests cover sandbox identity and registry rebuild using generated labels and a fake Podman runner; they do not prove live Podman restart behavior. Connector container fingerprints are derived at runtime from the resolved user/agent identity, execution context, connector image, and compatibility profile. The desired-state record does not infer an owner from the connector ID. A Linux integration test verifies that reconciliation inspects and preserves the generated IsolatedWithAgentFiles container name for its desired user and agent, using the same fingerprint calculation as launch. This proves the reconciliation identity path with a fake Podman runner; it does not prove live Podman connector reconciliation.

Production Manager startup and `--prod-run` share the `InitializeProductionAsync` entry point. It enables the boundary's `lan_approved` rule so approved LAN connections routed through Envoy can pass the kernel boundary. The boundary init container assigns the LAN source address to the interface carrying the outside pod IP, rather than assuming an interface name. Integration tests check the hosted startup rendering and acceptance wiring; the recorded rootless Podman acceptance report separately proves a LAN approval reaches its fixture.

Gateway writes explicit network privileges for user creation and role changes. Startup adds role defaults only when a user has no saved privilege record; it preserves saved choices and records any backfill as the `system` actor. A failed Manager sync during a role change restores the previous Gateway role and returns `503`; a failed startup lookup or backfill prevents Gateway startup. Reading an implicit admin default does not write the policy file. Linux integration tests cover reopened-store startup behavior, backfill audit attribution, promotion and demotion, and rejection of a demotion when Manager is unavailable. These are local test results, not CI or live-service evidence.

When a connector's MCP process exits with a generic shutdown error, Manager appends up to the last 100 lines of that container's Podman logs, capped at 8 Ki characters. A Linux integration test exercises this fallback with a fake Podman runner; live Podman diagnostics have not been verified.

Registry rebuild processes each live container inspection independently, so malformed inspect output is logged and skipped without preventing later valid workloads from being registered. A local Linux integration test covers this failure isolation; it does not prove live egress topology initialization.

On Manager restart, restored workloads remain outside the normal registry until the post-start verifier confirms their inspected security profile and live ruleset hash. Compatibility profiles are resolved from `podman image inspect` metadata for the container's immutable image ID, using approved repository digests or the inspected image digest for local builds. A failed verification is quarantined in the synchronized `manager-desired-state.json` store and removed by inspected canonical container ID; startup aborts if containment cannot be established or if both quarantine persistence and removal fail. Reconcile re-hashes active workload rules every existing 30-second tick. Persisted quarantines are checked before verification and surviving canonical IDs are retried from the same `ps` snapshot without registry adoption. Transient removal failures keep the registry entry deny-all and close launch admission; missing canonical identity also denies the existing entry and closes admission without latching a topology incident. Focused v7-53/v7-54/v7-55 integration tests use simulated Podman command runners and local state files; they do not constitute live Podman restart or 30-second timer validation.

An opt-in rootless Podman test exercises failed containment of a real disposable workload, reopens its persisted quarantine, and verifies removal during a new registry rebuild. It recreates the Manager recovery component rather than restarting the full Manager process. Quarantine cleanup uses a one-second Podman stop timeout within its five-second command limit so Podman can force removal before the command is canceled.

When post-start verification fails, Manager changes a matching in-memory registry entry to `deny-all`/`none` before cleanup. It uses the immutable 64-character container ID returned by Podman inspect for kill, force-remove, and any follow-up inspection. This pins cleanup to the inspected identity after inspection; it does not independently prove that the inspected container is the one launched if a name was replaced before the inspection. A confirmed successful force-remove permits deregistration; a nonzero exit, exception, or unconfirmed cleanup keeps the identity quarantined. If inspection did not provide a canonical ID, Manager does not act on the reusable container name; the workload may remain running, and its deny-all registry entry is retained because termination cannot be confirmed safely. Persistent connector and ephemeral connector cleanup paths must not erase that entry on verifier failure. Persisted quarantine and restart verification are described above; a full Manager process-restart test remains outstanding.

When replacing or stopping an inspected connector, Manager removes the inspected container ID rather than its reusable name. If the ID is missing or malformed, cleanup is deferred. Focused tests cover the replacement-name race and missing IDs; these tests use a simulated Podman runner.

The U8 Envoy LAN address-filter test compares against a literal expected CIDR list, and the default egress boundary ruleset has a static golden. The workload nft text golden and embedded JSON template share literal rule-comment and key-address checks. Local Linux mutation runs caught an incorrect LAN exclusion, a missing terminal drop, and an incorrect Envoy listener port. These are renderer tests; they do not prove that a live Podman topology applied the ruleset.

Kautuka NAC v7 enforces a strict **default-deny outbound network posture** for untrusted containerized workloads (Agent Sandboxes, MCP connector containers, and build-time tools), while providing unified application-layer enforcement and caller attribution for first-party host services (`Gateway`, `ChannelHost`).

For container traffic, rule changes apply when a new connection is authorized. Changing a rule does not itself terminate an already-established connection.

The container controls described here apply only to workloads created and managed by Kautuka Manager; they do not govern unrelated containers running in the same Podman installation.

```mermaid
flowchart LR
  subgraph HOST["Host Processes (Native .NET)"]
    GW["Gateway"]
    CH["ChannelHost"]
    subgraph MGR["Kautuka.Manager"]
      NAC["Kautuka.NetworkAccessController<br/>(Policy, Authorizer, Matching, Registry, Request Store)"]
      LST["Envoy ext_authz Listener<br/>(Linux: UDS, Windows: TCP+mTLS)"]
    end
    UI["Web UI -> Approvals & Rules"]
  end

  GW -->|"CheckOutbound (gRPC)"| NAC
  CH -->|"CheckOutbound (gRPC)"| NAC
  UI --- GW

  subgraph PODMAN["Rootless Podman Runtime"]
    subgraph WL["kautuka-workload (10.247.0.0/16 --internal --disable-dns)"]
      W1["Workload Container<br/>(10.247.1.5, cap-drop ALL, ro-rootfs)"]
    end
    subgraph EG["kautuka-egress Pod"]
      INIT["Init Container<br/>(boundary.nft)"]
      ENV["Envoy Proxy v1.40.2<br/>(:15001 original_dst + tls_inspector)"]
      DNS["CoreDNS<br/>(:53 sinkhole -> 198.18.250.250<br/>:10053 internal forwarder)"]
    end
  end

  W1 -->|"Outbound TCP via default route"| INIT --> ENV
  W1 -.->|"DNS queries (:53)"| DNS
  ENV -->|"ext_authz Authorization/Check"| LST
  ENV -->|"Approved egress"| NET["Internet"]
  HOOK["OCI createRuntime hook<br/>(kautuka_hook.py)"] -.-> W1
```

The package proxy runs at `10.247.0.3` on the workload network. Package clients resolve its name to a reserved service address and reach it through Envoy; Manager policy controls the connection. The proxy’s own upstream requests also pass through Envoy and Manager policy. Windows local development has been checked with a PyPI index request and a `uv` package install; this does not establish production or Linux live acceptance.

### Key Architectural Pillars

1. **Centralized Static Egress Pod (`kautuka-egress`)**:
   - Workload containers run on an internal, isolated network (`kautuka-workload`, `10.247.0.0/16`) with default route `0.0.0.0/0 via 10.247.0.2` and DNS server `10.247.0.2:53`.
   - The egress pod is multi-homed between `kautuka-workload` and `kautuka-outside`, operating with `net.ipv4.ip_forward=0` and `net.ipv6.conf.all.disable_ipv6=1`.
   - Envoy `v1.40.2` acts as a transparent L4/L7 forward proxy listening on `0.0.0.0:15001`. It uses `original_dst` and `tls_inspector` listener filters to inspect TLS Server Name Indication (SNI) or destination IP addresses.
2. **Synthetic DNS Sinkhole**:
   - CoreDNS inside the egress pod listens on `10.247.0.2:53` for workload queries, returning a fixed synthetic IP (`198.18.250.250`) for all `A` queries and empty `NOERROR` for `AAAA` queries. DNS is never forwarded to external resolvers from the workload side.
   - Envoy resolves upstream domains via its internal loopback listener (`127.0.0.1:10053`), which forwards directly to host/system resolvers. This completely prevents workloads from sending forged SNIs to arbitrary IPs.
3. **OCI Runtime Hook (`scripts/nac/kautuka_hook.py`)**:
   - Podman triggers `kautuka_hook.py` at the `createRuntime` stage for every container.
   - The Linux installer requires the packaged hook payload and fails with a direct error if it is missing; the hook is installed executable at the path configured for rootless Podman.
   - Enforces atomic `table inet kautuka_workload` nftables rules in the container's network namespace:
     - Drops all IPv6 traffic (`ipv6_drop`).
     - Drops source-IP spoofed packets (`spoof_drop`).
     - Drops inter-container lateral traffic (`wl_net_drop`).
     - Drops link-local and cloud metadata addresses (`meta_drop` for `169.254.0.0/16` and Azure `168.63.129.16`).
     - Drops outbound UDP (`udp_drop`).
     - Permits outbound TCP to `10.247.0.2:15001` and DNS to `10.247.0.2:53`. Package traffic follows the Envoy path; the proxy listener accepts connections from the egress pod and is not directly accessible to ordinary workloads.
   - Manager allocates a distinct workload IP before launch. Normal connectors require a user identity and use the agent identity when invoked by an agent; save-time discovery has no agent. Missing user identity or address fails the start instead of falling back to a shared system identity or a fixed IP. Internal system connectors remain `deny-all` whether invoked without a user or through an agent. Catalogue scope determines systemwide connector availability; an agent invocation uses the invoking user and agent for network decisions, as with other connectors. The agent ID used for policy is separate from the workspace ID used for filesystem paths.
   - A persistent connector started with an explicit command is registered as deny-all until its post-start verification succeeds; Manager then promotes the verified container identity to its intended policy.
   - The warm container for an explicit-command connector uses `tail -f /dev/null` as its entrypoint. Connectors with no explicit command use their image's normal entrypoint.
4. **Host Services Application-Layer Enforcement**:
   - `Gateway` and `ChannelHost` outbound HTTP and WebSocket clients fail closed (`FailClosedNetworkAccessChecker` / `checker_missing`) when constructed without an explicit `INetworkAccessChecker` or `NetworkAccessWebSocketChecker`. Non-ChannelHost callers must carry an explicit `NetworkAccessContext` user identity (never defaulting missing users to `"system"`), failing closed (`unknown_source`) when unattributed and enforcing per-user brakes across OIDC link, OAuth, AI provider discovery/probes, and catalog downloads. Denials surface directly into agent runs via Narad Protocol without opening approval cards for automated system seeds.
5. **Policy State Durability (`NetworkAccessPolicyStore`)**:
   - Approvals, revocations, user privileges, and emergency brake state flush synchronously (`SaveSync`) with directory fsync, while pending-request creation and retry attempt updates coalesce on a background writer ordered by a monotonic mutation sequence (`inMemoryMutationSeq`) with deterministic LRU eviction so older background snapshots cannot overwrite newer synchronous saves. Failed synchronous writes leave the previous in-memory state active and return the persistence error. Background write failures are logged and retried with exponential backoff.
6. **Decision Audit Log (`NetworkAccessAuditRing`)**:
   - The bounded background JSONL writer waits up to 5 ms for queue capacity so ordinary decisions do not wait indefinitely for disk I/O. Decisions still queued when that limit expires are summarized by an `audit_loss` record containing the dropped count and earliest/latest event timestamps. A pending loss summary is written during disposal even when no later decision arrives.
   - If the audit file cannot be written because of an I/O or permission error, Manager emits an error through its normal logger. Network decisions continue, and the unavailable audit file cannot preserve the lost entries.

---

## 2. User & Approval Workflows `[Implementation status varies; see status note above]`

### 2.1 Approval Cards

Unapproved network connections produce requests for the requesting user and agent or connector. A user with permission to manage their own network access can approve or block their requests; an administrator can decide requests for other users. A rule can apply to the platform, a user, a user and agent, or a user and connector. Only administrators can create platform rules. Explicit blocks win across all applicable scopes. See [Network access matching](network-access-matching.md) for the matching rules and examples.

A connector save performs discovery with the signing-in user's identity and no agent. Disabling an existing connector skips discovery. When discovery creates a network approval request, the connector can be saved with its tool list still unknown. After approval, Test connection can retry discovery. Test connection itself still uses a temporary probe; its temporary decisions end with the probe.

### 2.2 User Network Permission

Administrators can set one permission: whether a user can manage their own network access. When disabled, their requests require administrator approval and rules they created are paused until the permission is restored. Administrator-created rules remain active. The old separate all-agents permission is not used by matching or approvals. Pre-release NAC rules and saved privilege records are reset when the new policy version is first loaded; brakes and user accounts are preserved.
### 2.3 Administrator Grants
- **Open Internet Grant (`any-public`)**:
  Administrators can grant open internet access to isolated containerized connectors (`scopeKind = user`, `destKind = any-public`, `port = 0`). This allows public internet egress while non-public IPv4 ranges (`NonPublicV4`) remain strictly enforced by kernel boundary rejection. Manager derives execution context and attribution from its workload registry, desired-state snapshot, and recorded request history; it does not trust caller-supplied context flags. If Manager cannot establish an enforceable context for a connector, it rejects an `any-public` rule with `scope_not_enforceable`.
- **LAN Grants (`allowLan`)**:
  When LAN grants are enabled, connections to RFC1918 private subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) route through Envoy's dedicated `dfp_lan` / `orig_dst_lan` clusters bound to the egress pod's routable outside address (`10.248.0.3`). The boundary init container assigns this address to the outside interface, Envoy and CoreDNS's infrastructure allowlist entries include it, and the kernel boundary rule `lan_approved` permits traffic from it while blocking access to `10.247.0.0/16` and metadata. The boundary hook allows only the exact fresh set `{10.247.0.2, 10.248.0.2}` or the exact restart set `{10.247.0.2, 10.248.0.2, 10.248.0.3}`; Envoy and DNS remain pinned to their exact three-address sets. A production rootless Podman and Envoy run is recorded in `../verification/nac-v7-production-rootless-run.json`: seven scenarios passed, including an approved request reaching a LAN fixture, with a negative control for each scenario. The report records a local run; it does not establish CI results or exhaustive platform and policy coverage. The acceptance harness removes only a namespace or veth whose creation command returned success. If a creation command times out after the kernel applied it, the result is ambiguous, so that resource may be left behind; cleanup deliberately avoids deleting a possibly pre-existing resource. Cleanup is best effort, continues after a cleanup-command exception, and preserves the original fixture setup error.

### 2.4 Emergency Brake
- **User Emergency Brake**: Immediately pauses network access for all containers owned by the user, stops running workload containers, and blocks new container creation.
- **System Emergency Brake (Admin Only)**: Halts all workload network egress across the entire instance and shuts down all running agent containers. Rules remain intact and are restored upon releasing the brake.

### 2.5 Image Compatibility Profiles
Third-party containerized MCP tools that cannot operate under hardened security defaults may be granted an admin compatibility profile keyed by image digest (`sha256:...`). The profile selectively relaxes:
- `writable-rootfs`: Permits a writable container filesystem.
- `root-user`: Permits execution as container UID 0.
- `file-ownership-caps`: Grants `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETUID`, `SETGID`, `FSETID`.
Network isolation, OCI hook firewall rules, and approval card requirements remain strictly enforced.

---

## 3. Operator Procedures & Diagnostics

### 3.1 Inspecting Hook State & Logs
The OCI runtime hook logs execution events to `$XDG_STATE_HOME/kautuka/nac/hook.log` (default: `~/.local/state/kautuka/nac/hook.log`). If appending the next record would take the active log over 5 MiB, it is rotated first; up to three previous logs are retained as `hook.log.1` through `hook.log.3`. A single record larger than 5 MiB can itself make the active log exceed that threshold. On Linux, the log and its rotation lock are created with mode `0600`.

When an ephemeral connector's MCP transport closes unexpectedly, Gateway includes up to the last 100 Podman log lines in the failure details when its container ID is available. Returned log text is capped at 8,192 characters.

During post-start verification, an ephemeral connector with normal egress can reach only the package proxy. Its normal network policy takes effect after verification; explicitly deny-all workloads remain deny-all.

Testing a saved local connector updates its shared tool definitions only when the caller can manage that connector and supplies no custom test environment values. Other authorized users can still test it without changing the shared definitions.

Runtime connector workloads require both a user and an agent identity. Connector tests and save-time discovery may run without an agent and use the user's connector scope.

Connector catalogue availability does not change the network workload fingerprint.

Network decisions use the applicable platform, user, agent and connector rules; an explicit block takes precedence over a probe's temporary allowance.

Inspect the latest hook decisions:
```bash
tail -n 20 ~/.local/state/kautuka/nac/hook.log | jq .
```
Each log line records:
```json
{
  "ts_ns": 1731301200000000000,
  "duration_ms": 32.45,
  "id": "kautuka-mcp-user-agent-conn-hash",
  "pid": 48210,
  "hook_version": "7.0.0",
  "ips": ["10.247.1.15"],
  "netns_inode": 4026532890,
  "action": "allow",
  "reason": "workload_rules",
  "rules_sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "annotations": {
    "io.kautuka.role": "workload"
  }
}
```

### 3.2 Inspecting Network Namespace Ownership
The netns owners registry prevents container namespace hijacking:
```bash
cat ~/.local/state/kautuka/nac/netns-owners.json | jq .
```
Permissions must be strictly `0600` and owned by the running user. If this file is corrupted, the hook refuses container starts with `owners_unreadable`.

### 3.3 Verifying Firewall Rulesets

#### Boundary Table (Egress Pod)
Verify the boundary firewall inside the egress pod:
```bash
EGRESS_PID=$(podman pod inspect kautuka-egress -f '{{.InfraContainerID}}')
nsenter -t $EGRESS_PID -n nft list table ip kautuka_egress
```
Expected chains:
- `prerouting`: redirects TCP to port 15001.
- `forward`: policy drop.
- `input`: accepts port 15001 and port 53; drops all other incoming packets.
- `output`: contains `private_reject`, `wl_net_reject`, and optional `decision_endpoint` / `lan_approved`.

#### Workload Table (Agent / Connector NetNS)
Verify the workload firewall inside a running container:
```bash
CONTAINER_PID=$(podman inspect -f '{{.State.Pid}}' <container_id_or_name>)
nsenter -t $CONTAINER_PID -n nft list table inet kautuka_workload
```
Verify the following rule comments are present:
- `input_drop`
- `ipv6_drop`
- `spoof_drop` (verifying `ip saddr != <WORKLOAD_IP>`)
- `wl_net_drop`
- `meta_drop`
- `udp_drop`
- `final_drop`

### 3.4 Validating Rendered Configurations

These commands validate files that have already been rendered. The checked-in templates and renderer tests do not prove that a live Podman topology applied the files.

#### Envoy Static Configuration
Validate the rendered Envoy bootstrap configuration using the official Envoy container:
```bash
podman run --rm \
  -v /path/to/rendered/envoy.yaml:/etc/envoy/envoy.yaml:z \
  docker.io/envoyproxy/envoy:v1.40.2 \
  --mode validate -c /etc/envoy/envoy.yaml
```

#### Boundary NFTables Configuration
Validate the rendered boundary ruleset:
```bash
nft -c -f /path/to/rendered/boundary.nft
```

### 3.5 Live Container Acceptance `[Historical Linux CI: production 7/7]`
The NAC acceptance workflow runs `--prod-run` against rootless Podman on Linux with `nf_tables` and `crun`; its production path includes the C6 post-start verifier and its tampering negative control. On commit `123200a`, CI run [43493361422](https://github.com/devkrity/kautuka/actions/runs/43493361422) passed topology startup, the canary, C6, and six of seven production scenarios, but `PROD_LAN_GRANT` did not receive the host fixture response after approval. The `lan_approved` counter incremented, and the observed Envoy route used the production outside-network gateway. Commit `4bbeb24` keeps the approval match on `10.248.0.3` and adds postrouting SNAT to Podman's assigned outside address `10.248.0.2`; CI run [43502037962](https://github.com/devkrity/kautuka/actions/runs/43502037962) showed that rootless Podman cannot connect directly to the runner's own physical LAN address (curl exit 7). Commit `424636b` moves the fixture to a separate routed network namespace connected by a veth pair on `192.168.248.0/24`. After fixing fixture teardown, CI run [43507042937](https://github.com/devkrity/kautuka/actions/runs/43507042937) passed the real production rootless Podman/Envoy acceptance (7/7, including `PROD_LAN_GRANT`) and the full integration suite. The workflow now retains the unedited production report as an artifact so it can be committed with its source SHA.
These are historical CI results for the listed commits. The normal no-LAN-grant request is denied by Envoy and is expected to produce no fixture hit or LAN masquerade-rule packet. The egress private_reject counter is exercised separately by the existing public-approved rebind negative control after the Envoy private-address filter is removed; that modified-filter result is distinct from the normal no-grant path. Contract tests check source wiring and expected observation fields; they do not prove a live kernel counter result. The production acceptance ran on commit `7aae8ef17c5b0781b30101746d99f31af99ed125`: [CI run 43634775317](https://github.com/devkrity/kautuka/actions/runs/43634775317) passed 8/8 scenarios, including the normal denial and the modified-filter kernel control. The no-SNAT observation is based on the tested masquerade-rule counter and fixture log; it does not independently measure a translated source address.

The production acceptance uses `rebind.<fixture-ip>.nip.io` and `lan.<fixture-ip>.nip.io` names so its existing configured resolver can return the routed fixture address without editing the rendered CoreDNS file after Manager startup. The Linux runner must be able to resolve nip.io; the harness logs the expected and observed address and fails with resolver details if that dependency is unavailable. For its positive `dns_upstream` proof, it creates a unique per-run `dns-proof-<guid>.<fixture-ip>.nip.io` destination, gives the existing workload an explicit allow-LAN rule, clears UDP/53 conntrack entries for every Corefile forwarder, and samples the live nft counter before and after a real workload request through Envoy/CoreDNS to the routed fixture. A zero conntrack deletion count is accepted only when conntrack reports no matching established entry; the subsequent unique lookup must still return the fixture body and increment `dns_upstream`. The separate allowed `example.com` response body remains independently checked. The intentional Envoy private-filter-removal negative control runs only after normal workload launches and reconcile checks finish; it then disposes the Manager reconcile lifecycle while leaving the already-running workload and the separate Envoy authorization host active. The acceptance auth host uses the production startup gate during normal checks and bypasses it only after disposal for this packet-path control; the control does not test production-gated authorization. The separate `NetworkAccessTopologyGateTests.EvaluateContainerConnection_DeniesNewAuthorizationWhenTopologyGateIsClosed_WithNegativeControl` test covers startup-gate denial. The final DNS-upstream negative control refreshes its nft rule handle and private-reject baseline immediately before deleting the rule, after the earlier Envoy filter control has completed. The updated harness passed all eight live scenarios and the integration suite in [CI run 43655492446](https://github.com/devkrity/kautuka/actions/runs/43655492446) on source commit `c72a5d2b81a4c45ec8a6ba037952b4b4573de015`.

The in-memory startup gate is authoritative for Envoy authorization and launch admission. Its best-effort `startup-gate.status` diagnostic write happens after releasing the gate-state lock so slow filesystem I/O does not delay authorization decisions.


The non-Linux Podman Machine verifier command is covered by an integration test that forces the machine path and rejects a partial ruleset; that path has not been exercised against a live Podman Machine in CI.

The production acceptance runner stages Podman cleanup only after `podman info` confirms the private graphroot and runroot and reports rootless mode. Run `Kautuka.NetworkAccess.Acceptance -- --self-test-isolation-cleanup` to exercise the actual production proof with a fake Podman response; the test verifies that a failed isolation check causes no Podman cleanup calls.

---

## 4. Troubleshooting Hook Refusal Reasons `[Built]`

When container startup fails with exit code 126 or 1, check the `reason` field in `hook.log`:

| Refusal Reason | Cause | Operator Action |
| :--- | :--- | :--- |
| `missing_pid` | Container state passed by OCI runtime contains no PID or PID <= 0. | Verify container runtime health (`crun` / `runc`). |
| `no_ip` | Container has no IPv4 interface or network namespace setup failed. | Check `podman network inspect kautuka-workload` and Netavark status. |
| `missing_allowlist` | `infra-allowlist.json` does not exist when starting a NAC container. | Restart `Kautuka.Manager` to regenerate the infrastructure allowlist. |
| `bad_allowlist_mode` | `infra-allowlist.json` permissions allow group or other access (must be 0600). | Fix permissions: `chmod 600 ~/.local/state/kautuka/nac/infra-allowlist.json`. |
| `bad_allowlist_owner` | `infra-allowlist.json` is not owned by the current user. | Fix ownership: `chown $USER ~/.local/state/kautuka/nac/infra-allowlist.json`. |
| `infra_ip_mismatch` | An allowlisted infra container has IP addresses differing from its registered allowlist entry. | Re-create the infra pod/container with the expected static IP. |
| `workload_off_network` | A container annotated with `io.kautuka.role=workload` is not attached to `10.247.0.0/16`. | Workload containers must only be attached to `kautuka-workload`. |
| `owners_unreadable` | `netns-owners.json` contains corrupt JSON, bad mode, or wrong owner. | Stop workloads, remove corrupt `netns-owners.json`, and restart Manager. |
| `reserved_range` | Container assigned an IP in reserved infra block `10.247.0.0/24`. | Check `WorkloadAddressAllocator`; workload IPs must start at `10.247.1.0`. |
| `bad_workload_ip` | Workload has multiple IPs, broadcast address (`.255`), or an IP outside `10.247.1.0-10.247.255.254`. | Verify IP allocation pool and Podman network subnet settings. |
| `shared_netns` | Container attempted to attach to a network namespace already owned by another active container. | Ensure workload containers use private namespaces and avoid `--net=container:`. |
| `apply_failed` | Applying `table inet kautuka_workload` via `nsenter` failed or ruleset verification failed. | Check kernel `nf_tables` module support and inspect `dmesg`. |

---

## 5. Outbound Redirect Policy (`v7-34`)
- `NetworkAccessCheckHandler` enforces `AllowAutoRedirect = false` on inner `SocketsHttpHandler` / `HttpClientHandler` instances and Gateway/ChannelHost `ConfigureHttpClientDefaults`.
- Any HTTP `3xx` `Location` target is also evaluated against `INetworkAccessChecker` and rejected with `NetworkAccessDeniedException` if unapproved rather than auto-followed silently.
- Chat tool-failure fallback lookup (`ChatToolNetworkAccessHelper`) queries Manager `ListRequests` as the current non-admin user (`IsAdmin = false`) and matches non-systemwide pending requests owned by the same `(userId, agentId)`. When several requests match, it reports the one with the newest valid `LastSeenUtc`; Linux integration coverage checks both the selected destination and request ID, including a negative control with the ordering removed.

## 6. AI Provider Endpoints (v7-37)
- There is no built-in localhost or Ollama exception. Gateway calls to a provider endpoint are denied until an admin grants a rule.
- If the address is not already allowed, **Test connection** asks the administrator for temporary access to the provider's host and port. The allowance is limited to that test and ends when it finishes or after 30 seconds, whichever comes first; testing creates no persistent rule.
- Saving a provider ensures a `system`-scope allow rule exists for its host and port, regardless of test results. Saving replaces a matching systemwide block; loopback and private IPv4 provider addresses receive a LAN grant. If Manager cannot be reached, the save is rejected.
- The temporary test uses the Manager outbound policy check with the test's identity; its completion operation removes the allowance. These changes have focused tests and have not yet been proven by a CI run.

## SELinux infrastructure config mounts

The egress boundary, CoreDNS, and Envoy generated configs are each stored in dedicated host directories and bind-mounted read-only. Linux adds Podman's shared `:z` relabel option so SELinux-enforcing hosts can read them; atomic replacement writes the new inode within the mounted directory. Windows Podman uses read-only mounts without SELinux relabeling. Envoy's config directory mounts at `/etc/kautuka-envoy`, separate from `/etc/envoy/certs`, which remains the Windows mTLS certificate mount.

At startup, Manager recognizes the expected directory mount. A stale file mount is migrated by stopping the egress pod once, verifying the pod and every member are inactive, then removing only the affected named infrastructure container. Failed or ambiguous inspection, stop, or removal fails closed. Integration tests cover DNS and Envoy migration, Windows and Linux mount arguments, and failure controls. A disposable Fedora enforcing-mode check on 2026-11-14 verified the boundary's `:ro,z` behavior and atomic replacement using a local image and temporary directory; it did not exercise the full Manager stack or validate the DNS and Envoy mounts live.

For stale boundary mount migration, a known `Degraded` pod is stopped before replacing its boundary init container. Replacement proceeds only after a fresh pod inspection reports a stopped pod, all expected members exactly once, unique member IDs, and no active or unknown member state; the reported `InfraContainerID` must match the listed infra member's `Id`. Members in `created`, `exited`, or `stopped` state are inactive. Inspection failures and malformed or mismatched membership fail closed. The Fedora incident snapshot had a degraded pod with a running infra member and other members in `exited`/`created` states; this targeted recovery logic has a fake-Podman integration regression, not a live Fedora restart validation.

Connector compatibility profiles are matched against inspected immutable image identifiers. Materialized local image tags are resolved before launch, and restart adoption uses the same image identity when checking the profile.
