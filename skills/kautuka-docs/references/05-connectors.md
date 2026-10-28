# Connectors (MCP)

Connectors expose MCP tools to agents. Manage them on `/connectors`, then grant them to individual agents in **Settings → Agents → Connectors**.

## Transport choices

- **HTTP** connects Gateway to a remote MCP URL.
- **Local command** accepts supported Node, Python, or uv commands and normalizes them into a Manager-managed container runtime.
- **Container** runs the declared immutable container image and command through Manager.
- **MCPB import** accepts one `.mcpb` bundle and creates or updates its containerized connector.
- JSON import accepts common MCP server entries; URL entries become HTTP and command entries become managed-container connectors.

Keep secret values in the connector credential editor, not in command arguments or URLs. Declared environment-key names describe what must be supplied; the connector projection does not return secret values.

## Workflow

1. Create or import the connector.
2. Add required credentials.
3. Use **Test**.
4. Refresh discovery and inspect the tool list.
5. Enable the connector and choose tool modes on the agent.

## Endpoints and verification

- List/detail/save/delete: `/api/connectors`, `/api/connectors/{connectorId}`
- Test: `POST /api/connectors/test`
- Discovery: `PUT /api/connectors/{connectorId}/discovery`
- MCPB import: `POST /api/connectors/import/mcpb`
- Credentials: `/api/credentials/connector/{connectorId}`

Verify that Test succeeds, discovered tools are present, and the agent’s effective tool policy exposes the expected tool. A saved connector is not automatically available to every agent.

## Microsoft 365 mail

The release appliance ships `kautuka-m365-mail` and `kautuka-google-mail`. An owner uses **Sign in** on that connector card. The owner does not register an app and does not paste a token. The release build sets `Kautuka:ConnectorAuth:Host` to `https://auth.kautuka.io`. That host inserts the client id and, for Google, the client secret. Hosted refresh for either mailbox is accepted only for the key registered at sign-in. The PKCE verifier stays on the appliance. The browser returns through `https://auth.kautuka.io/callback`, and the appliance redeems the code. The refresh token stays in CredentialsHost. The connector process receives only a short-lived access token.

A source checkout leaves that host unset. Sign-in then uses the developer's own OAuth app. Copy `client-id.example.txt` to `client-id.txt` beside the connector. Google also needs `client-secret.txt`. Register the redirect `http://127.0.0.1:<port>/api/connectors/oauth/callback` on that app, and open Kautuka at that same loopback address. Those files stay uncommitted. A checkout does not use Devkrity's client.

`mail_send` starts as **Ask me first** on each agent. An owner can change that tool to Allowed or Blocked. A later startup does not put it back. Disconnect deletes the stored mailbox sign-in and points the owner at Microsoft's revoke page.
