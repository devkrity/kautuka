# Memory

Kautuka separates session history from durable memory facts. Session messages preserve a conversation; facts are compact, reusable items extracted after completed turns when the agent enables memory fact extraction.

## Configure and inspect

- Enable **Extract and persist memory facts after each run** in **Settings → Agents → Main**.
- Select an agent on `/memories` to view its facts and sessions.
- Edit a fact’s title, content, or category, or delete it.
- Open the source session when it still exists.
- Delete a session independently; a fact can remain and will show that its source session was deleted.
- Advanced memory settings control embeddings and the injected-memory token budget.

Facts include source, category, optional confidence, update time, and source session. Extraction is selective; a completed run does not guarantee a fact. Use explicit durable statements when continuity matters.

## Endpoints and verification

- Agent memories: `GET /api/memories/agents/{agentId}`
- Edit/delete fact: `PATCH /api/memories/{factId}`, `DELETE /api/memories/{factId}`
- Delete session: `DELETE /api/sessions/{sessionId}`
- Memory policy: `GET/PUT /api/settings/memory`

Verify by completing a run with an explicit lasting fact, checking `/memories`, then starting a later session and confirming the fact is injected when relevant.
