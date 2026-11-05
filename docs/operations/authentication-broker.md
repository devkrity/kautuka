# Authentication and mail broker

Kautuka uses an authentication broker to connect a local installation to Google and Microsoft. The broker provides social sign-in and separate mailbox connections. An installation can use the hosted Kautuka broker or an operator's own compatible broker; it does not need a public callback address.

## Two distinct permissions

| Purpose | What the user authorizes | Where the OAuth application is configured |
| --- | --- | --- |
| Social sign-in | The provider identity used to sign in or link an account. | Login client ID and secret on the broker; the matching public client ID in Kautuka. |
| Mail connection | Mailbox access for the Google Mail or Microsoft 365 Mail connector. | Mail client ID and secret on the broker. |

These are separate applications and consent flows. Signing in with a provider does not itself connect a mailbox. Provider client IDs are public identifiers; client secrets belong on the broker. Google and Microsoft may require app verification or approval for the permissions requested.

## How a connection works

```mermaid
sequenceDiagram
    participant B as Browser
    participant K as Kautuka Gateway
    participant A as Authentication broker
    participant P as Google or Microsoft
    B->>K: Start sign-in or mailbox connection
    K->>A: Create flow with signed installation proof
    K-->>B: Open broker handoff
    B->>A: Bind the original browser to the flow
    B->>P: Review and grant provider permissions
    P->>A: Return to the broker's fixed callback
    B->>K: Continue in the original Kautuka page
    K->>A: Retrieve the result with installation proof and PKCE
    A-->>K: Return the result for this flow
    K->>A: Acknowledge after durable local persistence
```

The browser and appliance can be on different machines, but the same browser must initiate and complete a flow. If the popup is blocked, the browser is changed, or the flow expires, start again. The broker does not send the provider result to an arbitrary callback chosen by the installation.

Kautuka creates a persistent installation key and registers its public key with the broker. Signed requests are bound to their method, destination and body, and protected against replay. Mail connections also have separate DPoP keys. Provider OAuth state and PKCE bind the authorization to the flow. The broker admits a verified mailbox before delivering its credential, and a connection generation prevents an in-flight refresh from reviving a disconnected connection.

An installation key identifies a key, not an official build or a trustworthy operator. Someone controlling an installation can run the open-source code or register another installation. Users should grant mailbox access only to an installation and operator they trust. Broker-side disconnect prevents further brokered refresh for that connection; it cannot recall access tokens already issued by the provider or data already copied.

## Mail account limits and errors

Each installation can connect up to five distinct active Google mail accounts and five distinct active Microsoft mail accounts. Social sign-ins do not consume these limits. An account connected by more than one local user occupies one provider-account slot until all of its connections are removed. Shared or delegated mailbox access is not automatically counted as a separate account.

There is also a rolling limit on new accounts. Reconnecting an account already counted in the window does not use another allowance, and disconnecting does not erase the history. The broker's IP and per-location limits protect starts, polling, registration, validation and refresh; users on a shared network can encounter the same limit. Follow the retry guidance if the service returns a rate-limit error.

If a user declines consent or a flow expires, start a new sign-in. If the provider grants fewer required mail permissions, the connection is rejected and the user must grant the required permissions in a new flow. An ambiguous provider exchange is not blindly retried with the same code. If a disconnect cannot finish, Kautuka blocks local use and retains the information required to retry removal. Use the provider's permissions page to revoke an existing provider grant when needed.

## Using a self-hosted broker

Deploy and configure the [kautuka-auth service](https://github.com/devkrity/kautuka-auth), its Durable Objects, rate-limit bindings, and separate Google and Microsoft OAuth applications. Set `BROKER_ORIGIN` to the broker's HTTPS origin and register its exact `/callback` URL with each provider application. The broker operator must protect its client secrets and encryption keys and back up the mailbox pseudonym key with the broker state. Replacing that key without a coordinated migration changes account identities and breaks quota accounting; retain old encryption keys while their envelopes remain in use.

In Kautuka, open **Settings → Advanced → Authentication**. Enter the broker HTTPS origin and the public Google and Microsoft **social sign-in** client IDs that match its login applications. A blank social client ID disables that provider's social sign-in. Mail IDs and secrets are configured on the broker, not in these fields.

Before switching origins, keep an active administrator with a local password and disconnect existing mail connections. Save the new configuration and restart Kautuka; reconnect mail accounts afterward. Kautuka fences new mail sign-ins and installation-key rotation while the change is pending, and the pending change can be canceled from the same settings screen. Installation identities and saved mail credentials are scoped to their broker origin.

The loopback-only, administrator-authorized `/api/internal/broker/rotate-installation-key` endpoint rotates an installation key while retaining its identity and quotas. If the key is irrecoverably lost, the installation must establish a new identity and reconnect its accounts. A claimed installation ID or provider login alone cannot recover the old identity.

## Outages and deployment

When the broker is unavailable, new social sign-ins and mail connections cannot complete, and brokered mail credentials cannot be refreshed. Restore the broker and retry; for an ambiguous provider operation, start a new authorization when prompted. Existing Kautuka app sessions are not automatically signed out by the broker protocol change.

This protocol is a coordinated breaking change. Deploying the new broker retires the old hosted routes, so older Kautuka versions cannot start or renew hosted sign-ins. Plan the Gateway release and broker rollout together and tell users that existing mail connections must be reconnected. Before production rollout, provision and back up the mailbox pseudonym key and verify Durable Object migrations and rate-limit bindings. The release follow-ups for real-provider partial consent, lost responses and upgrade from the previous release are recorded in the [implementation plan](broker-connection-controls-plan.md).
