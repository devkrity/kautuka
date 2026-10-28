# ADR 0001: Podman is the execution runtime

**Status:** Accepted

## Decision

Manager uses Podman for agent sandboxes and connector containers. Gateway never owns container commands directly.

## Evidence

`Kautuka.Manager.RuntimeContainer` implements the container tool, sandbox specifications, connector execution, image provenance, and reconciliation. Gateway reaches this boundary through the Manager protobuf client.

## Consequences

- Local/release environments require compatible Podman support.
- Manager owns path validation, non-root execution, capability removal, resource bounds, mounts, networking, image provenance, and orphan cleanup.
- Tests must validate generated container arguments and live convergence where platform support exists.
- Docker-specific control paths are not maintained.
