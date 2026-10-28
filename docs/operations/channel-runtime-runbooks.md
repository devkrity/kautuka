# Channel runtime operational runbooks

Sanitized operator procedures for the polling-only channel runtime. Never paste secrets, provider bodies, or raw certificates into tickets or evidence.

## Certificate and signing-key rotation

1. Issue replacement Gateway/ChannelHost workload certificates with the documented overlap window.
2. Deploy current material; confirm readiness and a successful ChannelHost Ping.
3. After overlap expires, revoke previous leaves and confirm stale-leaf rejection.

## Provider credential rotation

1. Suspend the attachment from Settings.
2. Replace credentials through Gateway/CredentialsHost (never into ChannelHost state).
3. Recover, revalidate, and activate once. Confirm no webhook ingress path appears.

## Registration suspension

Suspend removes runtime access and blocks traffic while retaining audit evidence. Recovery returns inactive until owner revalidation succeeds.

## Replay denial and uncertain provider effect

Duplicate ingress must yield one canonical event. Outbound uncertain effects require reconciliation and must never auto-repeat with the same scoped identity.

## Stuck continuation

Inspect wait/approval/invocation stores for lease owner and expiry. Prefer first-valid resolution recovery over manual store edits. Capture seed, wait id, approval request id, and canonical sequence range before intervention.

## Failed migration / provider outage

Do not rewrite historical event meaning. Prefer fail-closed admission. For provider outages use bounded backoff and readiness differentiation between recoverable and reconciliation-required work.

## Simulator or live-test failure

Export redacted simulator evidence and owned-process records. Replay with `KAUTUKA_ACCEPTANCE_SEED` and the scenario grep from the evidence bundle. Do not promote simulator fixtures from untrusted live payloads.

## Evidence collection

Retain scenario id, seed, commit, configuration digest, canonical range, sanitized transcript hashes, service findings, and browser findings. Redact tokens and bodies before sharing.
