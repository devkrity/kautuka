# Agent workspace

## UX intent

The left panel uses a tree so people can move between agents and their sessions without spending the main workspace width on navigation. The selected agent stays prominent in the conversation heading; its sessions scroll within a bounded area so a long history does not push other agents out of view. Files, configuration, and stats actions sit beside the selected agent because they apply to that agent. The conversation area stays focused on messages, approvals, and run activity.

Session origin labels distinguish how a session started, while an explicit date and time on every row avoids ambiguous Today/Earlier buckets.

## Session list

The workspace sidebar lists sessions for the selected agent in most recently updated order. Session titles display their origin as a compact label: Chat, Tool, Autopilot, Telegram, or Slack. The title prefix is removed from the visible title because the label carries that information. Session rows show the update date as `dd-MMM-yy` followed by the local time.

Channel-created Slack sessions use the Slack origin label. Gateway restores agent persona icon and color from the persisted agent definition when loading the agent registry.
