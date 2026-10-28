# Security branch review — 2026-10-10

This is a historical source-review record, not a current security assessment. The original machine-specific test traces, local paths, request identifiers, and network details have been removed. Recheck each behavior against the current source before relying on these findings.

## Findings recorded at the time

1. **High — Shared-agent sandbox execution may mount a common parent of users' private folders.** The review recommended restricting mounts to the authorized user's workspace and proving isolation with two users.
2. **High — Attachment downloads may follow a linked parent directory.** The review recommended resolving and validating the final path before opening a file, with symlink and traversal regression cases.
3. **High — A rejected hard-linked overwrite may still truncate the target.** The review recommended opening safely, checking link identity before mutation, and proving the original target remains intact after rejection.
4. **Medium — Saving a user-owned connector may bypass the private-network probe restriction.** The review recommended applying the same outbound address policy to connector save and probe paths, including redirects and DNS resolution.

The original reproduction logs are not retained in this public engineering record. These items require a fresh code and test review before being described as open or fixed.
