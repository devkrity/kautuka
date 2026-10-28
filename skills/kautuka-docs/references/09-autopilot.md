# Auto-Pilot

Auto-Pilot runs a selected agent or owned workflow on a recurring five-field cron schedule. Configure it on `/autopilot` or use a workflow's **Auto-Pilot** action.

## Schedule settings

- Target is an agent or workflow. Workflow start values are a JSON object validated against the workflow input and working schemas when saved and again when launched.
- Agent prompt context becomes the starting instruction. Workflow workspace file references are authorized against the first step agent's workspace; only that first step receives them.
- Cron is evaluated in the appliance machine’s local timezone.
- Mode is Off, On, or Needs approval.
- A pre-run approval can expire at the next occurrence, after a duration, or never.
- Overlap policy either skips and notifies or skips silently when the prior occurrence still runs.
- Notifications can go to the UI inbox and Telegram.
- Pause is separate from mode and preserves the schedule.

Workflow schedules use the same workflow instance runner as a manual run. The run record links to its workflow instance, follows its running and approval states, and reports the final output or error. Pre-run approval shows the workflow name, revision, and start values. Approval reminders match the linked instance id or a step attempt's run id or session id; tool type alone does not link an approval to a workflow run. A paused or approval-waiting workflow receives the reminder “Auto-Pilot workflow is still waiting for a decision.” A still-running workflow suppresses the due occurrence without a notification. If the workflow input declares `receivedAfter` and the schedule leaves it empty, each launch uses the scheduled time of the latest Completed or NoOp run, or the schedule creation time for its first run.

The **Read email attachments** template requires the first step agent to have an enabled Outlook or Gmail connector. Its `items` array contains JSON-encoded strings because the current workflow schema subset only supports arrays of scalar items. That same connector requirement is checked when saving and starting a workflow and when saving and launching a workflow schedule. `mail_list` accepts an optional ISO-8601 `receivedAfter` value; the connector converts it to the provider's received-time query. Microsoft Graph search keeps its date clause inside one quoted `$search` value and does not combine `$search` with `$filter` or `$orderby`; non-search inbox reads use inbox ordering. Reading or sending mail remains subject to the agent's configured tool approval policy.

The page also shows run history and the notification inbox. **Run now** triggers the saved schedule immediately; it does not alter the cron expression.

## Endpoints and verification

- Schedules: `/api/autopilot/schedules`, `/api/autopilot/schedules/{scheduleId}`
- Pause/resume/run-now: `POST .../{scheduleId}/pause`, `/resume`, `/run-now`
- Runs: `GET /api/autopilot/runs`
- Inbox/read: `GET /api/autopilot/inbox`, `POST /api/autopilot/inbox/{notificationId}/read`

Verify the calculated next run, then use Run now. Confirm a run record appears and, when enabled, a UI or Telegram notification is delivered. For Needs approval, confirm the pre-run request appears on `/approvals` before agent execution starts.
