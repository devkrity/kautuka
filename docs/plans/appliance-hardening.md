# Appliance hardening

**Status:** Active

**Owner:** Appliance

**Activation condition:** The openobserve credential and loopback-publish fixes are immediate — they close a measured exposure and carry no open design question. The Unix domain socket transport change required approval first, because it forecloses splitting services across machines and must retain mTLS to keep that option recoverable; it was approved and has now landed, and `Kautuka:Transport=tcp` keeps the split-deployment option recoverable. See [landed](#landed-unix-domain-socket-transport).

**Scope:** Defects in the WSL appliance image, its network exposure, and its image supply chain. Residency and host-side lifecycle are a separate plan — see [Windows appliance residency](appliance-residency.md).

Every "measured" claim below was observed on a live distro on WSL 2.9.4.0 / kernel 6.18.35.2-1. The network-exposure and published-image findings come from `test distro`, imported from the GitHub-published tarball. The socket-transport and update-path findings come from appliances built by `scripts/local-release.sh` and imported fresh, and from a native install on Ubuntu 26.04; each is labelled where it matters.

## Network exposure

**Target: nothing on the appliance is reachable from outside it except `20229`, plus `22` which is deliberately retained for operator and remote debugging access.**

**A firewall cannot deliver this, and the reason is structural: the appliance has no network namespace of its own.** All running WSL2 distros share one. Measured three ways:

- `test distro` and `Podman-managed VM` both report `eth0` as `<private-network-address>/20` — the same address, not merely the same subnet.
- `ss -lntp` inside `Podman-managed VM` lists the appliance's `127.0.0.1:20000`, `127.0.0.1:20229`, `0.0.0.0:20001`, `*:20002`, `*:20003`, `*:20101` and `*:20102`, all without process attribution because the PID namespaces differ while the network namespace does not.
- From `Podman-managed VM`, `127.0.0.1:20002` and `127.0.0.1:20229` both connect. A control probe of its own sshd on `127.0.0.1:60966` also connects, so the harness is sound.

Two consequences follow, and both are decisive.

**Loopback is not private to the appliance.** Any WSL2 distro the user runs — their Fedora dev box, an Ubuntu install, the podman machine — can already reach every appliance service on `127.0.0.1`. Binding to loopback buys nothing against that. mTLS is the only thing standing in front of the four gRPC services, and openobserve has neither mTLS nor unique credentials.

**An `nftables` ruleset "in the appliance" is a machine-wide change.** It lands in the shared namespace, so a default-deny would govern the user's podman machine and every other distro. That is precisely the collateral damage the residency plan rules out for `.wslconfig`, and it applies here for the same reason. The NAT-mode test did confirm the rules *work* — `eth0:20001`/`:20002`/`:20003` went from `OPEN` to blocked with 8 packets on the fall-through counter — but it was closing those ports for every distro, not just ours.

The firewall is also blind to the path that matters most. WSL's localhost forwarding delivers host traffic to the distro's **loopback**: probing `127.0.0.1:20001` from Windows incremented `iif "lo"` by 6 packets / 328 bytes and nothing else. Inter-service gRPC and the WebHost→Gateway proxy also ride `127.0.0.1`, so `iif lo accept` is mandatory and cannot distinguish the two.

**Therefore Unix domain sockets are the mechanism, not a follow-up.** There is no bind address private to the appliance, so no `Listen` change can scope these services to it. A socket in the appliance's own filesystem is invisible to other distros, which have separate mount namespaces and separate VHDs. `20229` must stay TCP because a Windows browser has to reach it, and it therefore remains reachable from other distros — that is the one residual exposure, and it is the authenticated front door rather than an internal control plane.

An earlier draft of this section recommended binding everything to `127.0.0.1`. That was wrong twice over: it does nothing about other distros, and measured behaviour is that WSL forwards IPv4 listeners but not IPv6 dual-stack ones, so `20002`/`20003`/`20015` were *less* reachable from Windows as `*` than they would have been as `127.0.0.1`. That asymmetry is inferred from behaviour, not configuration — `sysctl` is unavailable in the appliance, so `bindv6only` was never confirmed, and it must not be relied on.

### Mirrored networking is worse than a port conflict

Measured by setting `networkingMode=mirrored` and restarting:

- **The appliance becomes entirely unreachable, including `20229`.** Every probe timed out from both `127.0.0.1` and the LAN address. The cause is the Hyper-V firewall, whose `DefaultInboundAction` is `Block` on this machine.
- **`podman machine` on Windows fails to start at all**: `machine did not transition into running state: ssh error: machine is not listening on ssh port`, and `podman ps` then fails with `dial tcp 127.0.0.1:60966: connectex: No connection could be made`. Podman relies on WSL's NAT port forwarding to reach its VM's sshd, and mirrored mode removes it. So the damage to the user's existing podman happens before any appliance interaction.
- The appliance's `eth0` becomes `<private-network-address>/24` with the home router as its default gateway — it takes the host's LAN address directly.
- `wsl --shutdown` to apply the change reproduced the `user@997.service` EBUSY leak, leaving the appliance `degraded`, which is consistent with [this image predating its own fixes](#the-published-image-predates-its-own-fixes).

This upgrades the [drift detection](appliance-residency.md#host-configuration-drift) entry for `networkingMode` from reasoned to measured, and corrects its stated mechanism: the appliance does not fail because ports collide, it fails because inbound is blocked at the Windows layer. It also justifies treating the key as breaking for the *host's* sake and not only the appliance's.

**`60966` is identified and is not ours.** It is `Podman-managed VM`'s sshd, visible from inside the appliance because of the shared namespace. There is nothing to close.

Measured exposure today:

| Port | Bind | Owner | From Windows | From another distro | Target |
|---|---|---|---|---|---|
| 20229 | `127.0.0.1` | WebHost | yes (intended) | **yes** (measured) | stays TCP — the one intended entry point |
| 22 | `0.0.0.0` + `[::]` | sshd | yes | yes | **keep** — retained by decision for operator and remote debugging |
| 20000 | `127.0.0.1` | Gateway | likely, untested | **yes** (measured) | Unix socket — **landed**, no TCP listener remains |
| 20001 | `0.0.0.0` | Manager | **yes** (measured) | **yes** (measured) | Unix socket — **landed**, no TCP listener remains |
| 20002 | `*` | CredentialsHost | no | **yes** (measured) | Unix socket — **landed**, no TCP listener remains |
| 20003 | `*` | ChannelHost | no | **yes** (measured) | Unix socket — **landed**, no TCP listener remains |
| 20004 | `*` | UpdateService | no (untested) | likely, untested | Unix socket — **landed**, no TCP listener remains |
| 20015 | `*` | openobserve via `rootlessport` | no | likely, untested | **superseded** — moved to `127.0.0.1:20215` |
| 20215 | `127.0.0.1` | openobserve via `rootlessport` | yes (intended OTLP) | **yes** (shared netns) | **landed** — loopback-only; away from host-Podman `20015` |
| 20101 | `*` | verdaccio via `rootlessport` | untested | **yes** (measured) | **landed** — host unpublished; `kautuka-pkgs-proxy` listens `20101` on the sandbox alias only |
| 20102 | `*` | proxpi via `rootlessport` | no | **yes** (measured) | **landed** — host unpublished; retired sidecar, orphan-GC on reconcile |
| 60966 | `0.0.0.0` + `[::]` | `Podman-managed VM` sshd | n/a | n/a | **not ours** — shared-namespace artefact |

**The four gRPC services are wide open by deliberate choice, and only in prod.** Manager is explicit about it:

```83:88:src/backend/Kautuka.Manager/Program.cs
    KautukaHostMode mode = KautukaHostPathPolicy.Resolve(context.Configuration).Mode;
    if (mode == KautukaHostMode.Prod)
        options.Listen(System.Net.IPAddress.Any, grpcPort, listenOptions =>
```

Dev binds loopback and prod binds every interface, which is inverted. CredentialsHost, ChannelHost and UpdateService use `ListenAnyIP` unconditionally.

To be accurate about severity: all four require mutual TLS with client-certificate validation against a local CA, so this is attack-surface reduction and not an open door. The sharper remaining item was **openobserve on `*:20015` with the public ZincObserve demo credentials** — that publish is now loopback-only on `20215`, and the demo pair is gone from `sidecars.json` / `KautukaTelemetryDefaults`. **Per-install generated credentials are deliberately deferred** (OTLP defaults are compile-time across every host; rotating away from the public demo pair is the accepted bar for now).

**sshd is retained by decision.** It is enabled and active on `0.0.0.0:22` with no consumer in the repository, and it is kept deliberately for operator and remote debugging access. The accepted tradeoff is that `22` stays externally reachable and must remain key-only with no root login, as the image already configures it.

Nothing inside a container needs to reach the package proxy on the host. Sandboxes and connectors reach it by container-network alias (`kautuka-pkgs-proxy:20101`). Host TCP `20101`/`20102` stay forbidden. OpenObserve stays host-published on loopback so systemd services can send OTLP to `127.0.0.1:20215` and Manager probes `GET /healthz` there. The package proxy stays unpublished and is built from a local HEALTHCHECK wrapper under `SidecarAssets/pkgs-proxy`; Manager waits for Podman `healthy` (falling back to `running` if an image has no health config). Admin diagnostics read cache inventory through Manager `podman exec … proxy stats -json` (`GET /api/runtime/diagnostics/pkgs-proxy/stats`); that payload is not stored and the git-pkgs UI/`/npm`/`/pypi` listener is not reverse-proxied. MCPB language-package install (`npm`/`pip`/`uv`) is `podman run --network kautuka-sandbox` plus `podman commit`, not a host-netns `podman build` `RUN`. Bake asserts the staged catalog seal and packed socket/drop-in files; Manager logs warn-only host-exposure findings after sidecar reconcile. Sidecar-role orphan GC removes retired catalog names (`kautuka-verdaccio`, `kautuka-proxpi`) on reconcile.

**Work**

1. ~~Move Gateway, Manager, CredentialsHost, ChannelHost and UpdateService onto Unix domain sockets.~~ **Landed** — see [below](#landed-unix-domain-socket-transport).
2. Keep `20229` on TCP and treat it as the authenticated front door, since a Windows browser must reach it and it is consequently reachable from other distros.
3. ~~Publish the openobserve port on `127.0.0.1` and replace the ZincObserve public demo credentials.~~ **Landed** — loopback on `20215`; demo pair rotated. Per-install generation deferred (not required for this seal).
4. ~~Unpublish the verdaccio and proxpi host ports.~~ **Landed** — later replaced by one unpublished `kautuka-pkgs-proxy` on container listen `20101`; host `20101`/`20102` remain forbidden.
5. ~~Add a bake-time assertion on the packed tarball that the intended socket and port set is what ships.~~ **Landed** — staged `sidecars.json` + wrapper Dockerfiles; packed `kautuka-sockets.conf` + `user@` restart drop-in.
6. ~~Add a startup assertion that measures actual reachability rather than trusting bind addresses.~~ **Landed** — warn-only `ApplianceHostExposureAssert` after sidecar reconcile (Linux `ss` + socket paths).

**Rejected: an in-appliance `nftables` ruleset.** It works, but it is not appliance-scoped — the shared network namespace means it would govern the user's podman machine and every other running distro. Verified policy is recorded here only so the option is not re-litigated from scratch: `policy drop` on input, accepting `iif lo`, `ct state established,related`, `tcp dport 20229`, `tcp dport 22` and ICMP. No podman bridge rule is needed, because rootless podman keeps containers in a separate `rootless-netns` and proxies through userspace, and WSL's DNS proxy `<private-network-address>` is bound to `lo`. If a firewall is ever revisited, the Hyper-V firewall on the Windows side is the correctly scoped place for it.

**Non-goal.** None of this mitigates `networkingMode=mirrored`, which blocks all inbound at the Windows layer and independently breaks the user's podman machine. That key stays covered by [drift detection](appliance-residency.md#host-configuration-drift).

### Landed: Unix domain socket transport

Gateway, Manager, CredentialsHost, ChannelHost and UpdateService now bind Unix domain sockets at `/run/kautuka/<service>/<service>.sock`. mTLS is unchanged and still authenticates every connection, so this reduced attack surface without swapping one control for another. `KautukaHostNetworkPolicy` is the single seam and Manager's inverted prod/dev bind divergence is gone. `Kautuka:Transport=tcp` restores the previous loopback addresses, which is how a split deployment or a PowerShell smoke test that cannot supply a `ConnectCallback` opts out.

Verified on a fresh import of an appliance built by `scripts/local-release.sh`: `systemctl is-system-running` = `running`, all five sockets bound, all six services active at `NRestarts=0`, **nothing listening on TCP `20000`–`20004`**, `GET /` = `200` and `GET /api/health` = `401` — the `401` being the proof that WebHost's `/api` proxy reached Gateway over the socket and Gateway's own auth answered.

**The socket file's permissions are not the directory's, and that is the whole trap.** Connecting to a Unix socket requires *write* permission on the socket file, not merely traverse on its directory. The socket is created under the service's `UMask=`, which the units set to `0077` (Gateway, ChannelHost) and `0027` (CredentialsHost) to protect state and logs — so a bound socket came out owner-only and **every** cross-service hop was refused, surfacing as `502` on `/api`. Only `UpdateService → Manager` worked, because both run as `kautuka`. **Windows does not enforce these bits**, so the same build passed there; this is only findable on Linux, and it is why the appliance and native Linux are not optional verification targets.

The fix is two halves, and neither works alone:

- The socket directories are **setgid** (`2750`, owner `<service>`, group `<service>-client`), so `bind()` gives the socket the dedicated client group rather than the service's own primary group. Group inheritance from a setgid directory does apply to sockets — measured, not assumed.
- Each service sets its socket to `0660` once Kestrel has bound it, via `IHostApplicationLifetime.ApplicationStarted`, which is the first point the file is guaranteed to exist.

Together these give `srw-rw---- <service>:<service>-client`, with the `0750` directory remaining the real access gate. That is why the socket is `0660` rather than world-writable: the setgid group makes the tight mode possible. Measured on a fresh import, unaided — all six required hops permitted, and both negative controls (`ChannelHost → CredentialsHost`, `CredentialsHost → Gateway`) still denied.

Client-group membership is deliberately *not* the service's primary group. Adding Gateway to `kautuka-credentials` would have granted it CredentialsHost's private files as a side effect of wanting socket access, so each service has a separate `<service>-client` group holding only traversal rights.

Provisioning is `systemd-tmpfiles`, not `RuntimeDirectory=`. Manager, UpdateService and WebHost are user-scope units, whose `RuntimeDirectory` lands under `/run/user/<uid>` at `0700` and is therefore unreachable by the system-scope services — measured. `tmpfiles` provisions both scopes uniformly. The three system units run `ProtectSystem=strict`, so each needed `/run/kautuka/<service>` added to `ReadWritePaths=` or the bind failed read-only.

Manager and UpdateService get `kautuka-manager-client` and `kautuka-updateservice-client` for the same reason, with Gateway the only member. Both run as `kautuka`, so their sockets could have been left in that group — which Gateway already belongs to — but that group also carries their group-readable state under `/var/lib/kautuka`, so using it as the socket gate would have widened Gateway's reach well past the two sockets it needs. UpdateService still reaches the Manager socket as its owner.

Verified identically on a **native Linux install** (Ubuntu 26.04, `kautuka-install-linux.sh`): all five sockets `srw-rw----` under their dedicated client group, all six units active, first-admin setup and login `200`, Gateway → UpdateService `200` reporting `Current`, and nothing on TCP `20000`–`20004`. Two Ubuntu-specific installer defects surfaced there and are recorded under [hygiene](#hygiene).

**Updates work between socket releases; retrofitting onto a pre-socket appliance does not.** Measured: a socket appliance updated `…111147` → `…999200` through discovery and apply, came back `Current`, and kept every socket and group intact. That works because the appliance was *baked* with socket provisioning. `scripts/kautuka-apply-release.sh` deploys binaries into `versions/<id>` and flips `current`; it writes no unit files, no tmpfiles configs and no group memberships. Worse, `/usr/local/lib/kautuka/apply-release` is installed only at install and bake time and is never refreshed by an update, so the helper running any update is always the one from the *currently installed* release. No change to that script can retrofit socket provisioning onto an appliance already in the field — those must be re-imaged. The retrofit half remains reasoned from the scripts; only the socket-to-socket path is measured.

### Landed: discovery no longer offers a release it cannot install

Apply enforces `ReleaseTrustPolicy` on every release URL — https, or http only on loopback — but discovery checked none of them. A feed published over plain http on a LAN address was therefore advertised as `Available`, and apply then failed with `Platform release signature URL must be https or loopback http`, leaving the appliance permanently offering an update it could never install. Measured against a local feed on `http://<private-network-address>:20300`, and the failure recurred on the *signature* URL after the manifest URL was corrected, because the rejected URLs are the ones signed **inside** the manifest.

Discovery now applies the same rule to the URLs it can see and marks such a release `Blocked` / `UntrustedSourceUrl`, carrying the exact rejection in `LastError` so the reason is visible without presenting an apply that is certain to fail. The check runs only when the release is otherwise eligible, so an up-to-date appliance still reports `AlreadyCurrent` rather than a URL complaint. `local-release.sh` also warns at build time when `--public-base-url` is non-loopback http, since it defaults to the host's LAN address and so produced this state by default.

The artifact URL apply finally downloads can come from the platform release descriptor, which discovery has not fetched, so that one is still only checkable at apply.

## The published image predates its own fixes

**The `TimeoutStopSec=5` and `Restart=on-failure` drop-ins exist in the build scripts but are in no published artifact.** Measured: the imported appliance's `user@997.service` has only `10-login-barrier.conf` and `10-timeout-abort.conf`, and the EBUSY cgroup leak reproduced verbatim —

```
user@997.service: Failed to spawn executor: Device or resource busy
user@997.service: Failed with result 'resources'.
```

— with OpenObserve teardown logged immediately before it, as recorded in the [residency testing notes](appliance-residency.md#testing). That boot came up `degraded` with Manager, UpdateService and WebHost all down while `is-system-running` still reported healthy on the system units.

Recovery behaved exactly as documented: `reset-failed` + `start` took 300 ms, after which every service was listening within 234 ms.

**The drop-ins do fix it once they actually ship.** A fresh import of an appliance built from these scripts reports `systemctl is-system-running` = `running` with **zero** failed units in both the system and the `kautuka` user scope, across repeated imports. So this is purely a republish, not an open defect — the fix exists and is verified, it is only absent from the published artifact.

**Work**

1. Rebuild and republish the appliance so the drop-ins actually ship.
2. ~~Add a post-import smoke assertion that both drop-ins are present, so an image without them cannot pass release verification.~~ **Landed** in bake (`assert_packed_appliance_network_seal`).

## Image supply chain

Digest is identity; authorization is the ECDSA-signed platform release. `ContainerImageProvenancePolicy.EnsureTrusted` still checks naming (`@sha256:<64 hex>` or `localhost/`), and first-boot infrastructure images (sidecar admit, remote sandbox pull, MCPB base) must also match `runtime-images.json`. That file is copied into the Manager payload (hashed in the signed platform tarball) and listed as `runtimeImages` on the signed platform release. There is no cosign, no `podman image trust`, and no GPG verification. Production Manager fails closed if that allowlist file is missing. User connector images are still digest-only until package admission.

Appliance bake mirrors first-boot blobs into `/var/lib/kautuka/oci-images` (`podman save`). Manager loads an archive before pulling. Sidecar upstreams stay digest-pinned. The sandbox base does not. Each release builds `kautuka-base` from `quay.io/fedora/fedora-minimal:latest`, tests the tools the sandbox contract needs, and pushes `ghcr.io/devkrity/kautuka-base` tagged with that release. The signed platform release records that digest as allowlist id `kautuka-base`. The appliance runs that image for the agent sandbox and for node, Python, and uv connectors, including a stored `kautuka-base:latest` tag or a `quay.io/fedora/fedora-minimal` pin. A connector save made while Manager is not listening yet is kept and published by the startup retry. Node and `uv` stay sha256-verified inside the image build. Local wrappers stay `localhost/kautuka-*:local`.

**Work**

1. ~~Digest-pin all three sidecar images and the sandbox base.~~ **Superseded for the sandbox.** Sidecars stay digest-pinned. `kautuka-base` is built from Fedora `latest` on the release runner. Node and `uv` tarballs stay checksummed.
2. ~~Extend `EnsureTrusted` to cover sidecars and the sandbox base image.~~ **Landed** for sidecar admit and remote sandbox pull; local `kautuka-base` builds stay `localhost/`.
3. ~~Decide whether digest-pinning is the intended root of trust or a placeholder for signature verification, and record it.~~ **Landed:** digest is identity; Kautuka ECDSA platform release / `runtime-images.json` is authorization; mirrored archives are availability. Package-admission remains the later path for untrusted MCP images. Cosign is not the Kautuka root. **Note (2026-09-23):** Quay garbage-collected the Fedora index digest and the appliance rebuild died on `manifest unknown`. Fedora `latest` is only the release-build input. The digest the appliance runs is `ghcr.io/devkrity/kautuka-base` for that platform release.
4. ~~Verify the Node tarball checksum in the sandbox Dockerfile and the MCPB runtime layer.~~ **Landed** for the sandbox Dockerfile and the MCPB system layer (same sha256). `uv` 0.11.19 is checksummed in that same image build. A local `kautuka-base:latest` rebuild can still install `uv` through `kautuka-pkgs-proxy`.

## Network readiness and isolation

First boot requires outbound network access to fetch runtime dependencies and container images. The appliance needs bounded retries and clear diagnostics when that access is unavailable.

WSL2 distributions can share host-forwarded loopback reachability. Binding a service to loopback is not, by itself, an isolation boundary between distributions. Keep authentication and firewall policy in place, and validate reachability on supported Windows and WSL versions. The original machine-specific addresses, port measurements, process observations, and Podman traces are intentionally omitted.

## Hygiene

Low risk, but each one costs someone an hour of confusion:

- Two installer defects found only on Ubuntu 26.04, both now fixed. `newuidmap`/`newgidmap` ship with `cap_setuid`/`cap_setgid` file capabilities *alongside* the setuid-root bit, which caps their effective set and makes rootless Podman fail with `newuidmap: open of uid_map failed: Permission denied`. And Ubuntu ships uutils coreutils, whose `/usr/bin/test` disagrees with the kernel about supplementary groups: the installer's `runuser -u <account> -- test -w …` probes reported failure for paths the account could demonstrably write. The second was the more dangerous of the two, because the same probes back negative assertions — "Gateway can read ChannelHost private material" fails the install when the probe *succeeds*, so a probe that wrongly reports failure made those isolation checks pass without testing anything. The probes now run through the shell builtin rather than whatever `test` binary is installed.

- Repo-root `config.json` assigns gateway `20001`, manager `20000` and OTLP `20006`, all contradicting `KautukaHostNetworkPolicy`, and references a `kautuka-core/` layout that no longer exists.
- Integration tests set `Kautuka:HostPaths:VisibleRoot`, `:InternalStateRoot` and `:LogsRoot`; `KautukaHostPathPolicy` never reads any of them. They pass because they also set `Kautuka:Mode=dev` and `Kautuka:ContainerStateRoot`.
- `src/backend/Kautuka.Gateway/NativeAssets/sqlite-vec/README` states the binary is intentionally not committed. `win-x64/vec0.dll` is committed.
- `RunTraceView.openObserveUrl` is declared in the frontend and rendered as an "Open in OpenObserve" link, but no backend code populates it, so the link never appears.
- Prod mode hardcodes `/var/lib/kautuka`, which resolves to `C:\var\lib\kautuka` on Windows. `Kautuka:ContainerStateRoot` covers most state but `LogsRoot` and `VisibleRoot` have no override, and both `UpdateServiceRuntimePaths` and `WorkspaceRuntimePaths` unconditionally create the logs root.

## Windows-side secret protection

Not appliance-internal, but adjacent and currently live: every secret-at-rest protection is a POSIX mode call behind `if (!OperatingSystem.IsWindows())`. CredentialsHost already publishes and passes a smoke test as `win-x64` in CI, so anything running natively on Windows today stores its vault, machine key and password-less PKCS12 client certificates with inherited default ACLs, and the Linux setgid-based separation between the four service accounts has no counterpart. Needs a Windows ACL or DPAPI equivalent.
