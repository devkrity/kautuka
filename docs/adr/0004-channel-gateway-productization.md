# ADR 0004: Channel Gateway productization gate

Status: Accepted - product-shaped subsystem, not a standalone product yet

## Decision

Maintain Channel Gateway as an independently buildable, publicly contracted Kautuka subsystem. Publish neither a separate commercial SKU nor a compatibility promise beyond the versioned packages until external demand and live-provider evidence exist.

## Evidence available

- Kautuka uses the same semantic and transport contracts for Slack and Telegram.
- Contracts and gRPC transport pack independently.
- A reference consumer builds without Kautuka Gateway or ChannelHost implementation dependencies. The process proof invokes the public Channel Gateway gRPC contract from an independent executable.
- Automated tests demonstrate deterministic rendering, budgets, security binding, durable retry, restart persistence, and fail-closed admission.

## Evidence not available

- No independent external adopter has operated the gateway.
- No support-cost or upgrade-compatibility history exists.
- CHG-019 WhatsApp admission is deferred by the product owner and may be revisited later. Kautuka requires every channel to use polling or an outbound-initiated persistent connection and permits no inbound webhook listener; no unofficial transport workaround is authorized.
- Live Slack and Telegram release-gate testing still depends on administrator-created provider applications.

## Consequence

Market it as part of Kautuka. The controlled external reference consumer proves technical interoperability over the public contract, but it is not an independent adopter or market-validation signal. Revisit extraction only after an independent adopter operates through the same public boundary, three admitted providers pass live release gates, and independent operation does not duplicate Kautuka administration, security, or observability.
