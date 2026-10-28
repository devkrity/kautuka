# Telegram agent routing

Kautuka uses one ordinary private Telegram chat for an account bot. Telegram topics are not part of the setup or routing model. Kautuka owns the selected agent, session bindings, queued prompts and approvals; Telegram provides the chat interface.

## Connect a bot

1. Create a bot with `@BotFather` and copy its token into Kautuka.
2. Open the bot's private chat and send `/start`.
3. Obtain the private chat ID using the official Telegram Bot API `getUpdates` response and enter it in Kautuka.
4. Select **Connect bot**. No group, forum, topic or Threaded Mode setup is required.

The persistent keyboard provides **Agents**, **Sessions**, **New session** and **Current context**. `/agents`, `/sessions`, `/new` and `/context` provide the same navigation for users who prefer commands. `/help` lists the other available commands.

Action cards are temporary controls, not chat history dashboards. Choosing an agent or session removes its picker and confirms the new context in chat. When an agent picker is mirrored to the linked Slack DM, choosing from either channel removes both pickers and invalidates the sibling buttons; stale cards also clean up their linked picker when clicked. Approval cards keep their request details and change to a final decision card with the action buttons removed. The **Queue** action shows waiting prompts across that bot only when at least two are waiting; each prompt has its own remove action, the card updates after a removal, and it disappears when the queue falls below two or after 90 seconds. These cards are not pinned.

## Destination rules

- The selected agent belongs to the Telegram user and remains selected until that user chooses another agent.
- Each agent retains its own last selected session. Switching agents does not change or merge its sessions.
- Starting a new session selects it for that agent.
- Background replies, approval events and Autopilot notifications do not change the selected destination.
- Every accepted prompt is bound to its agent and session at Gateway admission. A later switch cannot redirect a queued prompt.
- If a destination cannot be resolved, Kautuka asks the user to choose an agent or session; it does not infer a destination from a Telegram topic.
- The `Current context` action displays the selected agent and the selected session title, if one exists.

Agent messages use the configured agent icon and name with the session title as visual context. Agent icon and color are configurable in the agent's Persona settings. The browser uses the same identity in the agent heading and assistant turn cards. Telegram receives the configured icon as a Unicode character, but Telegram clients choose the glyph artwork, size and alignment, so it can look different from the browser. This is Telegram's emoji rendering behavior; Kautuka does not replace the selected icon. Agent color is retained for the browser UI and is not applied to Telegram messages.

## Existing Telegram topics

Kautuka no longer creates, renames, pins or routes through topics. During bot provisioning, ChannelHost attempts to delete only topic IDs that Kautuka previously recorded as managed. Successfully deleted IDs are cleared; failed deletions remain in a cleanup-only mapping and are retried during later provisioning. This does not delete Kautuka agents, sessions or history. Telegram does not provide Bot API topic enumeration, so manually created or otherwise unrecorded topics cannot be discovered or removed automatically.

Messages sent inside an old topic are not submitted as prompts. ChannelHost replies in the main chat with instructions to return there and select the intended agent. This prevents a stale topic from accidentally routing to the currently selected agent. Existing topic cards/history may remain in Telegram if Kautuka never recorded their IDs.

## Bot selection in agent settings

**Account bot** keeps all agents in the same private Telegram chat and lets the user switch agents with the keyboard. **Separate bot** sends that agent through its own bot chat; the user must start that bot in Telegram. Select Account bot for the single-chat experience.

## Ownership boundary

Gateway remains the authority for agent/session selection, prompt admission and queues, approvals, and deciding which semantic events should be delivered. ChannelHost translates Telegram updates and renders Gateway events using Telegram's API. Topic cleanup is a ChannelHost transport concern; it does not alter Kautuka state.
