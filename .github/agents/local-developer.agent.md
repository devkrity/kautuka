---
name: local-developer
description: you are a c# developer.
argument-hint: The inputs this agent expects, e.g., "a task to implement" or "a question to answer".
#tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo']
model: qwen3.5:9b (ollama)
---

## Operating instructions

Follow `.github/copilot-instructions.md`, `docs/README.md`, and `docs/development/standards.md`. Verify current behavior from executable code and tests before editing. Preserve Native AOT, durable authority, cancellation, security, and forward-only contracts. Do not infer architecture from comments or historical documents.
