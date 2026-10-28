# Channel Gateway consumer contract

Status: Implemented contract; external productization not yet approved.

Channel Gateway accepts a provider-neutral, versioned contract from an authenticated runtime. A consumer does not receive Kautuka user authority merely by connecting. Every privileged call requires both an exact workload identity and a short-lived Gateway-issued delegation bound to the operation, request digest, tenant, owner, registration, conversation, session, nonce, and expiry.

## Packages

- `Kautuka.ChannelGateway.Contracts` contains semantic messages, reviewed templates, provider capability budgets, accumulated stream updates, and durable interaction documents.
- `Kautuka.ChannelGateway.Grpc` contains the transport API, typed capability catalogue, authorization envelope, and media streams.

The packages build and pack independently. Consumers should reference only these two Kautuka public assemblies and call Channel Gateway over the public gRPC contract. They should not depend on Gateway, ChannelHost implementation, persistence, credentials, or Kautuka domain types.

## Required consumer behavior

1. Negotiate only known contract versions and fail closed on unknown security-critical fields.
2. Use the Kautuka session ID as the stable conversation identity. Do not create a competing conversation or approval store.
3. Treat interaction and callback values as opaque. Never embed code, credentials, tenant authority, or provider secrets.
4. Send accumulated stream text with a stable stream ID and increasing sequence. Retries reuse the same idempotency key. ChannelHost durably maps the stream to one provider message.
5. Keep provider credentials in CredentialsHost. The consumer receives neither provider tokens nor resolved secret values.
6. Expect revocation, owner mismatch, expired delegation, replay, digest mismatch, and inactive registration to fail without partial delivery.

## Semantic vocabulary

The v1 vocabulary is header, section, fields, context, divider, public HTTPS image, table, and actions. Actions are button, select, or input intent. Provider renderers enforce the typed capability budget and deterministically degrade unsupported presentation. A required approval decision, security warning, required field, or terminal state must never be silently omitted.

Workflow review decisions update the original card using the current semantic message document version. The resolved card keeps its title, removes decision buttons, and shows one outcome sentence for approval, requested changes, stopping, or expiry.

## Files

File intent must carry an opaque asset ID, declared safe name/type/size, and the same owner and conversation bindings as the message. Bytes are fetched just in time over an authenticated bounded stream. Durable messages and logs must not contain file bytes, signed download URLs, or credentials. Until that byte-stream contract is available for a provider, its `files` capability remains false.

## Verification

The permanent contract suite covers serialization, native rendering, provider budgets, callback safety, typed capability publication, fail-closed provider admission, and durable stream identity across store recreation. Security and integration suites cover exact certificate identity, delegation validation, replay, ownership, revocation, durable outbox retry, and restart behavior. Live provider smoke tests remain a release-gate activity and require user-supplied test installations; they are not replaced by the reference consumer. Use the [channel completion checklist](../development/channel-verification.md) to record the automated contract/security pass separately from the full live-provider pass.
