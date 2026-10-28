# Channels

Channels let a user receive prompts, agent replies, notifications, and human-approval actions outside the web UI. Current ChannelHost providers are Telegram and Slack.

WhatsApp CHG-019 is deferred, not rejected. It remains unadmitted while Kautuka requires polling or an outbound-initiated persistent connection and permits no inbound webhooks. Revisit it only if an official polling-compatible transport is verified or the product owner reopens the decision; no unofficial WhatsApp Web or browser-automation transport is authorized.

## Where to configure

- A user configures their own channels from the profile dialog.
- An administrator can configure another user under **Settings → Users**.
- The onboarding wizard offers an optional Telegram setup.

Each channel can select a default target agent and a channel sync mode: **Approvals + final reply** (`hitl` — the originating user prompt, HITL cards with prompt/args context, and the terminal assistant reply) or **Mirror completed replies** (`mirror` — also completed assistant replies, the same user-visible run messages as the web UI). Both modes echo the user prompt so channel messages read in context. ChannelHost interprets `hitl_tether_mode` for delivery; Gateway stamps the mode for audit only. Providers never progressive-stream tokens.

## Telegram

1. Open Telegram's official [BotFather](https://t.me/BotFather), run `/newbot`, and copy the bot token it issues. Kautuka cannot create a Telegram bot on a user's behalf.
2. Open a private chat with the new bot and send it any message so Telegram creates the conversation.
3. Enter that private chat's numeric Telegram chat ID in Kautuka, paste the bot token, choose the default Kautuka agent, and select **Save & Connect**. Kautuka verifies the bot, removes any webhook, publishes its supported commands, and uses long polling for inbound messages.

For a private Telegram bot chat, the chat ID identifies the destination Kautuka is allowed to receive from and reply to. Kautuka never asks for a `/connect` command and does not recommend sending credentials to a third-party ID bot. **Save & Connect** validates the bot and polling setup; **Verify with Test Prompt** confirms delivery to the entered chat ID. Telegram's official bot-creation walkthrough is [Creating your first bot](https://core.telegram.org/bots/tutorial).

## Slack

You can finish Slack setup from **Profile → Channels**. Kautuka creates a ready-to-use app manifest link so you do not need to copy settings or configure a public webhook.

1. Select **Create Slack app**. Slack opens the new-app page with Kautuka's app settings filled in. Pick your workspace and select **Create**.
2. Paste the two tokens into Kautuka's single token box, one at a time. Kautuka recognizes which token you pasted and stores it in the matching secure slot:
   - The app-level token starts with `xapp-`. In Slack, open **Basic Information → App-Level Tokens → Generate Token** and add `connections:write`. That permission lets Kautuka request the private Socket Mode WebSocket URL.
   - The bot token starts with `xoxb-`. In Slack, select **Install to Workspace**, approve the request, then open **OAuth & Permissions** and copy **Bot User OAuth Token**. If Slack shows **Request to Install**, a workspace administrator must approve it first.
3. Kautuka checks the bot token with Slack and checks the app token by requesting a Socket Mode WebSocket URL. It then shows a six-digit code. Select **Open in Slack** and send that code as a direct message to the Kautuka bot. The code expires after five minutes. Kautuka links the sender and the direct message automatically; you do not need to find or type an ID.
4. When Kautuka says Slack is connected, select **Send test message**.

The app manifest enables Socket Mode, message events, interactive buttons, and Kautuka's namespaced slash commands, including `/kautuka-queue` and `/kautuka-sessions`. Socket Mode means Slack sends activity back through a connection Kautuka opens, so no public webhook address is needed. The manifest asks Slack for permissions (Slack calls these **scopes**) to read messages in conversations where the bot is present, send replies, inspect shared files, look up display names, use reactions, and receive slash commands. Kautuka checks the installed permissions during setup. If your workspace requires approval, ask its administrator to approve the installation before continuing.

Token rotation stays off for this setup. Slack's token-rotation feature issues access tokens that expire after 12 hours and need a refresh process; Kautuka's local setup uses the non-rotating `xoxb-` and `xapp-` token forms instead. If the pasted token starts with `xoxe.`, turn token rotation off in the Slack app settings and reinstall the app before retrying.


## Destinations and routing

Incoming conversation or thread identifiers can establish more specific routing. Channel availability does not affect web chat.

Telegram requires the configured private-chat ID. Slack learns your direct-message destination when you send the pairing code. The selected Kautuka agent and channel sync mode are still configurable in the profile dialog.

## Rotation and recovery

- Provider credentials are encrypted by CredentialsHost and are not stored in ChannelHost's attachment database. Kautuka does not display a saved token again.
- **Suspend** immediately stops new inbound and outbound work and releases runtime credentials while preserving the registration and audit evidence. Use it before planned maintenance or while investigating a suspected compromise; revoke a known-compromised provider token in the provider console immediately as well.
- **Start recovery** moves a suspended or failed channel into an inactive state. Kautuka does not reactivate it merely because a credential was saved: replace credentials if needed, then select **Save & Connect** so provider-native validation succeeds before the status returns to active.
- **Replace credentials** clears only secret-entry fields in the browser. Create or reset the credential in the provider's official console, paste the replacement, and run **Save & Connect**. Revoke the old token only after the replacement is confirmed working, unless the old token is already believed compromised.
- Resetting a Telegram bot token invalidates the old token but does not change the configured Telegram chat ID or Kautuka owner. Save the replacement token and test the channel.
- Reinstalling a Slack app can issue a new bot token. Paste the replacement token into Kautuka and select **Check tokens and connect** again.
- If a channel is believed compromised, revoke the provider token first, then suspend and recover its Kautuka registration. Never paste provider tokens into support messages or logs.

Self-service permanent deletion is intentionally not exposed in this release. A deleted attachment is terminal, while current provider attachment IDs are deterministic; allowing users to delete one would prevent reconnecting that provider slot safely. Operators can use the administrative lifecycle only when permanent retirement is intended. Restorable self-service removal requires generation-scoped attachment IDs or an equivalent tombstone-replacement design.

## Endpoints and verification

- Current user: `GET /api/settings/me/channels`, `PUT /api/settings/me/channels/{channelType}`, `POST /api/settings/me/channels/{channelType}/lifecycle`
- Administrator: `GET /api/settings/users/{userId}/channels`, `PUT /api/settings/users/{userId}/channels/{channelType}`
- Test: corresponding `.../{channelType}/hitl-test` endpoint; Slack setup also uses the authenticated pairing endpoints and a plain test-message endpoint

Verify by sending the test, then send a real inbound message and confirm it opens or continues the intended agent session. If only outbound delivery works, check that Telegram's configured chat ID exactly matches the private chat, Slack Socket Mode is enabled, then check ChannelHost health on Dashboard. Provider setup errors shown in the UI are deliberately sanitized; use service diagnostics for operator investigation without copying credentials into logs.
