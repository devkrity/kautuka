# Models

A model entry binds a provider to the provider’s model name. Configure models in **Settings → Models** after the provider is saved.

## Setup

1. Select the provider.
2. Use model discovery when the provider supports it, or enter the exact model name.
3. Save the model.
4. Run **Check** to probe runtime capabilities.
5. Assign the saved model to an agent.

The capability result records observations such as streaming, tool calling, structured output, reasoning behavior, and model identity where the provider exposes them. Unknown capability data is not equivalent to support; check a model before relying on tools or structured output.

Deleting a model is blocked while an agent still references it. Changing a model does not automatically move existing agents to the replacement.

## Endpoints and verification

- List: `GET /api/settings/models`
- Discover: `POST /api/settings/models/discover`
- Probe before save: `POST /api/settings/models/probe-candidate`
- Save: `POST /api/settings/models/save`
- Check saved model: `POST /api/settings/models/{modelId}/probe`
- Delete: `DELETE /api/settings/models/{modelId}`

Verify that the model card shows capability results and that an agent using the model can complete a chat. For Ollama, first confirm the model is installed in Ollama and appears in `/api/tags`.
