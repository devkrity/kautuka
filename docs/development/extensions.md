# Extending Kautuka

## Providers and models

Provider behavior is registered through `IAiClientProvider` implementations in Gateway dependency injection and described by the provider type catalog. Add the canonical provider ID once, define its default endpoint and credential requirement, implement validation/discovery/client creation, and cover save/test/discovery plus missing-credential behavior. API keys belong in CredentialsHost, not provider catalog JSON.

## Agent tools

Tools are exposed through `IAgentToolProvider` registrations. Tool execution must pass policy evaluation, retain exact call identity, produce bounded serializable results, and emit canonical lifecycle events. Host operations must go through Manager rather than invoking Podman/filesystem authority from Gateway.

## Agent as a tool

AAAT registration creates `invoke_agent_*` tools for explicitly exposed agents. Preserve the exact parent run/tool/root lineage and durable invocation aggregate. Leaf mode disallows AAAT recursion, not ordinary child tools or approvals.

## MCP bundles

MCP packages enter through the package service, catalog, desired-state publication, Manager materialization, and connector execution boundary. Validate package bounds and permissions before persistence. External images require immutable digests. First-boot infrastructure images must also be listed in `runtime-images.json` (signed via the platform release). User connector images stay digest-only until package admission. Credentials remain opaque references resolved at execution.

## Skills

Skills are imported/validated packages that become agent-visible manifests and tools. Keep instructions and executable assets bounded, versioned, and owned by the package. Default Skills and MCPs are product assets; they are not an internal model or built-in agent.

## Channels

Provider adapters live in ChannelHost and normalize transport events into shared channel contracts. New adapters must define durable replay/deduplication behavior, credential references through CredentialsHost, attachment lifecycle, outbound idempotency, and provider-specific limits before registration. They must use polling or an outbound-initiated persistent connection; inbound webhooks are not admitted. Complete the applicable [channel completion checklist](channel-verification.md) before marking adapter work done.
