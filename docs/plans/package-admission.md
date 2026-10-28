# Package admission and isolation

**Status:** Active

**Owner:** Runtime security

**Activation condition:** The vulnerability scanner, policy format, quarantine store, and promotion boundary are selected.

## Outcome

Downloaded packages, MCP materialization outputs, generated images, and executable dependencies cannot enter agent execution until an immutable digest passes owner-scoped admission policy.

First-boot Kautuka infrastructure images are **not** waiting on this scanner. Their trust root is already recorded: digest identity, ECDSA-signed `runtimeImages` allowlist, mirrored blobs. Cosign/Sigstore is not introduced as a second PKI for those images. This plan remains the admission path for **untrusted** user/catalog MCP packages.

## Work

1. Quarantine artifacts before execution.
2. Scan the resolved artifact and transitive contents.
3. Record policy decision, scanner evidence, owner, digest, and expiry.
4. Promote only approved immutable digests into contained runtimes.
5. Re-evaluate on policy/scanner changes and deny revoked artifacts.
6. Add negative and restart-safe tests across filesystem and network acquisition paths.
