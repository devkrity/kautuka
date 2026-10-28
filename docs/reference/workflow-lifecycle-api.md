# Workflow lifecycle API

Workflow definitions are owner-scoped. `GET /api/workflows` returns active definitions; `GET /api/workflows?archived=true` returns archived definitions. Each definition includes `archived` and `hasRuns` state for the workflow list.

## Archive and restore

- `POST /api/workflows/{workflowId}/archive` archives a definition without changing its runs.
- `POST /api/workflows/{workflowId}/unarchive` restores it to the active list.
- Every Gateway start path rejects an archived definition with `409` and `This workflow is archived. Restore it to run it.`
- Archiving a workflow with a `running` or `paused` instance returns `409` with `This workflow has a run in progress. Stop it first.` Stop the run before archiving.

Archived definitions remain available through `GET /api/workflows/{workflowId}` and their existing `/instances` routes. Their runs stay in the workflow store and can still be inspected.

## Delete

`DELETE /api/workflows/{workflowId}` permanently removes a workflow only when it has no instances. If it has ever been run, the API returns `409` with `This workflow has runs, so it can't be deleted. Archive it instead.`

## Step review and retries

When a step pauses for review, resolve its pending approval with `approve`, `request_changes`, or `stop_run`. For `request_changes`, put the owner's note in `reason`. Each step allows up to two change requests; after that, approve or stop the run. A structured-output retry uses the step's `maxRetries` separately and does not spend a change request.

The Gateway rejects a review decision that is not among that review's allowed actions and leaves the review open. A change request received after the allowance is exhausted is rejected the same way, so it cannot close the review or leave the run paused without a way to resolve it.

The next attempt includes the latest earlier review note in its prompt. Workflow instance responses include that note as the attempt's computed `reviewNote`, so the run page can show what the owner asked to change.

## Test a saved step

`POST /api/workflows/{workflowId}/steps/{nodeId}/test` accepts `{ "input": { ... } }` and tests a step from the saved workflow definition. Inputs can include Start fields and results from earlier steps; the step's own results are produced by the test. It returns `results`, `droppedKeys`, or a plain `error`. The editor saves pending changes before it sends a test and asks for values using the field's type and pick-list answers. Empty values are omitted.

This call does not create a workflow run or workflow journal entry. It uses a temporary agent session that is deleted when the test ends, and memory writes are disabled. Every tool call requires approval during a test. If a step asks to use a tool, the Gateway stops it before the tool runs and returns `This step wants to use a tool (<tool name>). Tests stop before any tool runs.` The tool name is shown as supplied by the tool catalogue. Run the workflow to use tools or test steps with side effects.

