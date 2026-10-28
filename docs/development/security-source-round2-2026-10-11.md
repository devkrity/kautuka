# Source security review — round 2 — 2026-10-11

This is a historical source-review record, not a current security assessment. Detailed workstation-specific WSL and container-network measurements, local paths, and live identifiers have been removed because they are not reproducible across installations.

## Findings recorded at the time

### R2-01 — Caller-dependent tool discovery may be cached across users

The review reported that shared-agent discovery and in-flight cache keys did not distinguish callers. It also reported that cached MCP tool hydration could skip ownership filtering. The potential impact was cross-user exposure of connector tool descriptions and schemas. Credential extraction or a model attack was not demonstrated. Recheck cache keys, ownership checks, and execution authorization in the current source.

### R2-02 — Case-variant skill identifiers may disrupt tool construction

The review reported that skill identifiers differing only by case could cause tool-construction failures for unrelated agents. Recheck identifier normalization and collision validation in the current source.

## Validation boundary

The original review included tests and environment-specific network probes. Their detailed traces are not retained here. The general deployment lesson is to treat host, WSL, container, and loopback reachability as distinct boundaries and validate them on the target installation.
