# Windows appliance residency

**Status:** Active

**Owner:** Installer

**Activation condition:** The mechanism below is accepted and the elevation constraint is resolved. The decisions in [Decisions](#decisions) are settled.

## Outcome

An installed appliance is reachable at `http://127.0.0.1:20229` from system boot, without an interactive logon, without a stored credential, and without the user knowing that WSL is involved. Residency survives `wsl --shutdown`, podman machine restarts, and host-level `.wslconfig` edits made for unrelated reasons — recovering by itself where it can, and telling the user precisely what is wrong where it cannot.

## Why a scheduled task and not a service

The mechanism is forced by measured WSL behaviour; the evidence is recorded in the [testing notes](#testing) and is not restated here. In short:

- A `LocalSystem` service cannot work at all. WSL refuses it outright (`Wsl/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED`), and SYSTEM has no `Lxss` registration to act on.
- A service running as a user account would work, but the SCM needs a credential — either the user's password or a dedicated account whose password the installer generates and stores.
- A Task Scheduler task with an `S4U` principal gets the same non-interactive, runs-whether-logged-on-or-not behaviour with **no stored password**, and is available on Home, where `secpol`/`gpedit` are not.
- Residency itself requires a process under a `Relay(<pid>)` parent, which only a host-initiated `wsl.exe` invocation produces. Nothing inside the image can do it, so this must live on the Windows side.

Verified by reboot: an `AtStartup` + `S4U` task fired 15–28 s after boot with no interactive logon present, and took the distro from `Stopped` to `Running`.

## Design

**Trigger.** `AtStartup`, plus a repetition interval (see [Decisions](#decisions)). `AtStartup` alone is not sufficient: it fires once, so any later `wsl --shutdown`, Podman machine restart, or lost keepalive leaves the appliance down until the next boot.

**Principal.** The installing user, `LogonType = S4U`, `RunLevel = Limited`.

**Registration.** `Register-ScheduledTask -LogonType S4U` (or the equivalent Task Scheduler COM/`ITaskService` call). `schtasks /ru <user> /np` must not be used: it prompts for a password and blocks even with `/np`. Registration requires elevation, which the installer already holds via `requireAdministrator`.

**Action.** Invoke the staged unelevated `Kautuka.Monitor.exe` with `--supervise`, rather than invoking `wsl.exe` directly or the elevated installer. The binary is extracted beside the staged installer at `%LOCALAPPDATA%\Kautuka\WSL\.installer\`, which survives after the downloaded installer is deleted. Routing through Monitor keeps the pass idempotent and loggable instead of encoding shell behaviour in a task argument. The installer still accepts `--supervise` and calls the same pass, so the verb does not diverge.

The verb is a short-lived *ensure* pass, not a resident process. That is deliberate: a long-running supervisor would need its own crash recovery, and the only credible way to restart it is a scheduled task — so the task may as well be the supervisor. Each firing does three things and exits.

**1. Ensure residency, idempotently.** A repeating trigger would otherwise accumulate one `sleep infinity` per firing:

1. Ensure the distro is running with a cheap exec.
2. Count existing keepalive processes inside the distro.
3. Start a keepalive only when the count is zero, using `wsl -d <distro> -u root --exec /usr/bin/setsid /usr/bin/sleep infinity`.

Step 1 doubles as shutdown detection. A stopped distro is not an error state to report — the exec starts it, which is the whole point. What *is* worth recording is the transition, because repeated unexplained restarts are the signal that something on the host is tearing WSL down.

**2. Detect `.wslconfig` drift.** See [Host configuration drift](#host-configuration-drift).

**3. Report.** Write the outcome to a rolling log beside the staged installer, and raise a Windows Event Log entry on state changes only — never on a clean pass, or a 2-minute trigger will flood the log.

**Distro name** is written into the task action at registration time. It cannot be discovered later: the name defaults to `kautuka`, is overridable with `--distro-name`, and survives only inside the Add/Remove Programs `UninstallString`. There is no durable machine-readable record of it.

**The keepalive is a `flock`, not a process count.** Measured: the appliance ships without `procps-ng`, so `pgrep`, `pkill` and `ps` do not exist, and matching on `/proc/*/cmdline` self-matches the `grep` doing the matching. `flock` *is* present (util-linux 2.41.5), so residency is held by `setsid flock -n /run/kautuka-keepalive.lock -c "sleep infinity"`. That makes ensure-exactly-one race-free rather than best-effort: a duplicate start cannot acquire the lock and exits immediately. The lock lives on tmpfs, so it clears whenever the distro restarts — correct semantics, since a keepalive lock must not outlive the instance it was holding up. Verified: probe returns 0 with no holder, 1 once held, and a second start does not duplicate.

**Elevation.** The installer stays `requireAdministrator`. `Kautuka.Monitor` is `asInvoker`, and the task principal is `RunLevel = Limited`, so a two-minute pass and a shortcut click do not prompt. The installer launches the tray through `explorer.exe` so the process does not inherit the installer's elevated token. Highest was the earlier plan only because the elevated installer was going to be the task executable.

**Uninstall ordering is load-bearing.** The task must be deleted, and its keepalive released, *before* `wsl --unregister`; otherwise the unregister can hang against the live `wsl.exe` session. The new step belongs between the current `wsl --unregister` call and `UnregisterWindowsAppliance()`, with the unregister moved after task removal.

## Host configuration drift

`%USERPROFILE%\.wslconfig` is the appliance's single largest uncontrolled dependency. Everything in `/etc/wsl.conf` ships inside the image and is ours; `.wslconfig` is machine-wide, user-editable, applies to *every* distro including `Podman-managed VM`, and a user who edits it for an unrelated reason silently reconfigures the appliance.

**The policy is detect and surface, never auto-correct.** Rewriting `.wslconfig` to suit the appliance would reach across into the user's other distros and their podman machine. An appliance that repairs itself by breaking someone's Fedora dev box is worse than one that explains itself.

Two keys are known to break the product outright:

| Key | Effect | Basis |
|---|---|---|
| `localhostForwarding=false` | `http://127.0.0.1:20229` stops resolving from Windows and the appliance becomes unreachable, even though it is healthy inside. WebHost binds `127.0.0.1:20229` *only* — measured — so forwarding is the sole path in. | Verified: bind address measured; the shortcut works today via forwarding |
| `networkingMode=mirrored` | The appliance becomes **entirely unreachable, including `20229`**, because the Hyper-V firewall's `DefaultInboundAction` is `Block`. Separately, it breaks the user's own `podman machine`, which cannot start because it relies on NAT port forwarding to reach its VM's sshd. The appliance's `eth0` also takes the host's LAN address. | **Measured in a private test environment; the address is omitted** — see [network exposure](appliance-hardening.md#mirrored-networking-is-worse-than-a-port-conflict). The earlier reasoning about port occupancy was wrong; the failure is at the Windows firewall layer, not at `bind()` |

Others degrade rather than break, and should be reported without blocking: `memory` set below the appliance's working set (the user manager alone peaked at 224 MB; the full appliance with sidecars is 1–2 GB), `processors=1`, `swap=0`, a custom `kernel` or `kernelModules` (podman depends on the shipped kernel's feature set), and `dnsTunneling` or `firewall` changes that can interfere with image pulls.

`vmIdleTimeout` is the one key that would bear directly on residency. On this development machine WSL is 2.9.13.0, not 2.9.4.0, and the setting name is present in `wslsettings.dll`. The idle-shutdown value was not measured, so the drift check does not evaluate the key.

**Mechanism.** Record a baseline hash of `.wslconfig` at install time. Each supervisor pass re-hashes; on change, parse and evaluate against the table above, and report only the keys that matter. Hash-only comparison keeps the common case cheap and avoids re-reporting formatting edits.

## Testing

Contract tests assert the generated task script — principal, logon type, trigger, and action arguments — and the drift evaluator. They do not register a live scheduled task.

## Decisions

**Settled.**

1. **Repetition interval — repeating, 2 minutes.** Boot-only is rejected: it cannot detect a mid-session `wsl --shutdown`, which is exactly the failure the supervisor exists to catch. A firing against an already-running distro costs a sub-second `wsl.exe` exec (measured 0.7–2.1 s), so the interval is bounded by politeness rather than cost. Two minutes caps user-visible downtime after a teardown while keeping the process-spawn rate modest; Task Scheduler's floor is 1 minute if that proves too slow.
2. **Action host — staged `Kautuka.Monitor.exe --supervise`.** A raw `wsl.exe` action cannot be idempotent, cannot detect drift, and cannot log. The elevated installer is the wrong host because its manifest cannot run unelevated.
3. **Recovering from a lapse — the desktop shortcut and the logon tray start `Kautuka.Monitor`.** The shortcut is `Kautuka.lnk`, not a `.url`. The window is a status card: up, stopped, or blocked, plus the last supervisor result and any breaking `.wslconfig` finding. Open launches the browser only after port 20229 answers. Restart runs one supervise pass. Quit closes the tray and leaves the task and the appliance running.
4. **Distro name is not an Add/Remove Programs field.** Settings shows `Kautuka WSL Appliance`. The monitor task, the logon Run value, and the uninstall command already carry `--distro-name`. On the published install of `the historical prerelease`, the current-user uninstall key `KautukaWSL` had that name only inside `UninstallString` and the install-location path. A separate ARP value is not required.
5. **One Windows account owns the appliance.** The task stays bound to the account that registered the distro. Other people use the browser. They reach it from the network only when the installer has forwarded a Private-network address. The test environment used a Public network profile, port 20229 listened on loopback only, and no port forward was configured. A second Windows account installing or sharing the appliance is not a supported path.
6. **No drift surface inside the appliance.** `.wslconfig` is outside the distro, so the appliance cannot report it. The host monitor already shows a breaking finding on the signed-in user's status card, and the supervisor writes the rolling log. Another surface is ignored for now.

## Work

**Landed, and proven with the published unsigned installer, including on Windows Home.** A clean install of `the historical prerelease` (Authenticode status `UNSIGNED`) was rebooted at 2026-10-02 22:31:40. At 22:32:11 the monitor task finished with result 0 and started the keepalive. `kautuka` was running, `http://127.0.0.1:20229` returned 200 through `wslrelay` on `127.0.0.1`, and no port forward was present. The tray started at 22:34:53 from the logon Run key. The same task was confirmed on Windows Home.

1. `Kautuka.Monitor.Core` owns one supervise pass: ensure-running, ensure-exactly-one keepalive via `flock`, `.wslconfig` drift evaluation, a rolling log, and exit codes 0 healthy, 1 cannot supervise, 2 a host setting is breaking the appliance. The installer `--supervise` verb calls that pass. The scheduled task calls `Kautuka.Monitor.exe --supervise`.
2. `WslConfigDrift` evaluates `.wslconfig` text. The host path is injectable. Contract tests cover mirrored networking, `localhostForwarding=false`, a commented `#networkingMode=mirrored`, a 512 MB memory finding, and the generated task script (`S4U`, `Limited`, `PT2M`, task path `\Kautuka\`).
3. Install registers the task (`AtStartup` plus a two-minute repeat, installing user, S4U, Limited), writes the logon Run value `Kautuka.Monitor`, replaces `Kautuka.url` with `Kautuka.lnk`, and starts the tray unelevated. Monitor is embedded in the installer and extracted beside it. Uninstall deletes the task, the Run value, and the tray, then `wsl --terminate`, then `wsl --unregister`.

The earlier live check of the flock protocol still stands: a stopped distro with no keepalive was restored, a second pass did not duplicate the keepalive, and the distro was still running after the supervisor process exited. A commented `#networkingMode=Mirrored` produced no finding.
