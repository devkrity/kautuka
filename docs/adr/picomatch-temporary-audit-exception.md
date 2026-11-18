# Temporary picomatch audit exception

Status: temporary; owner: Kautuka maintainers; expires 2026-12-19.

The frontend development toolchain depends on picomatch 4.x. GHSA-fc5q-39xc-vjpf affects releases through 4.0.7, and no fixed release was available when this exception was added. The production frontend does not import picomatch as application code.

The workspace resolves affected 4.x paths to 4.0.7 and applies the `lib/parse.js` change from the unmerged upstream proposal https://github.com/micromatch/picomatch/pull/267 (commit d9a3f12faf186860cfc86c2834c41f38701152f2). This is a proposed fix, not an upstream release. `scripts/test-picomatch-patch.mjs` runs advisory patterns with a child-process timeout and checks representative matching behaviour. Its negative control against the unpatched npm package timed out.

The audit gate accepts only this advisory in the frontend workspace when every reported affected instance is development-only version 4.0.7, the patch and lockfile match the reviewed SHA-256, and the regression test passes. Other findings continue to fail. Replace the patch and exception with the official fixed release when available, or reassess before the expiry date.
